# 03 — API and examples (dev side first)

Two apps show that nothing is pharmacy-specific:

- **YFex Farma** — the chain **FarmaViva** (tenant `farmaviva`, realm `scope:0=farmaviva`), store **Loja Centro**
  (`0=farmaviva/1=loja-centro`), checkouts CAIXA-01/02, manager PC-GERENTE (installations 03). People: **Ana** (cashier), **Bruno**
  (store manager), **Rita** (support at the vendor).
- **YFex Marketplace** — shoppers in one shared realm (`shared:marketplace`). Person: **Carla**.

## 1. Central API (YFex Farma) — composition root

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddYFexCryptography(c => c
    .KeyRing("authentication/tokens",  r => r.For(KeyPurpose.Signing).Rotate(TimeSpan.FromDays(30)))       // JWT ES256 + JWKS
    .KeyRing("authentication/codes",   r => r.For(KeyPurpose.MessageAuthentication))                        // code MACs, links, flow tokens
    .KeyRing("authentication/pepper",  r => r.For(KeyPurpose.MessageAuthentication).NeverExpire()));        // password pepper (04 §2)

builder.Services.AddYFexAuthorization(a => a.UseRolePermissions(FarmaRoles.Map));
builder.Services.AddYFexAuthentication(auth => auth
    .Realms(r => r.PerScopeLevel(0))                                   // staff accounts per chain (⚑AN4)
    .Passwords(p => p.MinimumLength(10).BlockCommonPasswords())        // NIST shape (⚑AN22)
    .OperatorCredentials(o => o.PinLength(4, 6))                       // operator code + PIN for checkouts (04 §4)
    .Codes(c => c.Channels(DeliveryChannel.Email, DeliveryChannel.Sms, DeliveryChannel.WhatsApp))
    .ExternalProviders(p => p
        .Google(g => g.ClientId("…apps.googleusercontent.com").ClientSecret(secretName: "google"))
        .Microsoft(m => m.ClientId("…").Audience(MicrosoftAudience.Organizations))   // any work account
        .PerScope())                                                   // + tenants' own Entra/Keycloak from the directory (⚑AN9)
    .Tokens(t => t.Issuer("https://api.yfexfarma.com.br").Audience("yfex-farma").AccessTokenLifetime(TimeSpan.FromMinutes(10)))
    .SecondFactor(m => m.RequireFor(FarmaRoles.Manager, FarmaRoles.Administrator))   // 09 §3
    .UseRealmFromTenant());                                             // realm from the Tenancy resolution of the request

builder.Services.AddYFexAuthenticationDelivery(d => d
    .Email(e => e.Smtp("smtp.farmaviva.com.br", 587).From("nao-responda@farmaviva.com.br"))
    .Sms(s => s.Twilio())                                               // credentials per scope from YFex.Web endpoints "authentication.twilio"
    .WhatsApp(w => w.MetaCloudApi(template: "codigo_acesso")));         // authentication template approved in WhatsApp Manager
builder.Services.AddYFexAuthenticationData(o => o.ConnectionName("central"));    // YFex.Data SQL stores (12)
builder.Services.AddYFexAuthenticationAspNetCore();                     // cookie + bearer handlers over the YFex validator
builder.Services.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().WithTenant()))
                .AddYFexAuthenticationRpc();                            // ICallTokenValidator (07 §8)

var app = builder.Build();
app.UseYFexTenancy();                 // resolves the tenant → realm
app.UseAuthentication();              // our handlers
app.UseYFexAuthorization();
app.MapYFexAuthentication("/auth");   // every endpoint in 07 §9, rate-limited (11 §2)
app.MapYFexJsonWebKeys();             // /.well-known/jwks.json
app.Run();
```

No permission, role or tenant configuration moved: Authorization and Tenancy are configured exactly as today. The only change for
them is that the `ClaimsPrincipal` now comes from `YFex` handlers instead of a third-party JwtBearer (⚑AN2).

## 2. Ana signs in at CAIXA-02 (desktop POS)

```csharp
// CAIXA-02 composition root (Avalonia)
services.AddYFexAuthenticationClient(c => c
    .Server(new Uri("https://api.yfexfarma.com.br/auth/"))
    .Realm(AccountRealm.ForScope("0=farmaviva"))                 // the installation's tenant (LocalInstallation.Scope)
    .BindToInstallation()                                         // refresh only from this installation (⚑AN16)
    .SharedTerminal(TimeSpan.FromMinutes(5)));                    // 10 §2: idle lock, no stored refresh token (a back-office PC uses .StoreTokensSealed())
