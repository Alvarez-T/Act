# 06 — External identity providers (Google, Microsoft, Apple, gov.br, any OIDC)

> **As built (AN-P3a, 2026-10-18):** package `YFex.Authentication.OpenIdConnect` — `ExternalSignInFlow`, `IdTokenValidator`,
> `ProviderKeySet` (RSA/EC JWKs through YFex.Cryptography C1), Google/Microsoft/Apple/GitHub/generic builders, `MapYFexExternalSignIn`;
> engine `ExternalSignInService` (§5) and `IExternalLoginStore`. **AN-P3b (2026-10-18):** per-scope providers (§6: `ScopedProviderRecord`,
> `IScopedProviderStore`, `IdentityProviderDirectory`), `.Data` tables, gov.br (§3.4, `.GovBr`), `ContactVerificationRequired` and
> `ProviderEnforcement.Required` (§5), the desktop/mobile handoff (§2.2), sealed subjects and pending flows. Log: 14 §5. The descriptor
> below is `IdentityProviderOptions` in code.

## 1. One generic client, small descriptors

`YFex.Authentication.OpenIdConnect` is an **OAuth 2.0 / OpenID Connect client** (relying party). Each provider is a descriptor plus,
where the provider deviates from the standard, a small hook. Nothing provider-specific leaks into the engine: the engine receives
an `ExternalIdentity` and decides sign-in, sign-up or linking (§5).

```csharp
public sealed record IdentityProviderDescriptor(
    IdentityProviderName Name, string DisplayName, IdentityProviderKind Kind,     // OpenIdConnect | OAuth2 (no ID token)
    Uri Authority,                         // discovery at {Authority}/.well-known/openid-configuration
    ProviderClient Client,                 // client id + secret reference (YFex.Web sealed secret) or private key reference (Apple)
    ImmutableArray<string> Scopes,         // "openid email profile"
    IExternalClaimsMapper Mapper,          // provider rules → ExternalIdentity (email trust, display name, assurance hint)
    IssuerRule Issuer,                     // exact, or tenant template (Microsoft)
    ProviderEnforcement Enforcement = ProviderEnforcement.Optional);

public sealed record ExternalIdentity(
    ExternalAccountKey Key,                // (provider, issuer, subject) — the only linking key
    EmailAddress? Email, bool EmailTrusted,// trusted = the provider is authoritative for this address (§5)
    PhoneNumber? Phone, bool PhoneTrusted,
    string? DisplayName, string? Culture,
    AssuranceLevel? AssuranceHint,         // from acr/amr when the provider states MFA (Entra "mfa" amr, gov.br levels)
    DateTimeOffset AuthenticatedAt);
```

The raw ID token and userinfo are **not stored** (personal data, no use after mapping); `ExternalAccountKey` and the mapped,
verified contacts are.

## 2. Flows

### 2.1 Web (server is the client) — authorization code + PKCE

```
browser                          our server (/auth)                                      provider
  │ GET external/{p}/start?returnUrl  │                                                      │
  │──────────────────────────────────►│ state = MessageToken(flow id, realm, returnUrl,      │
  │                                   │         nonce, PKCE verifier hash, expiry 10 min)    │
  │◄───── 302 authorize?client_id&redirect_uri&scope&state&nonce&code_challenge(S256) ───────►│
  │                                                                                          │ user signs in
  │ GET external/{p}/callback?code&state&iss  ◄──────────────────────────────────────────────│
  │──────────────────────────────────►│ check state (MAC, expiry, single use, same browser    │
  │                                   │   cookie), check iss == discovery issuer (RFC 9207)   │
  │                                   │── POST token (code, verifier, client auth) ──────────►│
  │                                   │◄── id_token (+ access token) ─────────────────────────│
  │                                   │ validate id_token (§4) → ExternalIdentity → §5       │
  │◄──── 302 returnUrl (allow-listed relative path) + session cookie ─────────────────────────│
```

