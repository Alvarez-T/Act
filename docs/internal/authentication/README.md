# YFex.Authentication — accounts, sign-in, external providers, one-time codes, recovery, sessions and tokens — Design

> **Status:** AN-P0 (core `EmailAddress`/`PhoneNumber`) + **AN-P1 + AN-P2 implemented 2026-10-18**, AN-P3a/P3b (external providers)
> 2026-10-18, **AN-P4 (desktop client, shared terminals, offline bridge) 2026-10-19**, **AN-P5 (second factors, step-up, inline
> re-authentication, sign-in on another device) 2026-10-19**, **AN-P6 (passkeys) 2026-11-12** in `C:\Dev\YFex\YFex.Authentication`
> (245 tests on Windows and Linux, AOT smoke 0 warnings) — log and pending items in [14 §5](14-phases-and-acceptance.md). The user's
> go-ahead ("start implementing Phases 1 and 2") applied every ⚑AN recommendation of §7 as written. Next: AN-P7 (device session profiles,
> with installations I6). Hosting of background services: [hosting](../hosting/README.md).
> **Why:** the user asked (2026-10-17) for one reusable library that handles every form of authentication across YFex apps:
> passwords, external providers (Google, Microsoft and any OpenID Connect provider), confirmation by e-mail, SMS and WhatsApp,
> password reset, second factors, and the desktop/offline cases. Today **nothing in the ecosystem authenticates a person**
> ([01](01-current-state-and-inventory.md)): YFex.Web sends credentials *to* other APIs, YFex.Authorization takes an already
> validated `ClaimsPrincipal` ("JWT validation is the host's"), YFex.Tenancy reads a claim from it, Messaging.Rpc has an empty
> `ICallTokenValidator` slot, and YFex.Offline (⚑O12, confirmed today) waits for an "app-chosen credential source".
> **Repo (proposed):** `C:\Dev\YFex\YFex.Authentication` (sibling, like YFex.Authorization, YFex.Installations, YFex.Licensing).
> **Core types (proposed, ⚑AN3):** `EmailAddress` and `PhoneNumber` in YFex core (types only). Everything else lives in the repo.
> **No NuGet package** is added in the recommended plan: BCL (`HttpClient`, `System.Text.Json` source generation,
> `System.Threading.RateLimiting`, `RandomNumberGenerator`, `System.Net.Mail`), YFex.Cryptography for every key, MAC, hash and
> signature, YFex.Web for outbound provider APIs, YFex.Data for SQL stores.
> **Audience:** the user first, then the sessions that implement the phases ([14](14-phases-and-acceptance.md)).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`.

## 1. The one sentence

**Authentication proves *who* is there and hands out a session; it never decides what they may do.** Authorization
(`IAuthorizationContext`, permissions, roles, approvals) decides that, Tenancy says *whose data*, Installations says *on which
installed copy*. Authentication's output is exactly what those three already consume: a subject id (`ActorIdentity.Subject`), a
`ClaimsPrincipal`, an `AccessToken`, a session id, and — new — **how** the person proved it (methods, assurance level, time).

```
                        ┌──────────────────────────── YFex.Authentication ─────────────────────────────┐
  person ──► proves ──► │ password · operator code + PIN · e-mail/SMS/WhatsApp code · magic link ·     │
                        │ Google · Microsoft · Apple · gov.br · any OIDC · authenticator app · passkey │
                        │ recovery code                                                                │
                        │      ▼                                                                       │
                        │ ACCOUNT (AccountId, realm, verified contacts, credentials, external logins)   │
                        │      ▼                                                                       │
                        │ SESSION (SessionId, methods, assurance, auth time, installation?)             │
                        │      ▼                                                                       │
                        │ cookie (browser) · access token JWT ES256 + rotating refresh token (apps)    │
                        └──────┬──────────────────────┬─────────────────────┬──────────────────────────┘
                               ▼                      ▼                     ▼
   YFex.Authorization: IAuthorizationContext   Messaging.Rpc: CallIdentity   YFex.Offline: cached verifiers,
   (permissions from its own sources;          (ICallTokenValidator)         operator credential (⚑O12)
    acr/amr for step-up, act for impersonation)