services.AddYFexAuthenticationOffline();                          // cached verifiers for ⚑O12 (10 §3)
services.AddYFexMessagingRpcClient(rpc => rpc.CallContext(ctx => ctx.SendToken(AuthenticationClient.RpcToken)));   // per-call token

// Login screen view-model — one call per method, one exhaustive switch
SignInResult result = await signIn.WithOperatorPinAsync(operatorCode: "0142", pin: pinBox.Secret, ct);

switch (result)
{
    case SignedIn s:                    shell.Start(s.Session); break;      // client already called AmbientPrincipalAccessor.SignIn + RPC reconnect
    case SecondFactorRequired f:        await secondFactorDialog.ShowAsync(f); break;
    case PasswordChangeRequired p:      await changePassword.ShowAsync(p.Flow, p.Reason); break;
    case ContactVerificationRequired v: await verifyContact.ShowAsync(v); break;
    case LockedOut l:                   message.Show($"Bloqueado por {l.RetryAfter:mm\\:ss}"); break;
    case NotAvailableOffline:           message.Show("Primeiro acesso neste caixa precisa de conexão"); break;
    case SignInRejected r:              message.Show(r.Reason == SignInRejection.InvalidCredentials ? "Código ou PIN inválidos" : "Acesso indisponível"); break;
}
```

What happened underneath:

1. `POST /auth/sign-in/operator` with realm, code, PIN, installation assertion header (Installations.Rpc).
2. Central throttles (account+installation, 11 §2), verifies the PIN verifier, checks the account status, opens a session
   (`OperatorPin`, `SingleFactor`, `installation = CAIXA-02`), issues access token (10 min) + refresh token bound to CAIXA-02.
3. The client keeps the refresh token (sealed, except on shared terminals), calls `AmbientPrincipalAccessor.SignIn(principal,
   accessToken)` (Authorization); Messaging.Rpc sends the current token with every call (`SendToken`). `ActorContext.Session` now
   names Ana and her session.
4. A PIN is not kept on the till (⚑O12: a 4-digit verifier falls to brute force): offline, Ana signs in at the site broker with the
   site verifier central publishes for her (`ISiteCredentialSource`, Offline Phase 4). A password sign-in instead leaves a terminal
   verifier through `YFex.Authentication.Offline` (10 §3). *(Corrected 2026-10-19: earlier text had central send the PIN verifier.)*

Bruno (manager) uses **password + authenticator app**; Rita (vendor support) uses Google with the vendor's Workspace. Same screen,
other calls: `WithPasswordAsync(email, password)`, `WithProviderAsync(IdentityProviderName.Google)` (system browser + loopback,
10 §1). A second factor is `CompleteSecondFactorAsync(flow, AuthenticationMethod.AuthenticatorApp, code)`.

## 3. Carla signs up on the marketplace with Google, then verifies WhatsApp

```csharp
// Marketplace web (server-rendered Blazor or Razor) — cookie session
builder.Services.AddYFexAuthentication(auth => auth
    .Realms(r => r.Shared("marketplace"))
    .Registration(r => r.SelfService().RequireVerified(ContactRequirement.PhoneOrEmail))     // 08 §1
    .ExternalProviders(p => p.Google(…).Apple(…))
    .Codes(c => c.Channels(DeliveryChannel.WhatsApp, DeliveryChannel.Sms, DeliveryChannel.Email).SignInWithCode()));  // passwordless
builder.Services.AddYFexAuthenticationAspNetCore(o => o.Cookies());

app.MapYFexAuthentication("/auth", e => e.ExternalCallbacks().CodeSignIn().Registration());
```

```text
GET  /auth/external/google/start?returnUrl=/checkout          → 302 accounts.google.com (state, nonce, PKCE S256)
GET  /auth/external/google/callback?code=…&state=…&iss=…      → ExternalSignInResult
       new Google subject, e-mail carla@gmail.com verified by Google, no account → RegistrationResult.Created (realm marketplace)
       policy RequireVerified(PhoneOrEmail) satisfied by Google's verified e-mail → SignedIn → cookie → 302 /checkout
POST /auth/contacts/phone/start  { "phone": "+5511987654321", "channel": "whatsapp" }   (signed-in, to receive order updates)
       → WhatsApp authentication template "123456 é o seu código…" with a copy-code button