- **PKCE even for confidential clients** (OAuth 2.0 Security BCP, RFC 9700): protects against code injection.
- `returnUrl` must be a **relative path** of the same origin or an exact allow-listed absolute URI; anything else falls back to `/`
  (open redirect, 11 §1).
- The `state` cookie (`__Host-yfex.flow`, SameSite=Lax, 10 min) ties the callback to the browser that started it (login CSRF).
- `response_mode=form_post` is supported for providers that require it (Apple with name/e-mail scopes).

### 2.2 Desktop and mobile apps — our server stays the client (RFC 8252 shape)

```
CAIXA app                 system browser                our server                         provider
  │ PKCE v2, loopback http://127.0.0.1:{port}/cb          │                                   │
  │── open /auth/external/google/start?client=desktop&redirect=loopback&code_challenge=… ──►│
  │                           │  …same web flow as 2.1 with the provider…  ◄──────────────►│
  │                           │◄── 302 http://127.0.0.1:{port}/cb?handoff=…  ───────────────│
  │◄── loopback listener receives the one-time handoff code                                  │
  │── POST /auth/external/handoff { handoff, code_verifier } ─────────►│ → SignInResult + tokens
```

- Provider secrets and per-tenant providers stay on the server; the app registers **no** client at Google or Microsoft.
- The loopback listener (`HttpListener` on `127.0.0.1`, random port, one request, 5 min) is BCL on Windows/Linux/macOS; mobile uses a
  claimed HTTPS redirect (App Links / Universal Links) instead. Embedded web views are **not** used (providers block them, and the
  app could read the password).
- The handoff code is single-use, 60 s, bound to the app's PKCE challenge, so another local process that sees the redirect cannot
  redeem it.

**As built (P3b) — the wire shape P4's client uses:**

```text
GET  /auth/external/{provider}/start?realm=…&client=desktop|mobile&redirect_uri=http://127.0.0.1:{port}/cb
         &code_challenge=BASE64URL(SHA256(verifier))&code_challenge_method=S256[&scope=…][&returnUrl=…]
     → 302 to the provider (400 when the redirect or challenge is not acceptable)
GET  /auth/external/{provider}/callback (system browser)
     → 302 {redirect_uri}?handoff=<sealed, 60 s, single use>        (no session cookie)
     → 302 {redirect_uri}?authentication=rejected&provider=…&reason=… (failure)
POST /auth/external/handoff  { "handoff": "…", "codeVerifier": "…", "client": "desktop" }
     → the same JSON as every sign-in: signedIn (+ accessToken, refreshToken), linkRequired, contactVerificationRequired, rejected
```

Accepted redirects: loopback **IP literals** (`127.0.0.1`, `[::1]`, any port — not `localhost`, which can be re-pointed) or exact
`AllowAppRedirect(uri)` entries (claimed https links, private-use schemes). The installation the redeeming request proves binds the
refresh token.

### 2.3 Native provider SDK buttons — ID-token exchange

Android "Sign in with Google" (Credential Manager) and "Sign in with Apple" on iOS hand the app an **ID token**. The app sends it to
`POST /auth/external/{provider}/id-token { id_token, nonce }`; the server validates it exactly like §4 with the **mobile client id**
as audience and the nonce it issued before. No code exchange.

## 3. Built-in providers

### 3.1 Google