```

## 2. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Account** | the authentication record of one person in one realm: id, status, verified contacts, credentials, external logins. **Not** the app's employee/customer record (that references the `AccountId`) | Ana's account in realm `0=farmaviva` |
| **Realm** | where an account is unique: a scope prefix the app declares (⚑AN4). Accounts never cross realms | staff of FarmaViva = realm `0=farmaviva`; marketplace shoppers = one shared realm |
| **Credential** | something the account holds and the library verifies: password, operator PIN, authenticator-app secret, passkey, recovery codes | Ana's password + operator code `0142` + PIN |
| **External login** | a link between the account and a subject at an identity provider | (`google`, `https://accounts.google.com`, `1043…`) |
| **Identity provider** | a party that authenticates the person for us: Google, Microsoft Entra ID, Apple, gov.br, a tenant's own Entra or Keycloak | FarmaViva's own Entra ID tenant (⚑AN9) |
| **Challenge** | a short-lived, single-use proof request: a code sent to a channel, or a magic link, for one **purpose** | 6-digit code to `+55 11 9****-4321` on WhatsApp to reset a password |
| **Delivery channel** | how a challenge reaches the person: `Email`, `Sms`, `WhatsApp`, `Voice` (+ app-defined) | WhatsApp through the Meta Cloud API |
| **Method** | how a person proved something in a session (RFC 8176 `amr` mapping) | `password`, `whatsapp-code`, `external:google`, `passkey` |
| **Assurance** | closed scale `None < SingleFactor < MultiFactor < PhishingResistant` (NIST 800-63B AAL1–AAL3 shape) | password + TOTP = `MultiFactor` |
| **Session** | the result of a sign-in: who, how, when, where (installation), until when; revocable | Ana on CAIXA-02 since 08:01, idle 15 min |
| **Sign-in flow** | the steps between "start" and "signed in" (identify → prove → second factor → verify contact → done), carried by a sealed **flow token** | Carla: Google → WhatsApp verification → signed in |
| **Step-up** | proving again (or with a stronger method) right before a sensitive operation | manager re-enters PIN to change prices |

Pharmacy and marketplace words appear **only in examples** (`yfex-libs-domain-agnostic`).