POST /auth/contacts/phone/complete { "challenge": "…", "code": "123456" } → ContactVerified
```

Next week Carla's laptop is gone; on her phone she chooses **"entrar com código"**: `POST /auth/sign-in/code/start` with her
phone → WhatsApp code → `POST /auth/sign-in/code/complete` → same `AccountId`, method `WhatsAppCode`.

## 4. Password reset (Bruno forgot his)

```csharp
// API surface (the endpoints call the same service)
ChallengeStartResult started = await passwords.StartResetAsync(realm, identifier: "bruno@farmaviva.com.br", ct);
// always "Sent(MaskedDestination?)" to the caller — even for an unknown e-mail (no enumeration, 08 §3)

RecoveryResult done = await passwords.CompleteResetAsync(started.Challenge, code: VerificationCode.Parse("482913"), newPassword, ct);
// PasswordReset: SecurityStamp bumped → every session and refresh family of Bruno revoked → IAuthorizationSignals.RevokedAsync
// + "Sua senha foi alterada" sent to his other verified contact
```

If Bruno has a second factor, the reset **does not remove it**: after the code he must still pass his authenticator app (or a recovery
code) before choosing a password, unless an administrator reset his factors with an approval (08 §5).

## 5. Step-up before a sensitive command

```csharp
// Cqrs configuration (Authorization R1–R3, built 2026-10-19)
b.Command<ChangePriceTable, Unit>()
    .RequirePermission(PricingPermissions.ChangeTable)
    .RequireRecentAuthentication(TimeSpan.FromMinutes(5), AssuranceLevel.MultiFactor);

// client: the command comes back as ErrorKind.ReauthenticationRequired (HTTP: 401 + RFC 9470 challenge)
if (error.TryGetValue(out ReauthenticationRequired needed))
{
    StepUpResult step = await auth.StepUpAsync(AuthenticationMethod.Password, password, needed.Requirement.Minimum, ct: ct);
    if (step.Value is SecondFactorRequired more)                                  // the password alone is single factor
        step = await auth.StepUpAsync(AuthenticationMethod.AuthenticatorApp, code, needed.Requirement.Minimum, more.Flow, ct: ct);
    // SteppedUp: same session, new auth_time/acr and access token
}
await bus.CommandAsync<ChangePriceTable, Unit>(command);
```

## 6. FarmaViva brings its own Entra ID (per-tenant SSO, ⚑AN9)

```csharp
// Admin screen at central (permission authentication.providers.configure), stored per scope
await providers.ConfigureAsync(new ScopedIdentityProvider(
    Scope: "0=farmaviva",
    Name: IdentityProviderName.Parse("farmaviva-entra"),
    Kind: IdentityProviderKind.OpenIdConnect,
    Authority: new Uri("https://login.microsoftonline.com/7f0c…/v2.0"),
    ClientId: "…", ClientSecret: SecretReference.Sealed("…"),
    Linking: ExternalLinking.ByVerifiedDomain("farmaviva.com.br"),     // 06 §5
    Enforcement: ProviderEnforcement.Required), ct);                    // passwords off for @farmaviva.com.br accounts (except operator PINs)
```

From now on the FarmaViva sign-in page shows "Entrar com a conta FarmaViva"; checkouts still accept operator code + PIN.

## 7. Tests

```csharp
var harness = new AuthenticationHarness(realm: AccountRealm.Shared("tests"));       // in-memory stores, fake clock, capturing sender
var carla = await harness.CreateAccountAsync(email: "carla@example.com", password: "correct horse battery");

var started = await harness.CodeSignIn.StartAsync(carla.Email, DeliveryChannel.Email);
var code = harness.Sender.LastCodeTo(carla.Email);                                   // reads the captured message
var result = await harness.CodeSignIn.CompleteAsync(started.Challenge, code);
result.ShouldBeSignedIn().Session.Methods.ShouldContain(AuthenticationMethod.EmailCode);

harness.Clock.Advance(TimeSpan.FromMinutes(11));
(await harness.CodeSignIn.CompleteAsync(started.Challenge, code)).ShouldBeRejected(SignInRejection.FlowExpired);

var google = harness.Providers.Fake(IdentityProviderName.Google);                   // TestIdentityProvider: in-process OIDC issuer
var external = await harness.SignInExternalAsync(google.User(subject: "g-1", email: "carla@example.com", emailVerified: true));
```
