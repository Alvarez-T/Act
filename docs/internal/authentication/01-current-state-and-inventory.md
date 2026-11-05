# 01 — Current state and inventory

Survey date 2026-10-17. Paths are relative to `C:\Dev\YFex`. "Line" = line in the current file. The survey searched every
`YFex*` repository (backups excluded) for `AddAuthentication`, `JwtBearer`, `ClaimsPrincipal`, `SignIn`, `Login`, `Password`,
`RefreshToken`, `Otp`, `TwoFactor`, `OpenIdConnect`, `Google`, `Smtp`, `Twilio`, `WhatsApp`, `Sms`.

## 1. Summary

**No YFex library or app authenticates a person.** There is no account store, no password check, no sign-in screen, no external
provider client, no code sender, no session or refresh-token store. Everything that exists either **consumes** an already
authenticated `ClaimsPrincipal`/bearer token, or authenticates **outbound** service calls. The pieces below are the seams a new
library must plug into — they define its outputs.

## 2. What exists, by repository

### 2.1 YFex.Web — outbound credentials only

| What | Where | Relevance |
|---|---|---|
| Closed credential union `ApiCredential` (`BasicCredential`, `BearerTokenCredential`, `ApiKeyCredential`, `OAuthClientCredential`, `ClientCertificateCredential`) + `EndpointAuthHandler` switch | `YFex.Web/src/Web/YFex.Web/Credentials/ApiCredential.cs:16`; `YFex.Web.Refit/Handlers/EndpointAuthHandler.cs` | **reuse** for Authentication's own outbound calls (Google token endpoint, Microsoft JWKS, Twilio, Meta). Not a sign-in mechanism |
| OAuth **client-credentials** token acquisition, tenant-scoped token cache, `[OAuthTokenEndpoint]` generator | `YFex.Web/docs/03-authentication-guide.md` §3.3; `YFex.Web.Generator/OAuthGenerator.cs` | proves the AOT-safe binding pattern (lambda or generated). User sign-in needs the **authorization-code** grant, which Web does not do (and should not: Web is outbound-only by its AGENTS rule) |
| Per-scope endpoints + sealed secrets (`SealedSecretProtector`, `y3.` prefix, `ISecretProtector`) | `YFex.Web/src/Web/YFex.Web/Credentials/ISecretProtector.cs:63` | **reuse** for per-tenant provider client secrets and SMS/WhatsApp sender credentials (⚑AN9) |
| Tenant strategies: `ClaimTenantStrategy` reads `httpContext.User.FindFirstValue(claimType)` | `YFex.Web.AspNetCore/Tenant/Strategies/ClaimTenantStrategy.cs:26` | Authentication writes the **home-scope claim** this strategy reads (07 §3) |
| Act-as-tenant header + `ITenantImpersonationGate` | `YFex.Web.AspNetCore/Tenant/TenantResolutionMiddleware.cs:59-69`, `TenantImpersonationOptions.cs` | unchanged; user impersonation (⚑AN17) is a different thing |

### 2.2 YFex.Tenancy — reads the principal