| Item | Value / rule |
|---|---|
| discovery | `https://accounts.google.com/.well-known/openid-configuration` |
| issuer | `https://accounts.google.com` (also accept `accounts.google.com`, which Google documents for older tokens) |
| key | `sub` |
| e-mail trust | `email_verified = true` **and** (address ends in `@gmail.com` **or** the token has `hd` equal to the address's domain — a Google Workspace domain is authoritative for its own users) |
| extras | `hd` (Workspace domain) can be required per scope (`RequireHostedDomain("farmaviva.com.br")`) |

### 3.2 Microsoft (Entra ID + personal accounts)

| Item | Value / rule |
|---|---|
| authority | `https://login.microsoftonline.com/{tenant}/v2.0`, `tenant` = `common`, `organizations`, `consumers` or a tenant id (`MicrosoftAudience`) |
| issuer | **template** `https://login.microsoftonline.com/{tid}/v2.0` with `{tid}` taken from the token's `tid` claim; with a fixed tenant id, exact match. Optional allow-list of tenant ids |
| key | (issuer with `tid`, `sub`) — `sub` is pairwise per application, stable for this app. `oid` is recorded as a hint, not the key |
| e-mail trust | **not trusted by default.** Entra `email` is an optional, admin-editable attribute: trusting it lets a user of any tenant claim any address ("nOAuth"). Trusted only when the optional claim `xms_edov = true` (e-mail domain owner verified) is present, or the scope configured `ByVerifiedDomain(...)` for its own tenant id |
| personal accounts | `tid = 9188040d-6c67-4c5b-b112-36a304b66dad` (consumers); e-mail trusted as for Google's `email_verified` only when the claim says verified |
| MFA hint | `amr` contains `mfa` → `AssuranceHint = MultiFactor` |

### 3.3 Apple

| Item | Value / rule |
|---|---|
| issuer | `https://appleid.apple.com` |
| client authentication | `client_secret` = a short JWT **signed ES256** with the developer's `.p8` key — signed through YFex.Cryptography (`ISigningKey` imported into a signing ring; P-256 is already supported) |
| response | `form_post` when `name email` scopes are asked; the person's **name arrives only on the first authorization** (form field `user`) → captured into `DisplayName` at sign-up |
| e-mail | may be a private relay (`is_private_email = true`) — trusted (Apple verified it) but marked relay; messages to it are forwarded by Apple |

### 3.4 gov.br (Brazil)

| Item | Value / rule |
|---|---|
| authority | `https://sso.acesso.gov.br` (staging `https://sso.staging.acesso.gov.br`), PKCE required, client registered by the government |
| key | `sub` = **the person's CPF**. The key is therefore personal data: stored as a keyed hash for lookup + sealed for display (12 §3), `[Sensitive(PersonalIdentifier)]` |
| assurance | account levels (bronze / prata / ouro) come from gov.br's reliability information; mapped to `AssuranceHint` (bronze → `SingleFactor`, prata/ouro → `MultiFactor`) — **to confirm with a real client registration** (README §8) |
| use | apps that must prove *which citizen* (health, pharmacy programs) can require `gov.br` for specific operations as a step-up method (09 §4) |

### 3.5 GitHub (OAuth 2 only) and generic OIDC

- GitHub has no ID token: the mapper calls `GET /user` and `GET /user/emails` (YFex.Web) and trusts only `verified: true` primary
  e-mails. Key = numeric user id (not the login, which can be renamed).
- **Generic OIDC** (Keycloak, Auth0, Okta, Entra single tenant, Google Workspace with a fixed `hd`): discovery + standard claims;
  e-mail trust is a per-provider setting (`TrustEmail.Never | IfVerifiedClaim | ForDomains(...)`), default `Never`.

**As built (P3b), gov.br:** `.GovBr(g => g.ClientId(…).ClientSecretFromStore(…))` — subject must be a valid CPF; e-mail/phone trusted only
with `email_verified`/`phone_number_verified`; auto-link off; token endpoint with HTTP Basic; scopes `openid email phone profile
govbr_confiabilidades`; reliability levels read after the code exchange (`ReliabilityLevels(false)` turns it off). Still **to confirm with
a real registration**.

## 4. ID-token validation (hot enough to matter, security-critical)

1. Split the compact JWS into three spans; `Base64Url.DecodeFromChars` header and payload into stack buffers (≤ 4 KB, else pooled).
2. Header: `alg` must be the **algorithm of the key** selected by `kid` from the provider's JWKS (RS256, ES256, PS256 only; `none`,
   `HS*` and anything unknown → reject). Never pick the verification algorithm from the header alone.