## 3. Shape and packages

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Authentication.Public` | contracts and value objects (leaf: YFex core + BCL + Authorization.Public): `AccountId`, `SessionId`, `AccountRealm`, `IdentityProviderName`, `ExternalAccountKey`, `AuthenticationMethod`, `AssuranceLevel`, `DeliveryChannel`, `ChallengePurpose`, `VerificationCode`, `Password`, `AuthenticatedSession`, results (`SignInResult`…), `ICredentialVerifierSource` bridge contract, `AuthenticationEvents` | YFex core, YFex.Authorization.Public |
| `YFex.Authentication` | the engine: accounts, password policy + verifiers, operator credentials, challenges and codes, flows, throttling, sessions, token issuer + **span-based validator** (hot path), JWKS, external-login linking, recovery, step-up, TOTP + recovery codes, `AuthenticationPermissions` catalog, in-memory stores | the above + YFex.Cryptography, YFex.Authorization (catalog, signals), YFex.Tenancy, M.E.DI/Options/Hosting abstractions |
| `YFex.Authentication.OpenIdConnect` | generic OAuth 2.0 / OIDC **client**: discovery, authorization code + PKCE, `iss` response check (RFC 9207), ID-token validation over JWKS, device authorization grant (RFC 8628); built-in descriptors **Google, Microsoft, Apple, gov.br**, GitHub (OAuth 2 only), generic OIDC | the engine + YFex.Web (outbound HTTP, per-scope credentials) |
| `YFex.Authentication.AspNetCore` | minimal-API endpoints (`MapYFexAuthentication`), cookie and bearer `AuthenticationHandler`s over the YFex validator, external callback, `/.well-known/jwks.json`, CSRF/redirect safety, rate-limit wiring, BFF helpers for SPAs | the engine, ASP.NET Core |
| `YFex.Authentication.Rpc` | `ICallTokenValidator` implementation for Messaging.Rpc, session revocation → `IAuthorizationSignals.RevokedAsync` | the engine, YFex.Messaging.Rpc |
| `YFex.Authentication.Client` | desktop/mobile/WASM client: sign-in calls, system-browser + loopback flow (RFC 8252), token storage (sealed, OS key store), silent refresh, `AmbientPrincipalAccessor.SignIn`, lock/unlock and quick switch at shared terminals | `.Public`, YFex.Authorization, YFex.Persistence, YFex.Cryptography |
| `YFex.Authentication.Delivery` | channel senders: **SMTP** (BCL), **e-mail HTTP APIs**, **Twilio** (SMS, WhatsApp, Verify), **Meta WhatsApp Cloud API**; later Zenvia, Amazon SNS/SES, GSM modem via YFex.Devices | `.Public`, YFex.Web |
| `YFex.Authentication.Offline` | bridge to YFex.Offline ⚑O12: credential verifier source, verifier capture at online sign-in, operator credential publication | the engine/client + YFex.Offline |
| `YFex.Authentication.Data` | SQL stores on YFex.Data (accounts, credentials, external logins, sessions, challenges, throttle counters, audit-friendly history) | the engine + YFex.Data |
| ~~`YFex.Authentication.Passkeys`~~ *(AN-P6: built inside the engine, `Passkeys/`)* | WebAuthn/FIDO2 registration and assertion (span CBOR/COSE reader, attestation `none`) | — |
| `YFex.Authentication.Testing` | in-memory everything, `TestIdentityProvider` (an in-process OIDC issuer), `CapturingSender` (reads the codes), fake clock, `AuthenticationHarness` | the above + YFex.Authorization.Testing, YFex.Time.Testing |
| tests, AotSmoke, Benchmarks | `.Tests` (Windows + Linux), `.AspNetCore.Tests`, `.OpenIdConnect.Tests` (recorded real token vectors), `.AotSmoke` (0 warnings), BenchmarkDotNet (`InProcessEmitToolchain`) | |

**No custom source generator** (⚑AN21, [13](13-performance-aot-platforms.md) §5): System.Text.Json source generation, ASP.NET's
request-delegate generator and the existing Authorization/Privacy/Logging generators cover every case found.
**No `.Windows`/`.MacOS`/`.Linux` package**: the only OS-specific part (client token storage) goes through YFex.Cryptography /
YFex.Persistence stores, which already carry the per-OS code ([13](13-performance-aot-platforms.md) §4).

## 4. What goes where

| Concept | Where | Why not elsewhere |
|---|---|---|
| `EmailAddress`, `PhoneNumber` (E.164) value objects | **YFex core** (⚑AN3) | named by Authentication, Delivery, Privacy, CRM-like apps; today `Email`/`Telefone` sit in YFex.Domain (`YFex.Entity.Public`) and `Telefone` is Brazil-only |
| accounts, credentials, external logins, challenges, sessions, tokens, flows, recovery, step-up proof | **YFex.Authentication** | — |
| permissions, roles, scoped grants, approvals, impersonation **decision**, `ActorContext` writers | **YFex.Authorization** (unchanged) | Authentication only proves identity; requests in [14](14-phases-and-acceptance.md) §2.1 |
| "act as user" token (`act` claim, RFC 8693) | **Authentication issues it** after Authorization allows it (⚑AN17) | answers Authorization README open question "claims shape … `act` claim" |
| tenant resolution from the token | **YFex.Web / Tenancy** `ClaimTenantStrategy` (unchanged) reading the home-scope claim Authentication writes | — |
| offline modes, site verifier bundle transport, offline session, lockout counters at the broker | **YFex.Offline** (⚑O12, unchanged) | Authentication supplies the verifier **source** and the operator credential (⚑AN18) |
| installation identity, device-to-central proof | **YFex.Installations** | a session can be **bound** to an installation (⚑AN16), but a device never "signs in" here |
| outbound HTTP to Google/Microsoft/Twilio/Meta with per-tenant credentials | **YFex.Web** (`AddApi`, per-scope endpoints, token cache) | reuse; no second HTTP client stack |
| keys, MACs, password verifiers, JWT signatures, sealed refresh tokens | **YFex.Cryptography** | crypto rule (no direct `HMACSHA256`/`ECDsa`/`RSA` outside it; requests ⚑AN11/⚑AN12) |
| durable audit trail of sign-ins, resets, factor changes | **YFex.Audit** maps `AuthenticationEvents` | Authentication emits through the core probes like `AuthorizationEvents` |
| app profile (job title, store, customer number) | **the app**, keyed by `AccountId` | model-layering: the account is not the app's user model |
| general notifications (order shipped, marketing) | **not here**; a future `YFex.Notifications` may reuse `.Delivery` (⚑AN14) | authentication messages have special rules (templates, rate limits, no links in WhatsApp OTP) |
| being an OAuth authorization server **for third parties** | **out of scope v1** (⚑AN7) | a full OP (consent screens, client registration, dynamic scopes) is a product of its own |

## 5. Reading order

1. [01 — Current state and inventory](01-current-state-and-inventory.md) (what exists, file:line, defects)
2. [02 — Model and types](02-model-and-core-types.md) (account ≠ actor ≠ app user, realms, value objects, unions)
3. [03 — API and examples](03-api-and-examples.md) (**dev side first**: server, desktop POS, marketplace web, tests)
4. [04 — Passwords, PINs and local credentials](04-passwords-pins-credentials.md)
5. [05 — One-time codes, links and delivery (e-mail, SMS, WhatsApp)](05-one-time-codes-and-delivery.md)
6. [06 — External identity providers (Google, Microsoft, Apple, gov.br, any OIDC)](06-external-identity-providers.md)
7. [07 — Sessions, tokens and validation](07-sessions-and-tokens.md)
8. [08 — Account lifecycle, verification and recovery](08-account-lifecycle-and-recovery.md)
9. [09 — Second factors, step-up and assurance](09-second-factors-step-up-assurance.md)
10. [10 — Desktop, shared terminals, offline and installations](10-desktop-offline-installations.md)
11. [11 — Threats, abuse and privacy](11-threats-abuse-privacy.md)
12. [12 — Storage](12-storage.md)
13. [13 — Performance, AOT, platforms and generators](13-performance-aot-platforms.md)
14. [14 — Phases, acceptance, requests to other libraries, implementation log](14-phases-and-acceptance.md)

## 6. The rules that shaped every decision

1. **Identity only.** No permission, role or tenant membership is ever decided or cached here. Tokens carry identity facts
   (`sub`, `sid`, home scope, `amr`, `acr`, `auth_time`, `act`), never permission lists (Authorization resolves them, ⚑AN15).
2. **Fail closed, say little.** Every rejection to an anonymous caller is generic ("invalid credentials", "if the account exists, a
   code was sent"). Reasons are logged and audited, never returned before the caller proved something.
3. **One verifier per secret, all through YFex.Cryptography.** Passwords and PINs → `PasswordHashing` (PBKDF2-SHA256, pepper MAC).
   Codes and links → `MessageAuthentication`/`MessageToken`. Our tokens → `SigningKeyRing` ES256. No secret is stored readable.
4. **Hot path = token validation.** Every HTTP request and RPC connection validates a token: span parsing, no LINQ, no boxing,
   `[Union]` result, a validated-token cache. Sign-in flows are cold: native `union` results, clarity first.
5. **Generic by declaration.** Channels, purposes, methods, providers and realms are app-declarable value objects with well-known
   built-ins. Only truly closed sets are enums (`AssuranceLevel`, `AccountStatus`, rejection reasons).
6. **Per-scope everything that a tenant may own:** provider client ids/secrets, SMS/WhatsApp senders, templates, password policy,
   MFA policy. Resolved through YFex.Tenancy + YFex.Web per-scope endpoints, so one deployment serves many tenants.
7. **Degrade, never lock the shop out** — offline sign-in rules are Offline's (⚑O12); Authentication makes sure the verifiers exist.

## 7. Decisions to confirm (⚑AN)

Prefix **⚑AN** because ⚑A is YFex.Audit's.

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **AN1** | Where does it live? | Sibling repo **`C:\Dev\YFex\YFex.Authentication`**, packages §3; contracts in a leaf **`.Public`** (core + BCL + Authorization.Public, enforced by an architecture test) | inside YFex.Authorization (mixes "who" with "may"; every authorization consumer would ship password and OIDC code); inside YFex.Web (Web is outbound-only by its AGENTS rule) | §3, §4 |
| **AN2** | Broker or pass-through? | **Broker**: apps sign in through Authentication; external providers are *methods* of a YFex account; Authentication issues its **own** session/tokens. Apps that already trust an external IdP's tokens directly keep `AddYFexAuthorizationJwtBearer` (unchanged, "bring your own IdP" mode) | each app validates Google/Entra tokens directly (no MFA policy of ours, no revocation, no offline verifier capture, several token formats per app) | 02 §1, 07 §1 |
| **AN3** | Contact value objects? | **Core** `EmailAddress` (generalised from `YFex.Entity.Public.Email`: lower-cased, `[Sensitive(Personal, contact)]`, IDN domain as punycode) and **`PhoneNumber`** (E.164 digits + country calling code; national formatting is an app/extension concern). `Email` and `Telefone` in YFex.Domain get conversions (`Telefone.ToPhoneNumber()`), not deleted | reference `YFex.Entity.Public` from an infrastructure library (domain repo dependency, Brazil-only phone) ; strings (no validation, no masking) | 02 §3 |
| **AN4** | Where is an account unique? | **Realm = a scope prefix declared by the app** (`AccountRealm.Shared` or `AccountRealm.PerScopeLevel(0)` = per tenant). An account lives in exactly one realm; the same e-mail in two realms = two accounts. Membership in sub-scopes (stores) is Authorization's scoped roles | always global accounts (tenant admins could see/lock users of other tenants; per-tenant SSO and deletion become hard); always per tenant (a marketplace shopper needs an account per seller) | 02 §2 |
| **AN5** | Account id? | `AccountId` = `Guid.CreateVersion7()`, canonical text = `ActorIdentity.Subject`; `ICanonicalValue`, `ILogValue`; not personal data by itself (like `InstallationId`). Never the e-mail | e-mail as subject (changes, personal data in every log and token) | 02 §3 |
| **AN6** | Which methods in v1? | **Phase order:** password + operator code/PIN (P1) → e-mail/SMS/WhatsApp codes, magic links for verification/reset (P2) → OIDC Google/Microsoft/generic + Apple + gov.br (P3) → TOTP + recovery codes + step-up (P5) → passkeys (P6). Passwordless code sign-in is P2 | all at once (P1 would be months) | 14 §4 |
| **AN7** | Are we an OAuth server for others? | **No** in v1: we publish **JWKS** so our own services verify our tokens, but no `/authorize` for third-party clients, no consent screens, no client registry. Open question for later (§8) | full OpenID Provider (a product of its own: Keycloak/OpenIddict territory) | 07 §6 |
| **AN8** | Token formats? | **Browser** → server session + cookie `__Host-yfex.session` (HttpOnly, Secure, SameSite=Lax; SPA = BFF). **Apps/desktop/mobile/RPC** → **access token JWT ES256** (`typ at+jwt`, RFC 9068, 10 min) + **opaque rotating refresh token** (256-bit random, stored as `Hash256`, family reuse detection). | opaque HMAC access tokens (only the issuer can validate; every service needs a lookup); long-lived JWTs (no revocation) | 07 §2–§4 |
| **AN9** | Per-tenant providers (enterprise SSO)? | **Yes**: an `IIdentityProviderDirectory` resolves the providers offered **per realm/scope** (FarmaViva signs in with its own Entra tenant; the marketplace offers Google + Apple). Client secrets live in YFex.Web's sealed per-scope secrets (`SealedSecretProtector`). Static configuration covers single-tenant apps | providers only in `appsettings` (every tenant shares one Google client; no enterprise SSO) | 06 §6 |
| **AN10** | Own JOSE code or Microsoft.IdentityModel? | **Own minimal JOSE**: issue ES256; verify ES256, RS256, PS256 only; `alg` from the **key**, never the header; span-based (`Base64Url` + `Utf8JsonReader` over stack buffers), golden vectors (RFC 7515/7518, recorded Google/Microsoft tokens). JwtBearer stays for "bring your own IdP" | `Microsoft.IdentityModel.JsonWebTokens` + JwtBearer (NuGet, larger surface incl. `alg` negotiation, allocation-heavy validation, AOT status to measure) | 07 §5, 13 §2 |
| **AN11** | RSA for external ID tokens? | **Request to YFex.Cryptography:** verify-only `SignatureAlgorithm.RsaPkcs1Sha256` / `RsaPssSha256` for **external** public keys (`SignatureVerifier.ForExternalKey(jwk)`), never for signing by YFex | RSA inside Authentication as a protocol-mandated exception (architecture-test allow-list entry) | 06 §4, 14 §2.2 |
| **AN12** | TOTP needs HMAC-SHA1 (RFC 6238 default; most authenticator apps) | **Request to YFex.Cryptography:** `OneTimePassword.Totp(secret, time, digits, algorithm)` with SHA-1 (protocol-mandated) and SHA-256; secret in a sealed record | Authentication calls `HMACSHA1` directly (allow-list entry) | 09 §2, 14 §2.2 |
| **AN13** | Codes: who generates and checks them? | **The library**, by default: 6 digits (`RandomNumberGenerator.GetInt32`), stored only as a MAC (`authentication/codes` ring, domain per purpose), 10 min, 5 attempts, single use, resend cooldown 60 s. **Provider-verified** mode (Twilio Verify) as an alternative `IVerificationProvider` per channel | provider-only (vendor lock-in, no WhatsApp via Meta directly, no offline/on-prem SMTP) | 05 §2–§3 |
| **AN14** | Message delivery? | **Narrow port** `IChallengeSender` per `DeliveryChannel` in `.Public`; adapters in **`YFex.Authentication.Delivery`** on YFex.Web: SMTP (BCL `SmtpClient` for relays), e-mail HTTP API (SendGrid-style, generic), **Twilio** (SMS + WhatsApp + Verify), **Meta WhatsApp Cloud API** (authentication templates with copy-code/one-tap). Zenvia, Amazon SNS/SES and GSM modem later. A future `YFex.Notifications` may absorb `.Delivery` | a full notifications library first (blocks auth on a bigger design); MailKit (new NuGet, ask first) | 05 §4–§6 |
| **AN15** | What goes into our tokens? | Identity only: `iss`, `aud`, `sub`, `sid`, `iat`, `exp`, `auth_time`, `amr`, `acr`, `scope_home` (canonical home scope for `ClaimTenantStrategy`), optional `act`, `installation`. **No roles/permissions** — Authorization's sources resolve them; an `IAccessTokenClaimsContributor` lets an app add a few claims explicitly | permissions in the token (stale for 10 min, large tokens, two truths with `PermissionSnapshot`) | 07 §3 |
| **AN16** | Bind tokens to the device? | **Yes where we can:** on enrolled installations the refresh token is bound to the `InstallationId` (refresh accepted only over an Installations.Rpc call / assertion of that installation); in browsers the cookie is the binding; DPoP (RFC 9449) for other public clients later | bearer refresh tokens everywhere (a copied token works on any machine) | 07 §4, 10 §5 |
| **AN17** | Impersonation tokens? | Authentication issues a **separate, short (15 min) session** with `act` = the support agent, after Authorization allows `authentication.impersonate-user` and an approval if configured; the real actor's session is untouched; impersonated sessions cannot change credentials or contacts | swap the subject in place (real actor lost — Authorization's D11 again) | 07 §7 |
| **AN18** | Offline (⚑O12) — who owns the operator credential? | **Authentication owns the credential** (operator code unique per realm, PIN/offline secret verifier at central, lockout policy). **Offline keeps** the site verifier bundle (`authentication.verifiers`, already reserved), the offline session and broker lockout. `YFex.Authentication.Offline` implements Offline's credential source and captures the terminal's cached verifier at online password/PIN sign-in. Needs the Offline session's agreement | operator credential stays in YFex.Offline (two places define "a PIN", online sign-in cannot use it) | 10 §3–§4 |
| **AN19** | Account linking with external providers? | **Never link silently by e-mail** unless the provider is trusted for that e-mail (Google `email_verified` + Google-hosted domain; Microsoft only with `xms_edov`/verified domain — the "nOAuth" attack). Otherwise: "an account with this e-mail exists — sign in to link". Explicit linking from the account page | link by e-mail always (account takeover through an unverified Entra e-mail) | 06 §5 |
| **AN20** | Sign-in transport for apps? | **HTTP endpoints** (`MapYFexAuthentication`) for every client, because Messaging.Rpc captures the token **at WebSocket connect** (`FromConnection`); the client then (re)connects RPC with the token. Same `ISignIn` services underneath | sign-in commands over RPC (anonymous connection, then reconnect anyway) | 03 §2, 07 §8 |
| **AN21** | Source generator? | **None** in v1 (analysis in 13 §5): STJ source-gen contexts for wire types, ASP.NET RDG for endpoints, existing Authorization catalog generator for `AuthenticationPermissions`, Privacy/Logging generators for classified types. Reconsider only for compile-checked message templates | a generator for templates/claims mapping (no measured need) | 13 §5 |
| **AN22** | Password policy? | NIST 800-63B shape: **min 10** (per scope 8–64), max 128, all Unicode (NFC), **no composition rules, no forced rotation**, block a bundled **common-password list** + the account's own e-mail/name; optional **breached-password check** (HIBP k-anonymity, opt-in, off by default); PBKDF2 600 000 + **pepper** (MAC under `authentication/pepper` before hashing); upgrade on sign-in | composition rules + 90-day rotation (proven to weaken passwords); Argon2 (not in BCL, ⚑K14) | 04 §1–§3 |
| **AN23** | Throttling and abuse? | **Layered**: per account+IP (5 failures → exponential delay, lock 15 min), per IP, per destination (3 codes/15 min), per scope SMS/WhatsApp **daily budget** and **country allow-list** (SMS pumping), optional `IHumanVerification` hook (Turnstile/reCAPTCHA adapter by the app). In-memory by default, `.Data` store for farms | only per-account lockout (lets an attacker lock every user; no SMS fraud protection) | 11 §2–§3 |
| **AN24** | Unions? | **Hot paths `[Union]` tagged structs**: `TokenValidation`, `ThrottleDecision`, `ChallengeCheck`. **Cold paths native `union`**: `SignInResult`, `RegistrationResult`, `ExternalSignInResult`, `RecoveryResult`, `StepUpResult` | `[Union]` everywhere (verbose for rare flows); native everywhere (boxes `TokenAccepted` per request) | 02 §5 |
| **AN25** | Permissions of the library? | `AuthenticationPermissions` catalog: `authentication.accounts.view`, `.accounts.manage` (create, disable, temporary password/PIN), `.accounts.invite`, `.factors.reset`, `.sessions.revoke`, `.impersonate-user`, `.providers.configure`, `.policy.configure`. Resetting another person's second factor and impersonating **require an approval** by default (Authorization `.RequireApproval`) | ad-hoc admin flag | 08 §6 |

### 7.1 Added 2026-10-20 — mobile handhelds (installations [11](../installations/11-mobile-handhelds.md)) — confirmed by the user 2026-10-20

| ⚑ | Question | Decision | Alternative (cost) | Details |
|---|---|---|---|---|
| **AN26** ✅ | Session length for a specific device app (e.g. a stock handheld)? | **Optional, configurable session profile per application name** (the installation's `ApplicationName`): idle, absolute, lock-after-idle sent to the client. Without a profile the per-`ClientKind` defaults of 07 §2 stay (user: "optional and configurable") | a fixed shift-length session for every mobile client (wrong for shopper apps) | 07 §2.1 |
| **AN27** ✅ | "Re-authenticate after N hours outside the store"? | **Optional off-site rule** in the profile: when the session has no verified **presence** (installations ⚑I30, witness-signed) for longer than `after`, the next refresh demands a step-up to `minimum` (or locks / signs out, configurable). The verified presence time is the `presence_at` claim. **Never** from IP, SSID or network fingerprint | IP/Wi-Fi based (copied SSID, shared password, CGNAT) | 07 §2.1, 09 §4.3 |
| **AN28** ✅ | Revoked installation? | Installations' revocation event **ends every session bound to that installation at once** (refresh families revoked, `sid` into the revoked-session set), not only at the next refresh | wait for the next refresh (≤ 10 min of access-token life) | 07 §4, 10 §5 |

## 8. Open questions (not blocking the decisions)

- **OpenID Provider for third parties** (⚑AN7): a partner app signing in "with YFex Farma". Later, probably by putting a standard
  server (OpenIddict/Keycloak) in front of the account store rather than writing one.
- **SAML 2.0** for large enterprises that have no OIDC: out of v1; Entra, Okta and ADFS all speak OIDC.
- **Login UI controls** (YFex.UI/Avalonia sign-in screens): the client package exposes view-model-friendly state; reusable
  controls are a YFex.UI decision.
- **CAPTCHA adapters** (Cloudflare Turnstile, reCAPTCHA): the hook is in v1, adapters are app-provided until one is needed twice.
- **Notifications library** (⚑AN14): if order/marketing messages need WhatsApp/SMS too, `.Delivery` moves into `YFex.Notifications`.
- **gov.br levels** (bronze/prata/ouro) as an `acr`-like assurance input for Brazilian apps: modelled as provider claims mapped to
  `AssuranceLevel` (06 §3.4); confirm with a real gov.br client registration.