| What | Where | Relevance |
|---|---|---|
| `ITenantImpersonationGate.CanImpersonate(ClaimsPrincipal?)`; default `PermissionTenantImpersonationGate` checks `HasClaim("permission", "Tenant.Impersonate")` | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantImpersonationGate.cs:22,37` | **still reads a raw claim** (Authorization D11; the planned `PolicyTenantImpersonationGate` of ⚑Z14 is **not built** — no file found). Authentication must not "fix" this by putting a permission claim in its tokens (⚑AN15); Authorization's Z14 is the fix |
| `ITenantScopeProjector.Project(ITenantInfo?, string? userId)` | `ITenantScopeProjector.cs` | the user rung is the plain subject string → our `AccountId` text |
| `ITenantResolveStrategy` with `object context` | `ITenantResolveStrategy.cs` | — |

### 2.3 YFex.Authorization — consumes identity

| What | Where | Relevance |
|---|---|---|
| `AddYFexAuthorizationJwtBearer(configure)` = `AddAuthentication(JwtBearer).AddJwtBearer(configure)`; doc: "JWT validation is the host's" | `YFex.Authorization/src/YFex.Authorization.AspNetCore/ServiceCollectionExtensions.cs:18,33` | stays for "bring your own IdP" (⚑AN2). Apps using YFex.Authentication register **our** handler instead (07 §5) |
| `IAuthorizationContext { Principal, Actor, RealActor, Relation, Scope, IsAuthenticated, Permissions, Version, Token }` | `YFex.Authorization.Public/IAuthorizationContext.cs` | built **from** the principal Authentication produces |
| `ActorIdentity(Subject, Kind, HomeScope)`, `ActorRelation { None, TenantImpersonation, UserImpersonation, Delegation, AgentOnBehalf }`, `ActorScope(Actor, RealActor, Relation, SessionId)` | `YFex.Authorization.Public/Actor.cs` | `Subject` = `AccountId` text; `SessionId` = our `SessionId`; `UserImpersonation` ↔ `act` claim |
| `AmbientPrincipalAccessor.SignIn(ClaimsPrincipal, AccessToken?)`, `SignOut()` | `YFex.Authorization/AmbientPrincipalAccessor.cs` | the desktop client calls it after sign-in (10 §1) |
| `AccessToken(Value, ExpiresAt, Scheme)`; **`IsExpired` uses `DateTimeOffset.UtcNow`** | `YFex.Authorization.Public/AccessToken.cs:26` | reuse as our access-token carrier. Finding **D2** below |
| `ClaimType` normalises `sub` → `ClaimTypes.NameIdentifier`, `role`, `email`, `name` | `YFex.Authorization.Public/ClaimType.cs` | our principal must set `NameIdentifier` (D3) |
| `IAuthorizationSignals.RevokedAsync(subject)` closes RPC connections | README §4 | sign-out / disable / password reset call it (07 §6) |
| Approvals (`ApprovalSource.Inline` = approver re-authenticated on the same screen, audit 02) | `YFex.Authorization.Public/Approvals.cs`; audit 02 `:249` | Authentication supplies `IReauthentication` for inline approvals (09 §4) |
| Open question in the consolidation README: "identity provider claims shape for `scoped_role`/`scoped_permission` and the RFC 8693 `act` claim" | `docs/internal/authorization-consolidation/README.md:94` | **answered here**: no roles in our tokens (⚑AN15), `act` for user impersonation (⚑AN17) |

### 2.4 YFex.Messaging.Rpc — waits for a validator

| What | Where | Relevance |
|---|---|---|
| `ICallTokenValidator.ValidateAsync(AccessToken, ScopeIdentity requestedScope, ct) → CallIdentity?` — "registered by the app; the token format is the host's business" | `YFex/src/Messaging/YFex.Messaging.Rpc/CallContext/CallContextOptions.cs:28` | **`YFex.Authentication.Rpc` implements it** (07 §8) |
| `ICallIdentitySource`, `FromConnection()` captures `HttpContext.User` + bearer **once at WebSocket connect** | same file `:14`, `:49-58` | why sign-in runs over HTTP first (⚑AN20) |
| `CallContext.SubjectOf(principal)` = `NameIdentifier ?? "sub"`; `ExpiryOf` reads the `exp` claim | `CallContext/CallContext.cs:72-76,146-149` | our principal carries both |

### 2.5 YFex.Offline — waits for a credential source

| What | Where | Relevance |
|---|---|---|
| ⚑O12 confirmed 2026-10-17: "verifiers come from an **app-chosen credential source** (built-in operator credential optional; identity provider per project) … capturing the secret when the app's online login never sees it is the app's job" | `docs/internal/offline/README.md:179`, open question `:148-153` | `YFex.Authentication.Offline` **is** that source (⚑AN18) |
| `OfflineBundleKinds.CredentialVerifiers = "authentication.verifiers"` | `YFex.Offline/src/YFex.Offline/OfflineDomains.cs:38` | the kind name already says "authentication"; payload owned by Offline, content produced from our verifiers |
| Offline login flow sketch (`SignInRequest(OperatorCode, Secret)`, outcomes `signedIn / notAvailableOffline / lockedOut / rejected`), PBKDF2 600 000, short PINs only when the broker verifies, lockout 5 tries / 5 min doubling, session ≤ 12 h | `docs/internal/offline/05-protecting-offline-data.md:225-258` | our `SignInResult` uses the same case names where they mean the same (02 §5) |

### 2.6 YFex.Cryptography — primitives ready

| What | Where | Relevance |
|---|---|---|
| `PasswordHashing.CreateVerifier/Verify` (PBKDF2-SHA256, 600 000 default, NFC normalisation, `PasswordCheck { Match, MatchNeedsUpgrade, Mismatch, UnsupportedFormat }`) | `YFex.Cryptography/src/YFex.Cryptography/Passwords/PasswordHashing.cs:72,98,132` | passwords, PINs, recovery codes (04) |
| `MessageAuthentication`, `MessageToken.Issue/Read` (domain-separated HMAC tokens with expiry), `KeyRing`, `ScopedKeyRings` | `Authentication/MessageToken.cs:45-149` | codes (stored as MACs), magic links, sealed flow tokens, CSRF/state (05, 07) |
| `SigningKeyRing` (software P-256 rings, own `IPublicKeyDirectory`), `SignatureVerifier`, `SignedPayload` | crypto README | our JWT signatures + JWKS (07 §5) |
| `SealedValue`, `RecipientSealing` | crypto README | refresh tokens at rest on clients, TOTP secrets at rest |
| **Only `SignatureAlgorithm.EcdsaP256Sha256`** is implemented (ML-DSA reserved) | `YFex/src/YFex/Cryptography/Signatures.cs:7-15` | Google/Microsoft ID tokens are **RS256** → request ⚑AN11 |
| No HMAC-SHA1 | — | TOTP authenticator apps → request ⚑AN12 |

### 2.7 Value objects that exist

| Type | Where | Fit |
|---|---|---|
| `Email` (lower-cased, `[Sensitive(Personal, contact, {email})]`, span validation) | `YFex.Domain/src/YFex.Entity.Public/Email.cs:31` | good implementation, **wrong home** for an infrastructure library (domain repo) → core `EmailAddress` (⚑AN3) |
| `Telefone` (Brazil only: `+55`, DDD, 9-prefixed mobile) | `YFex.Domain/src/YFex.Entity.Public/Telefone.cs:8-15` | **not reusable** for SMS/WhatsApp to other countries → core `PhoneNumber` (E.164), `Telefone` converts |
| Validation rule `Email : ITextRule` | `YFex/src/Validation/YFex.Validation/BuiltIn/Checks.cs:39` | validation rule, not a value object; keep |
| `ValidityPeriod`, `TrustedInstant`, `Hash256`, `KeyId`, `InstallationId` | YFex core | reused as-is |

### 2.8 Delivery

| What | Where | Fit |
|---|---|---|
| GSM modem SMS: `AtClient` sends `SmsPdu.EncodeSubmit(number, text)` | `YFex.Devices/src/YFex.Devices/Protocols/At/AtClient.cs:134,225`; `SmsPdu.cs:16` | a possible later on-premises SMS adapter (05 §6) |
| SMTP, e-mail APIs, SMS gateways, WhatsApp | **nothing** | new `.Delivery` package (⚑AN14) |

### 2.9 Apps

`YFex.Farma`, `YFex.Marketplace`, `YFex.Domain`, `YFex.Brain`: **no login screen, no user store, no password field** (search for
`login|signin|senha|password` in `.cs`/`.axaml` returned nothing). There is nothing to migrate; the library defines the first
implementation.

## 3. Findings

| # | Kind | Finding | Where | Handled in |
|---|---|---|---|---|
| **D1** | gap | No inbound authentication anywhere; Authorization's inbound docs assume an external IdP | §2.3 | the whole design |
| **D2** | correctness | `AccessToken.IsExpired` reads `DateTimeOffset.UtcNow` (not `TimeProvider`/trusted time); tests and offline clients cannot control it | `AccessToken.cs:26` | request to Authorization: `IsExpired(TimeProvider, skew)` overload (14 §2.1 R5). Authentication never calls the clock-less overload |
| **D3** | contract | Three readers of "who" (`CallContext.SubjectOf`: `NameIdentifier ?? sub`; Tenancy projector: `string? userId`; `ClaimType` normalisation `sub → NameIdentifier`). Our principal must put `AccountId` in `NameIdentifier` **and** keep `sub` raw claim available | §2.3, §2.4 | 07 §3 (principal shape, one place) |
| **D4** | security | Tenancy's default impersonation gate reads a raw, case-sensitive `permission` claim. If any token issuer ever adds `permission=Tenant.Impersonate`, it works | `ITenantImpersonationGate.cs:37` | not ours to fix (Authorization ⚑Z14); our tokens never carry permission claims (⚑AN15) |
| **D5** | gap | Offline login (⚑O12) has no source for verifiers; the bundle kind exists, the producer does not | §2.5 | 10 §3 |
| **D6** | gap | Cryptography verifies only ECDSA P-256 | §2.6 | ⚑AN11 |
| **D7** | reuse risk | `Telefone` is Brazil-only; WhatsApp/SMS need E.164 | §2.7 | ⚑AN3 |

## 4. Prior art in other designs to stay consistent with

- **Installations**: identity of a *copy of the app* comes only from certificates; Authentication never authenticates a device, it
  can only **bind** a session to an installation (⚑AN16).
- **Licensing**: named-user licensing (open question there) could later count Authentication **sessions** per realm.
- **Privacy**: `DataCategory.Credentials` = `Secret` (erased always); contact data `Personal`.
- **Audit 02**: "Desktop / POS login — the app's session service enters `ActorContext`"; Authentication's client is that service.
- **Cryptography 04 §7**: PBKDF2 600 000; Argon2/scrypt/bcrypt rejected (⚑K14) — followed in ⚑AN22.