3. Signature through YFex.Cryptography (`SignatureVerifier`; RSA verify-only for external keys = request ⚑AN11).
4. Claims with `Utf8JsonReader` (no DOM, no reflection): `iss` (rule §3), `aud` contains our client id (and `azp` equals it when
   `aud` has several values), `exp`/`nbf`/`iat` against `TimeProvider` with 2 min skew, `nonce` equals the flow's nonce, `auth_time`
   when `max_age` was requested.
5. JWKS: fetched through YFex.Web, cached by `kid`; an unknown `kid` triggers **one** refresh per provider per 5 minutes (rotation
   without letting garbage `kid`s hammer the provider).

## 5. Sign-in, sign-up and linking (⚑AN19)

```
ExternalIdentity arrives
  ├─ ExternalAccountKey already linked to an account in this realm → sign in that account (status checks, MFA policy)
  ├─ not linked, signed-in user is linking from the account page → link (step-up within 5 min required)
  ├─ not linked, EmailTrusted and an account in the realm has that VERIFIED e-mail
  │     ├─ realm policy AutoLinkTrustedEmail (default ON for Google/Apple-style trust, OFF for generic OIDC) → link + sign in
  │     └─ otherwise → LinkRequired(key, masked e-mail): "sign in with your password/code once to connect Google"
  ├─ not linked, no account with that e-mail
  │     ├─ registration allowed for this provider in this realm → create account (contacts verified as trusted) → sign in
  │     └─ otherwise (staff realms: invitation only) → SignInRejected(AccountUnavailable) shown as "no access — ask your manager"
  └─ e-mail NOT trusted → never matched against existing accounts
```

```csharp
public sealed record ExternalSignedIn(SignedIn SignedIn, bool AccountCreated, bool LoginLinked);
public sealed record LinkRequired(SignInFlowToken Flow, ExternalAccountKey Key, MaskedDestination ExistingAccount);
public union ExternalSignInResult(ExternalSignedIn, LinkRequired, SecondFactorRequired, ContactVerificationRequired, SignInRejected);
```

- An account may have several external logins and keep its password; removing the **last** way to sign in is refused.
- `ProviderEnforcement.Required` for a domain (FarmaViva Entra, 03 §6): accounts with `@farmaviva.com.br` can no longer use
  passwords or e-mail codes; disabling the person in Entra means the next sign-in fails, and our session lifetime bounds the rest
  (optional back-channel/`sid` logout is an open item).

## 6. Providers per scope (⚑AN9)

```csharp
public interface IIdentityProviderDirectory
{
    /// <summary>Providers offered for a realm and (optional) scope, in display order. Cached per (realm, scope); invalidated on change.</summary>
    ValueTask<IdentityProviderList> GetAsync(AccountRealm realm, string? scope, CancellationToken ct);
}
```

- Static providers from `AddYFexAuthentication(...)` + **scoped providers** from the store (12 §2), configured by
  `authentication.providers.configure`. Secrets are references into YFex.Web's sealed per-scope secrets, never plain text in the
  provider table.
- The sign-in page asks `GET /auth/providers?realm=…` (public list: name, display name, icon key). Home-realm discovery: typing
  `bruno@farmaviva.com.br` on a shared page may route straight to FarmaViva's provider (domain → provider map).

**As built (P3b):** `ScopedProviderRecord` (kinds `OpenIdConnect`, `MicrosoftTenant`, `GoogleWorkspace`; `ClientSecretReference` = a YFex.Web
endpoint name of the scope's tenant, credential `OAuthClientCredential`), saved with `IdentityProviderDirectory.SaveAsync` (version CAS,
static names refused) behind the app's `authentication.providers.configure` gate. A scoped provider serves **only the per-scope realm of its
scope** (and scopes beneath it), never a shared realm. `GET providers?realm=&scope=&login_hint=` → `providers[{name, displayName, icon,
scope}]` + `discovered` (the provider the hint's domain is required for). Cache: 1 minute per scope and node; this node's changes apply at
once.
