# 14 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules (every phase)

- **No code before the user confirms ⚑AN1–AN25** (README §7).
- `C:\Dev\YFex` is **not a git repository** and edits are live. Before touching an existing file, copy it to
  `C:\Temp\authentication-backup\<phase>\` with the same relative path.
- Check modification times before editing shared files. Parallel sessions own: Authorization, Offline (Phase 3 offline login),
  Installations, Cryptography, Web, Tenancy. Coordinate through `ListAgents`/`SendMessage` and their logs; never edit their code to
  land a hook — send the request (§2) and wait.
- Use the RoselYn MCP tools for C# exploration and `dry_run_compile` before writing non-trivial edits.
- No new NuGet package without asking (MailKit, `System.IO.Hashing`, Microsoft.IdentityModel are **not** in the plan). Every key, MAC,
  hash and signature goes through YFex.Cryptography (architecture test green with **no** new allow-list entry).
- Build with `--artifacts-path C:/Temp/authentication-artifacts -p:NuGetAudit=false`; native AOT smoke at 0 warnings for every package;
  tests on Windows, Linux runs listed as pending when no Linux machine is available.
- Record test counts before and after in §5 and update the memory file `yfex-authentication.md`.

## 2. Requests to other libraries (each owner decides the exact shape)

### 2.1 YFex.Authorization

| # | Request | Why | Needed by |
|---|---|---|---|
| R1 | `Requirement` case **RecentAuthentication(maxAge, minimum AssuranceLevel)** reading `auth_time`/`acr` from the caller (`AssuranceLevel` would live in `.Public` of Authentication, or Authorization defines a byte-compatible enum — owner decides) | step-up for commands (09 §4.1) | P5 — **✅ 2026-10-19** (AN-P5 session): `AssuranceLevel` moved to Authorization.Public, `AuthenticationFacts`, `RecentAuthenticationRequirement`, `IAuthorizationContext.AuthenticatedAt`/`Assurance` |
| R2 | `DenialKind.ReauthenticationRequired` + Cqrs `Error` mapping (like `ApprovalRequired`), RPC wire bump | typed client reaction | P5 — **✅ 2026-10-19**: `ErrorKind.ReauthenticationRequired = 9`, RPC v7, HTTP 401 + RFC 9470 challenge |
| R3 | builder verb `.RequireRecentAuthentication(TimeSpan, AssuranceLevel)` on Cqrs/route/endpoint builders | dev syntax of 03 §5 | P5 — **✅ 2026-10-19** (`RequirementBuilder<T>`, Cqrs command/query builders) |
| R4 | confirm the principal shape (07 §3): `NameIdentifier` = `sub`, `ClaimsIdentity.Actor` for `act` → `RealActor` + `UserImpersonation` | impersonation (⚑AN17), D3 | P3 — **✅ 2026-10-19** (AN-P4 session): `AuthorizationContext` takes `RealActor` + `UserImpersonation` from `ClaimsIdentity.Actor` (`AuthorizationContext.RealActorOf`), and `ActorScope.From` carries the `sid` claim as `SessionId` |
| R5 | `AccessToken.IsExpired(TimeProvider, TimeSpan skew)` overload (finding D2) | testable/offline expiry | P1 |
| R6 | read of an account's roles for MFA policy (`RequireFor(roles)`) without a full context — reuse `IPermissionDirectory` or a small `IRoleDirectory` | 09 §3 | P5 — **✅ 2026-10-19**: `IRoleDirectory.IsInAnyRoleAsync` (+ `FakeRoleDirectory`) |
| R7 | build `PolicyTenantImpersonationGate` (⚑Z14, not built; finding D4) | removes the raw-claim gate | independent — **✅ 2026-10-29** (Authorization Phase 6): `PolicyTenantImpersonationGate` replaces the raw-claim gate in `AddYFexAuthorization` (role grants count, `Tenant.Impersonate` canonicalizes), `ImpersonationRestrictions`, real actor in `ActorContext` over HTTP and RPC (`AllowImpersonation()`), `ImpersonationStarted` |
| R8 | *(2026-10-20)* `Requirement` case **SitePresence(maxAge)** reading the `presence_at` claim, `DenialKind.SitePresenceRequired`, builder `.RequireSitePresence(TimeSpan)` | per-command "only inside the store" for handhelds (⚑AN27, installations 11 §5.3) | AN-P7 — accepted by Authorization as ⚑Z20 — **✅ 2026-10-29** (consumer side): `RequirementKind.SitePresence`, `SitePresenceRequirement`, `AuthenticationFacts.PresenceAtClaimType`, `IAuthorizationContext.PresenceAt`, `ErrorKind.SitePresenceRequired = 10`, RPC v8, HTTP 403 + `maxAge`, `TestCallerBuilder.PresentAt`; the `presence_at` producer is still AN-P7 / installations I6 |

### 2.2 YFex.Cryptography

| # | Request | Why | Needed by |
|---|---|---|---|
| C1 | **verify-only RSA** for external keys: `SignatureAlgorithm.RsaPkcs1Sha256`, `RsaPssSha256`; `SignatureVerifier` from a JWK/`SubjectPublicKeyInfo` marked *external* (⚑AN11) | Google/Microsoft/Apple ID tokens, passkeys RS256 | P3 — **✅ built 2026-10-18** (`ExternalPublicKeys`, `SignatureVerifier` RS256/PS256; crypto 12 §5) |
| C2 | `OneTimePassword.Totp/Hotp` (HMAC-SHA1 protocol-mandated, SHA-256 option), constant-time compare (⚑AN12) | authenticator apps | P5 — **✅ built 2026-10-19** (`OneTimePassword`, crypto 04 §13, 12 §5) |
| C3 | JWK export of a `SigningKeyRing`'s public keys (EC P-256 `x`/`y`, `kid` = `KeyId`) — or confirm `PublicKeyRecord.SubjectPublicKeyInfo` is enough for us to write it | JWKS endpoint | P1 |
| C4 | confirm `MessageAuthentication` with a `KeyRing` pepper is acceptable as "PBKDF2 input pre-hash" (no new primitive) | 04 §2 | P1 |
| C5 | import of a developer-provided P-256 PKCS#8 key (Apple `.p8`) into a signing ring | Apple client secret | P3 — **✅ built 2026-10-18** as a fixed `ImportedSigningKey` (not a ring: rings rotate) + `ISecretStore.ImportSigningKeyAsync` |

### 2.3 Others

| Library | Request | Status |
|---|---|---|
| YFex core | `EmailAddress`, `PhoneNumber` (⚑AN3); `ICanonicalValue` on both | P0 |
| YFex.Domain (Entity.Public) | `Email.ToEmailAddress()`, `Telefone.ToPhoneNumber()` conversions | P0, owner's call |
| YFex.Offline | agree the credential-source contract and the operator-credential split (⚑AN18); site verifier payload stays Offline's | **✅ 2026-10-19**: Offline defines `ISiteCredentialSource` + `SiteCredentialVerifier` (Phase 4's bundle publisher reads it), `OfflineSignIn.CheckAsync` (unlock) and NFC secrets; terminal caching stays `OfflineSignIn.RememberAsync` |
| YFex.Installations | none new: assertion over HTTP for refresh binding uses the existing installation assertion (⚑I11); confirm an HTTP header form exists or add one | **✅ 2026-10-19**: it did not exist — added `InstallationAssertionHeaders`, `HttpInstallationAssertionChallengeSource`, `InstallationAssertionHandler` (core) and `UseYFexInstallationAssertion` / `MapYFexInstallationAssertionChallenge` (Central.AspNetCore) |
| YFex.Web | none new: provider/sender clients use `AddApi` + per-scope endpoints and sealed secrets | — |
| YFex.Messaging.Rpc | none: implement `ICallTokenValidator` | — |
| YFex.Audit | map `AuthenticationEvents` to entries (11 §5) | after P2 |
| YFex.Privacy | none: core attributes on the new value objects | — |

## 3. Dependencies

| Needs | From | Status | Phase here |
|---|---|---|---|
| ⚑AN1–AN25 | user | **pending** | all |
| `PasswordHashing`, `MessageAuthentication`, `MessageToken`, `SigningKeyRing`, `SealedValue`, `ScopedKeyRings` | YFex.Cryptography 0–3 | ✅ built | P1 |
| RSA verify (C1), imported P-256 key (C5) | YFex.Cryptography | ✅ built 2026-10-18 | P3 |
| TOTP (C2) | YFex.Cryptography | ✅ built 2026-10-19 | P5 |
| `ActorContext`, `AmbientPrincipalAccessor`, `IAuthorizationSignals`, catalog generator, approvals | YFex.Authorization | ✅ built | P1–P5 |
| `ICallTokenValidator` | Messaging.Rpc | ✅ built | P1 |
| per-scope endpoints, sealed secrets | YFex.Web (+ `.Data`) | ✅ built | P2–P3 |
| Offline Phase 3 offline login | YFex.Offline | ✅ 3a–3d built 2026-10-17 (terminal verifiers, offline session) | P4 |
| installation assertion | Installations.Rpc | ✅ built | P4 |

## 4. Phases

### AN-P0 — Core value objects and repo skeleton

- Core `EmailAddress`, `PhoneNumber` (+ tests, golden canonical bytes); conversions in Entity.Public (with the Domain owner).
- Repo `C:\Dev\YFex\YFex.Authentication` (`authentication.slnx`): `.Public`, engine, `.Testing`, `.Tests`, `.AotSmoke`, `.Benchmarks`;
  architecture test: `.Public` references only core + BCL + Authorization.Public; no direct crypto types outside YFex.Cryptography.
- **Acceptance:** core tests green (parse/format/mask, E.164 edge cases: `+1`, `+351`, `+55 11 9…`, 15-digit max); AOT smoke 0 warnings.

### AN-P1 — Accounts, passwords, sessions, tokens

- `.Public` types (02), engine: realms, accounts, password policy + verifiers + pepper + upgrade, operator code + PIN, sessions,
  flow tokens, access/refresh tokens (own JOSE ES256), validator with cache, revocation set, JWKS, throttling (in-memory),
  `AuthenticationEvents`, `AuthenticationPermissions`, in-memory stores; `.AspNetCore` handlers + endpoints for password/operator
  sign-in, refresh, sign-out; `.Rpc` validator; `AuthenticationHarness`.
- **Acceptance:** enumeration tests (unknown vs wrong password: same body/status, timing within 15 %); refresh reuse revokes the family;
  installation-bound refresh refused from another installation (harness with Installations.Testing); validator golden vectors
  (our tokens, RFC 7515 A.3 ES256 example), `alg` confusion tests (`none`, `HS256` with the public key, header/key mismatch); budgets
  of 13 §1 met; Authorization receives the principal (HTTP + RPC) and `ActorScope.SessionId` is the session.

### AN-P2 — Challenges, codes, links, delivery, verification, reset, registration

- Challenges + codes + links (05), contact verification, password reset, registration (self-service, invitation, admin), lifecycle
  (08 §1–§5); `.Delivery`: SMTP, HTTP e-mail, Twilio (SMS/WhatsApp/Verify), Meta WhatsApp; budgets and country allow-list; `.Data`
  stores for P1+P2 tables.
- **Acceptance:** a code works once, never across purposes, dies on password change; resend cooldown; destination limits; WhatsApp
  `NotOnWhatsApp` falls back to SMS; budget exhaustion refuses with an alert; no code/destination in any log line (log capture test);
  `.Data` tests on the YFex.Data test database; AOT 0 warnings including `.Delivery`.

### AN-P3 — External providers (split: **P3a ✅ 2026-10-18**, **P3b ✅ 2026-10-18** — §5; recorded real tokens pending)

- `.OpenIdConnect`: discovery, PKCE, `iss` check, ID-token validation (C1), JWKS cache, Google, Microsoft, Apple (C5), gov.br, GitHub,
  generic; linking rules (06 §5); per-scope providers + directory; native ID-token exchange; impersonation (07 §7, R4).
- **Acceptance:** recorded real ID tokens (Google, Microsoft work + personal, Apple) validate; tampered ones fail with the right
  `TokenRejection`; nOAuth test (Entra token with someone else's e-mail and no `xms_edov`) never links; `TestIdentityProvider` full
  flow in-process; per-tenant Entra configured at run time without restart.

### AN-P4 — Desktop client, shared terminals, offline bridge — **✅ 2026-10-19** (§5; Avalonia sample and Linux/macOS spikes pending)

- `.Client` (10 §1–§2, §4): system browser + loopback (06 §2.2), sealed token storage, silent refresh, lock/quick switch,
  `AmbientPrincipalAccessor` + RPC reconnect; `.Offline` bridge (10 §3) with the Offline session.
- **Acceptance:** Avalonia sample signs in with PIN, Google and code; lock/unlock; RPC reconnects with the refreshed token; Offline's
  standalone sign-in works with the cached verifier created at the last online sign-in; spikes on Linux/macOS for browser launch and
  loopback listener recorded.

### AN-P5 — Second factors and step-up — **✅ 2026-10-19** (§5; SQL device-sign-in table and passkey second factor pending)

- TOTP (C2), recovery codes, MFA policy (`RequireFor`, remember device, enrolment grace), step-up endpoint, `IReauthentication` for
  inline approvals, Authorization R1–R3/R6, sign-in on another device (10 §5).
- **Acceptance:** RFC 6238 test vectors; TOTP step replay refused; `ChangePriceTable` denied with `ReauthenticationRequired` then
  allowed after step-up; inline approval at a till verifies the supervisor's PIN without creating a session.

### AN-P6 — Passkeys — **✅ 2026-11-12** (§5; in the engine, not a `.Passkeys` package; sample-app browser test pending)

- `.Passkeys`: WebAuthn registration/assertion, span CBOR/COSE reader, ES256/RS256, sign-counter checks.
- **Acceptance:** WebAuthn conformance-style vectors (packed/none attestation samples), a browser test in the sample app.

### AN-P7 — Device session profiles and off-site rule (with installations Phase I6; ⚑AN26–AN28, 07 §2.1)

- Optional `Sessions(s => s.ForApplication(…))` profiles; `presence_at` from the verified installation presence; off-site rule
  (`Reauthenticate` / `Lock` / `SignOut`) on refresh; end the sessions of a revoked installation on Installations' event; Android token
  storage over `YFex.Cryptography.Android`.
- **Acceptance:** no profile = today's behaviour (regression tests); a profile changes idle/absolute only for its application; a session
  without fresh presence past `after` gets `ReauthenticationRequired(minimum)` and a step-up restarts the clock; a forged or other-site
  presence is ignored; revoking an installation refuses its next call at once; AOT 0 warnings.

## 5. Implementation log

### 2026-11-12 — AN-P6 (passkeys)

**Built** in `YFex.Authentication` (`src/YFex.Authentication/Passkeys/`, `SignInService.Passkeys.cs`) + `.Public` + `.AspNetCore`:
- `CborReader` (ref struct, WebAuthn subset: ints, byte/text strings, definite arrays/maps; simple values and floats only skipped;
  indefinite lengths, tags and nesting > 8 refused; a failed read consumes nothing); `CoseKey` (EC2/P-256/ES256 `-7`, RSA/RS256 `-257`)
  into a `PublicKeyRecord` through Cryptography's `ExternalPublicKeys` (point-on-curve, RSA size/exponent checked there);
  `AuthenticatorData` (RP id hash, flags, counter, attested credential data, extensions skipped, no trailing byte); `ClientData`
  (`Utf8JsonReader`); `PasskeyResponse` (the browser's `PublicKeyCredential.toJSON()`, ≤ 64 KiB); DER → P1363 for ES256 signatures
  (`System.Formats.Asn1`, encoding only). No new NuGet package, no crypto primitive outside YFex.Cryptography (architecture test green).
- `PasskeyService` (public): `StartRegistrationAsync` / `CompleteRegistrationAsync(caller, credentialJson, label)` / `ListAsync` /
  `RemoveAsync`. Registration: discoverable credential required, user verification required, attestation `none`, `excludeCredentials`,
  `credProps`; needs the sensitive-operation window (09 §5); first factor issues recovery codes; contacts notified; events
  `second-factor-enrolled/removed` with method `passkey`.
- `SignInService`: `StartPasskeyAsync(realm)` + `WithPasskeyAsync(realm, credentialJson, context)` (discoverable sign-in, user handle =
  account id; `{passkey}` alone is `PhishingResistant`, so no second factor); `StartSecondFactorPasskeyAsync(flow, context)` (completes
  through `CompleteSecondFactorAsync` with `SecondFactorProof(Passkey, credentialJson)`); `StartStepUpPasskeyAsync(caller)` (completes
  through `StepUpAsync`). Throttled per credential id + IP; every failure is `InvalidCredentials`.
- `SecondFactorService`: a passkey is an enrolled factor in the policy (`Allowed`, possession factors that lift a password, recovery codes
  usable/regenerable, `SecondFactorStatus.Passkeys`); removing the authenticator keeps recovery codes while passkeys remain and is not
  refused by a role requirement then.
- Options `auth.Passkeys(p => p.RelyingParty(id, origins…))`, per realm `RelyingParty(realm, id, origins…)`, `Name`, `CeremonyLifetime`
  (5 min), `MaxPerAccount` (10, cap 32). Origins validated at configuration (https, or http on loopback; host = RP id or a subdomain;
  `android:apk-key-hash:` / `ios:` kept verbatim).
- HTTP: `POST sign-in/passkey/start`, `sign-in/passkey`, `sign-in/second-factor/passkey`, `step-up/passkey`, `GET factors/passkeys`,
  `POST factors/passkeys/start|confirm|remove`; `sign-in/second-factor` and `step-up` accept `"credential": {…}` with method `passkey`;
  `GET factors` returns `passkeys`. Options are WebAuthn L3 JSON for `PublicKeyCredential.parse…OptionsFromJSON`.
- `SecondFactorRefusal.InvalidResponse` (7), `LimitReached` (8).

**Acceptance met (tests):** 33 new (245 total, Windows Release and Linux `yfex-linux` Debug): ES256 and RS256 registration + discoverable
sign-in; packed self-attestation and extension data accepted; passkey as second factor after a password (`Password + Passkey` =
`PhishingResistant`) and a sign-in ceremony refused there (purpose sealed); step-up of the same session; one refusing case per check
(other origin, unlisted subdomain, wrong type, other RP id, UV/UP missing, BS without BE, corrupted signature, cross-origin, top origin,
no user handle, foreign challenge, expired ceremony, unknown credential, another account's handle, replayed assertion); cloned
authenticator (counter not growing) refused, zero counters keep working; duplicates, foreign ceremonies, stale sessions, the per-account
limit; removal; off until configured; configuration refusals; stored format v1 golden; CBOR malformed inputs; DER edge cases; **pinned
vectors** (ES256 packed + RS256 none, generated once with discarded BCL keys); HTTP end to end over TestServer. AOT smoke registers and
signs in with a passkey: **0 IL warnings, exit 0**.

**Deviations / decisions taken while building:**
- **No `.Passkeys` package:** the checks plug into engine internals (second-factor policy, `ProveAsync`, sign-in throttling, flows); a
  separate package would have needed a new seam interface for one implementation. Lives in the engine under `Passkeys/` (rule: one
  package per library).
- **Challenge = sealed ceremony token** (ring `authentication/factors`, domain `yfex.authentication.passkey.ceremony.v1`: purpose, flow
  id, expiry, security stamp, account, realm), consumed once with `IChallengeStore.TryConsumeFlowAsync` — not a `Challenge` row (those
  are delivered codes). No ceremony state is stored; any node opens it.
- **Storage:** all passkeys of an account in **one** `CredentialKind.Passkey` record (format v1, `StoredPasskeys`), like recovery codes,
  so the one-record-per-kind store and its compare-and-set serve counter updates — no store contract or SQL change. The key is kept as
  the platform-encoded SubjectPublicKeyInfo, not the COSE bytes.
- Attestation statements and the AAGUID are never verified or stored (attestation `none` policy; any `fmt` is accepted and skipped).
- `user.name` and `user.displayName` are the account's display name (no contact goes into the authenticator).
- Credential ids are unique per account, not indexed across accounts: sign-in finds the account by the user handle first, so the same id
  on two accounts cannot cross over.

**Not done / pending:** browser test in a sample app (no Authentication sample app exists, and a real authenticator needs a person; the
software authenticator in the tests drives the same JSON); passkey enrolment inside a `MustEnrol` sign-in (offers the authenticator only);
passkeys in `IReauthentication` inline approvals (tills use PINs); `.Client` methods for passkeys (desktop goes through the system
browser, 06 §2.2; native platform APIs open); EdDSA (`-8`) keys; WebAuthn signal API / related origins.

### 2026-10-19 — AN-P5 (second factors, step-up, inline re-authentication, sign-in on another device)

Asked by the session that ran P4 ("Implement phase AN-P5 … ask/implement YFex.Cryptography request C2 … Authorization requests R1–R3 and
R6 … the user allows changing other libraries freely (not in production) as long as their design docs are aligned"). No cryptography
or authorization session was running, so C2 and R1–R3/R6 were built here (logs: crypto 12 §5, authorization-consolidation 09 "Implementation log").
Before: 194 tests. After: **210 green** (+16: `SecondFactorP5Tests` 13, `SecondFactorP5HttpTests` 2, one SQL test), solution 0 errors,
native AOT smoke 0 IL warnings, exit 0 (now with enrolment, TOTP replay, step-up, the recent-authentication requirement, the inline
proof and device sign-in). Other repos, all green: Cryptography 220 (+30), AotSmoke 37/37; Authorization 156 (+5); Messaging.Rpc 81
(+1); Messaging 185 (+1, 2 skipped); Cqrs.Approvals 12; Offline 69; Validation 62, Validation.Generator 50, Rules.Admin 29. Backups: `C:\Temp\authentication-backup\P5` (tar of this repo,
and under `other` the Authorization and Cryptography repos, `YFex.Cqrs` + `YFex.Messaging`, the Validation VM emitter, the
Offline file and the design folders). No new NuGet package.

**Built:**
- **Cryptography C2:** `OneTimePassword` (HOTP/TOTP, SHA-1 default + SHA-256/512, fixed-time window, `Replayed` for steps at or before
  the last accepted one, base32), `OneTimePasswordParameters`; `HMACSHA1.HashData` banned outside the library.
- **Authorization R1–R3, R6:** `AssuranceLevel` + `AuthenticationFacts` + `RecentAuthenticationRequirement` in `.Public` (Authentication
  imports the enum with a global alias in `Directory.Build.props`, `AuthenticationPrincipal.AcrOf` delegates); `Requirement` case 8,
  `PolicyEvaluator(…, TimeProvider)`; `DenialKind.ReauthenticationRequired` + `Denial.RecentAuthentication`; Cqrs
  `ErrorKind.ReauthenticationRequired` (+ RPC v7, VM generator code `server.reauthenticate`, Offline central finding); HTTP 401 with
  `WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age, acr_values` (RFC 9470); verbs
  `.RequireRecentAuthentication(maxAge, minimum)`; `IRoleDirectory` + `FakeRoleDirectory`; `TestCallerBuilder.AuthenticatedAt`.
- **Engine:** `SecondFactorService` — authenticator enrolment (sealed pending secret in the token, `otpauth://` URI with the masked
  e-mail, first code proves the app; ring `authentication/factors`), recovery codes (10, Crockford, peppered PBKDF2 100 000 with a
  2-byte keyed hint), status, removal (refused while required by role; other sessions revoked), the 09 §3 policy (`RequireFor` via
  `IRoleDirectory`, `RequireWhen`, `RememberDevice`, `GracePeriodForEnrollment`, `AllowCodes` for accounts not required by role), all
  writes of a used step/code through the new `ICredentialStore.ReplaceAsync` compare-and-set (in-memory + SQL). `SignInService`:
  every first factor goes through the policy (`FinishAsync`, forced password change) → `SecondFactorRequired { Allowed, MustEnrol }`;
  `CompleteSecondFactorAsync`, `StartSecondFactorCodeAsync`, `StartAuthenticatorEnrolmentAsync(flow)` / `ConfirmAuthenticatorEnrolmentAsync(flow, …)`
  (the owed enrolment opens the session), `StepUpAsync` / `StartStepUpCodeAsync` (09 §4.1 as built: one proof per call, `StepUp` flow
  for the second, the same session gets the step-up's methods and a new `auth_time`, new access token only), and it is the
  `IReauthentication` (operator code / identifier + PIN, password, offline secret or authenticator code, optional authenticator code as
  second factor; throttled per person + installation). `SessionManager.StepUpAsync`. `InlineApprovalService` (§4.2: proof without a
  session → Authorization context of a session-less principal → permission, separation of duties, minimum assurance →
  `IApprovalTokenIssuer`). `DeviceSignInService` + `IDeviceSignInStore` (in-memory) for 10 §5. A password reset no longer signs in
  an account that has or owes a second factor. New: `AuthenticationMethod.DeviceApproval`, `CredentialKind.EnrolmentGrace`, notices
  `RecoveryCodeUsed`/`SecondFactorAdded`/`SecondFactorRemoved` (pt/en/es templates), events `second-factor-enrolled/removed/failed`,
  `recovery-codes-issued`, `recovery-code-used`, `stepped-up`, `device-sign-in-approved`; `SignInContext.RememberedDevice`,
  `IssuedCredentials.RememberedDevice`, `Reauthenticated.Account/Methods/Assurance`, principals without a session have no `sid`.
- **Endpoints:** `sign-in/second-factor` (+ `/code`), `sign-in/enrolment/start|confirm`, `GET factors`, `factors/authenticator/start|confirm|remove`,
  `factors/recovery-codes`, `step-up` (+ `/code`), `device/start|approve|token`; `secondFactorRequired` JSON lists `allowed` and
  `mustEnrol`; browsers get the `__Host-yfex.device` cookie.
- **Client:** `CompleteSecondFactorAsync`, `StartSecondFactorCodeAsync`, `StartAuthenticatorEnrolmentAsync` / `ConfirmAuthenticatorEnrolmentAsync`
  (signed in, or the owed enrolment of a sign-in), `StepUpAsync` (keeps the refresh token, re-signs the ambient principal). The first
  factor's identifier and secret wait in memory until the second factor completes, so observers (the offline bridge) still get the
  password proof.

**Acceptance (tests):** RFC 6238 appendix B (18 vectors) + RFC 4226 in `OneTimePasswordTests` and the smokes; **TOTP step replay refused**
(the enrolment's step at sign-in, the same code in a new flow, 8 concurrent completions with one code → exactly one session, the same
over SQL); **`ChangePriceTable` denied with `ReauthenticationRequired`** (20 min after a multi-factor sign-in, through a refreshed token;
`Error.From` → `ErrorKind.ReauthenticationRequired` with the requirement) **then allowed after the step-up** of the same session
(password → `SecondFactorRequired` → authenticator), also over HTTP with the client; **inline approval at a till verifies the
supervisor's PIN without creating a session** (no session listed for the supervisor, token verifies with separation of duties; wrong
PIN, no permission, same person, too weak, lock after wrong PINs). Also: grace then enrol-only for managers, role directory fail-closed,
codes as second factor for shoppers only, remembered browser forgotten after a password change, reset without auto sign-in when a
factor exists, device sign-in (slow down, other installation refused, single use, denial, expiry).

**Deviations from the design:**
1. `AssuranceLevel` moved to YFex.Authorization.Public (R1 left the choice to the owner): one enum, no byte-compatible copy.
2. Step-up sets the session's methods to **what the step-up proved** (with `auth_time` now) instead of adding them to the old ones: an
   old password plus a fresh TOTP is not "multi-factor a minute ago". A weaker step-up can therefore lower `acr`.
3. `IReauthentication` returns the existing `ReauthenticationResult` (its `Reauthenticated` gained `Account`, `Methods`, `Assurance`)
   rather than a new `Verified/Rejected` union.
4. Inline approvals have no HTTP endpoint (`ApprovalToken` has no JSON codec; tills use the app's RPC command with Messaging.Rpc's
   formatter); `InlineApprovalService` is the API.
5. Device sign-in: the terminal session inherits the approving session's methods (plus `DeviceApproval`) instead of running the
   second-factor policy again; the store is in-memory only.
6. 09 §5 "sensitive operations always need a step-up": enforced for factor changes and recovery codes (5 minutes); the existing
   password change, contact and link flows keep their own checks (current password, 5-minute link window) — not retrofitted.

**Not done / pending:** SQL table for `IDeviceSignInStore` (in-memory per node); passkeys as second factor (P6); the Entra/gov.br MFA
hints still do not raise `acr` (06 `AssuranceHint` mapped only); risk rules beyond the `RequireWhen` delegate; an Avalonia sample of
the enrolment QR; Linux/macOS runs. P3/P4 leftovers unchanged.

### 2026-10-19 — AN-P4 (desktop client, shared terminals, offline bridge)

The user: "you can make those changes … you can change those other libraries without worrying to break contract … align the design
plan" (P3 leftovers stay for later). Before: 177 tests. After: **194 green** (+17: `ClientP4Tests` 16, one architecture test), solution
0 errors, native AOT smoke 0 IL warnings, exit 0 (now with the client, the session store, the offline bridge and the site source).
Other repos, all green: Authorization 151 (+1), Installations 170 (+1), Offline 69. Backups: `C:\Temp\authentication-backup\P4`
(tar of the repo before, plus every edited file of the other repos under `other`). No new NuGet package.

**Built:**
- **`YFex.Authentication.Client`** (app side, references no engine and no ASP.NET — architecture test): `IAuthenticationClient`
  (`WithPasswordAsync`, `WithOperatorPinAsync`, `StartCodeAsync`/`CompleteCodeAsync`, `CompletePasswordChangeAsync`,
  `WithProviderAsync`, `RestoreAsync`, `RefreshAsync`, `ReauthenticateAsync`, `Lock`/`UnlockAsync`/`ReportActivity`,
  `SetOfflineSecretAsync`, `SignOutAsync`, `Current`, `IsLocked`, `Changed`) over the existing HTTP endpoints, JSON read/written with
  `Utf8JsonWriter`/`JsonDocument` (no reflection). After a sign-in, in one place (10 §1): refresh token kept (`IClientSessionStore`,
  `KeyValueClientSessionStore` over the app's `IKeyValueStore`, which **must** be `EncryptedKeyValueStore` — refused otherwise; never on
  shared terminals), principal (`AuthenticationPrincipal`, now in `.Public`) signed into `AmbientPrincipalAccessor`, silent refresh at
  80 % (`TimeProvider` timer, single-flight, retry when central is unreachable, `Revoked`/`Expired` end the session here),
  observers told. Central unreachable reads as `NotAvailableOffline` (sign-in) / `SignInRejection.CentralUnreachable` (new, client-side).
  **External providers:** system browser (`IBrowserLauncher`, `SystemBrowserLauncher`) + loopback on `127.0.0.1:{random}` — a
  `TcpListener`, not `HttpListener` (needs a URL reservation for IP literals on Windows) — PKCE S256, then `POST external/handoff`.
  **Binding:** `BindToInstallation()` wraps the HTTP stack with Installations' `InstallationAssertionHandler` (challenges over HTTP
  when no RPC provider is registered). **Shared terminals:** `SharedTerminal(lockAfter)` = idle lock from `ReportActivity`, unlock =
  the same person's PIN/password/offline secret checked by central (`POST session/reauthenticate`), else by the offline verifier
  (`IOfflineUnlock`); "Trocar operador" = `SignOutAsync`. DI: `AddYFexAuthenticationClient(c => c.Server(…).Realm(…)
  .StoreTokensSealed().BindToInstallation().SharedTerminal())`; the client is `IDisposable` too (sync-disposed containers — found by
  the AOT smoke).
- **Engine/endpoints:** `SignInService.ReauthenticateAsync` (password, PIN or offline secret of the caller's own account, throttled per
  account+installation, impersonated sessions refused, no session opened, event `authentication.reauthenticated`) →
  `POST session/reauthenticate`; `AccountService.SetOfflineSecretAsync` (04 §5: `OperatorCredentials(o => o.OfflineSecrets())`, ≥ 10
  characters, not digits only, password policy, sign-in ≤ 15 min old, peppered + site verifier) → `POST credentials/offline-secret`;
  new method `AuthenticationMethod.OfflineSecret`; `AccountService.ListSiteVerifiersAsync` + `IAccountStore.ListByHomeScopeAsync`
  (in-memory + SQL); session JSON now carries `homeScope`, `authenticatedAt`, `client`, `installation`, `realActor`, `idleExpiresAt`,
  `absoluteExpiresAt`; `YFexAuthenticationSchemeOptions.InstallationOf` defaults to the core `InstallationIdentity` feature that
  Installations' HTTP middleware sets (⚑AN16 over HTTP without app code).
- **`YFex.Authentication.Offline`** (terminal): `AuthenticationOfflineBridge` (`AddYFexAuthenticationOffline()`), an
  `IAuthenticationClientObserver` + `IOfflineUnlock`: keeps Offline's verifier at online password sign-in under the typed identifier
  (lower-cased; operator codes as typed) — PINs are offered and Offline refuses them on lone terminals; `NeedsOfflineSecret` +
  `CaptureOfflineSecretAsync(login, secret, setAtCentral)` for provider/code sign-ins (a second terminal proves the existing secret
  through re-authentication); drops the person's verifiers when central refuses the session (`AccountUnavailable`, `Revoked`,
  `ReuseDetected`).
- **`YFex.Authentication.Offline.Central`**: `AuthenticationSiteCredentialSource` = Offline's `ISiteCredentialSource`
  (`AddYFexAuthenticationSiteCredentials()`; realm of a site = the single shared realm or the per-scope realm at the configured level).
- **Other libraries:** Authorization R4 (`RealActorOf`, `ActorScope.From` + `sid`); Installations HTTP assertion form (above); Offline
  `ISiteCredentialSource`/`SiteCredentialVerifier`/`SiteCredentialKind`, `OfflineSignIn.CheckAsync` + `OfflineCredentialCheck`, NFC
  normalisation of offline secrets (same as Authentication's verifiers).
- **Testing:** `TestSystemBrowser` (drives start → `TestIdentityProvider` → callback → real loopback request).

**Deviations from the design:**
1. **No RPC reconnect** (⚑AN20 said "the client then (re)connects RPC with the token"): Messaging.Rpc has no client-side connect-time
   token, but it has the per-call `SendToken`; apps use `ctx.SendToken(AuthenticationClient.RpcToken)` and the server
   `FromConnection().OrFromHeader("Authorization")`, so a refreshed token applies to the next call with no reconnect.
2. **Unlock is a re-authentication endpoint** now (P5's `IReauthentication`/step-up will build on `ReauthenticateAsync`), not a P5 type.
3. **03 §2 step 4** said central sends Ana's offline PIN verifier in the sign-in response: it does not (a PIN verifier on a terminal is
   refused by ⚑O12). PIN people get offline sign-in from the site broker's verifier bundle (Offline Phase 4 publishes
   `ISiteCredentialSource`), or an offline secret.
4. `QuickSwitch()` builder verb dropped: switching is `SignOutAsync` then a new sign-in (10 §2 already says the session is signed out).
5. Second-factor completion (`CompleteSecondFactorAsync`) is not in the client yet: no server endpoint exists until P5.

**Not done / pending:** the Avalonia sample app (acceptance "signs in with PIN, Google and code" is covered by `ClientP4Tests` over
TestServer + real loopback TCP, not by a UI); browser launch and loopback spikes on Linux/macOS (code paths: `xdg-open`/`open`); a
real `BindToInstallation` run against an enrolled installation (the handler is tested in Installations, the binding in
`ClientP4Tests` through the `InstallationIdentity` feature); Offline Phase 4's verifier-bundle publisher (consumer of
`ISiteCredentialSource`); the shared service host (⚑H1 decided 2026-10-19: new repo `YFex.Hosting`, built with Offline Phase 4 — [hosting](../hosting/README.md)). P3 leftovers unchanged (recorded real ID
tokens, gov.br registration, back-channel logout, ID-token benchmark, Linux run).

### 2026-10-18 — AN-P3b (external providers, second slice)

Asked by the session that ran P3a ("Build AN-P3b … scope = the P3b list of the AN-P3a entry"). Every ⚑AN of README §7 treated as
confirmed. Before: 145 tests. After: **177 green** (+32: `ExternalProvidersP3bTests` 24, `ExternalProvidersP3bHttpTests` 3,
`SqlExternalStoreTests` 5), solution 0 errors (only the existing NU1507 of YFex.Data), native AOT smoke 0 IL warnings, exit 0 (now with
scoped providers, required providers, contact verification, gov.br, the handoff and impersonation). Backups:
`C:\Temp\authentication-backup\P3b`. No new NuGet package; `.OpenIdConnect` gained a project reference to YFex.Web (client secrets of
scoped providers). No YFex.Authorization code was touched.

**Built:**
- **Per-scope providers (⚑AN9, 06 §6).** Engine: `ScopedProviderRecord` (scope, name, kind `OpenIdConnect | MicrosoftTenant |
  GoogleWorkspace`, client id, `ClientSecretReference` = YFex.Web endpoint name of the scope's tenant, authority / tenant id / hosted
  domain, scopes, `TrustEmail` + `Domains`, `Enforcement`, auto-link, registration, enabled, version; `Problem()` validation),
  `IScopedProviderStore` + in-memory, `ScopedProviderDescriptor` (the `descriptor` JSON, Utf8JsonWriter/Reader, never a secret).
  `.OpenIdConnect`: `IIdentityProviderDirectory` / `IdentityProviderDirectory` (static + scoped providers, cached per scope 1 min,
  invalidated at once on `SaveAsync`/`RemoveAsync` of this node; runtimes keep discovery/JWKS caches while the record's version is
  unchanged; `SaveAsync` refuses static names, invalid records, stale versions), `IdentityProviderList.ForEmail` (home-realm discovery).
  `ExternalSignInFlow` resolves providers through the directory (state v2 carries the scope); `GET providers?realm=&scope=&login_hint=`
  returns `icon`, `scope` and `discovered`. Client secrets of scoped providers: `IApiEndpointStore.GetAsync(reference, tenant)` →
  `OAuthClientCredential.Secret` (or Basic/ApiKey).
- **`.Data`:** `SqlExternalLoginStore` (`authentication_external_logins`: `subject_lookup` MAC, `subject_sealed`), `SqlScopedProviderStore`
  (`authentication_providers`: descriptor text + version CAS), schema for SQLite/PostgreSQL/SQL Server, `AddYFexAuthenticationData`
  registers both.
- **Subjects sealed everywhere (12 §3).** `ExternalLoginRecord.SealedSubject` (ring `authentication/sealing`, bound to the lookup key),
  `ExternalSignInService.RevealSubjectAsync` (admin reveal, gate + audit by the caller); `ExternalAccountKey` classified
  `[Sensitive(PersonalIdentifier)]`. The pending link flow is now **sealed** (domain `external-link.v2`) instead of a MessageToken whose
  payload — the subject — was readable (P3a issue, fixed: a gov.br CPF would have travelled in the `__Host-yfex.link` cookie).
- **gov.br (06 §3.4):** `.GovBr(g => …)` (`Staging()`, `ReliabilityLevels(bool)`, `TrustContacts(bool)`), `GovBrClaimsMapper`: subject
  must be a valid CPF (check digits), e-mail/phone trusted only with their `*_verified` claims, auto-link off, client auth HTTP Basic,
  reliability levels via `{api}/confiabilidades/v3/contas/{cpf}/niveis?response-type=ids` (bronze → `SingleFactor`, prata/ouro →
  `MultiFactor`, hint only). **To confirm with a real client registration** (claim names, issuer text, API, Basic auth).
- **`ContactVerificationRequired` (06 §5):** when registration is allowed but the provider gives no contact the realm accepts, a sealed
  flow (domain `external-contact.v1`, 10 min) is returned; a code goes at once to the contact the provider stated but does not vouch for,
  else `CodeSentTo` is null and `StartContactVerificationAsync(flow, identifier, channel)` sends one to a typed contact.
  `CompleteContactVerificationAsync` creates the account with that contact (and any trusted one) verified, links the login, opens the
  session (methods External + the channel's code); a contact owned by an existing account answers `LinkRequired` (never merges). Endpoints
  `POST external/contact/start|complete` (`__Host-yfex.contact` cookie for browsers). `ContactVerificationRequired.CodeSentTo` became
  nullable. Closed registration still refuses with `AccountUnavailable`.
- **`ProviderEnforcement.Required` (06 §5):** `IProviderEnforcement` in the engine (answered by the directory: static `RequiredFor(domains)`
  and scoped records); passwords and codes (`SignInService.FinishAsync`), other providers (`ExternalSignInService`), self-service sign-up
  of the domain (`NotAllowed`) and the sign-in after a reset are refused with the new `SignInRejection.ExternalProviderRequired` (only after
  the method itself succeeded). Operator PINs are not affected (a terminal credential).
- **Desktop/mobile handoff (06 §2.2):** `ExternalStartRequest(AppRedirectUri, AppCodeChallenge)`; loopback IP literals only
  (`127.0.0.1`/`[::1]`, any port, no `localhost`) or exact `AllowAppRedirect(uri)` entries; client must be desktop/mobile. The callback
  redirects to `{redirect}?handoff=…` (sealed identity + policy, domain `external-handoff.v1`, 60 s, single use) and opens no browser session;
  `POST external/handoff { handoff, codeVerifier }` → `RedeemHandoffAsync` checks expiry, S256(verifier) = challenge (fixed time), consumes
  the id, then runs 06 §5 with the app's own request context (its installation binds the refresh token). Shape for P4 in 06 §2.2.
- **Impersonation (07 §7, ⚑AN17):** `ImpersonationService.StartAsync(IAuthorizationContext, AuthenticatedCaller, target, reason, context)`:
  Authorization's context of the caller's own principal must grant `authentication.impersonate-user`; reason mandatory (≤ 200); no chains,
  not oneself; caller session ≥ `ImpersonationOptions.MinimumAssurance` (default MultiFactor) within 15 min; target realm = caller's or
  `AllowAcross(from, to)`. New session with `RealActor`, 15 min idle / 1 h absolute (`ImpersonationOptions.Lifetime`, also for refresh and
  sliding), tokens only (never a cookie). `EndAsync` signs the impersonated session out. Events `ImpersonationStarted/Ended/Refused` carry
  both actors. Endpoints `POST impersonation/start` (fails closed without `IAuthorizationContextFactory`) and `…/end`.
- **R4 (principal shape):** no Authorization session was live; checked against its code and authorization-consolidation 06 §3 (user
  impersonation = RFC 8693 `act` → `Actor` = target, `RealActor` = support, `UserImpersonation`). Our principal (`NameIdentifier` = `sub`,
  `ClaimsIdentity.Actor` = real actor) and `AuthenticatedCaller.ToActorScope()` match it. **Gap on the Authorization side (request):**
  `AuthorizationContextFactory.CreateAsync` never reads `ClaimsIdentity.Actor`, so an `IAuthorizationContext` built from an impersonated
  principal has `RealActor = null`, `Relation = None` and `ImpersonationRestrictions` do not apply yet. Note in the shared session folder.

**Deviations / decisions:**
- `authentication_external_logins` primary key is (realm, subject_lookup) — the lookup MAC already covers provider, issuer and subject;
  (provider, issuer, subject_lookup) of the 12 §2 sketch would allow nothing more.
- A scoped provider is offered and signs people in **only in the per-scope realm of its scope** (or for a scope beneath it); never in a
  shared realm, so a tenant's administrator cannot mint identities for other tenants or for shared accounts. Scoped providers default to
  auto-link off.
- The P3a test "unverified or foreign-domain Google e-mail is never matched" and the nOAuth test now expect `ContactVerificationRequired`
  (the e-mail is still never matched; P3a refused instead).
- Administration of scoped providers is `IdentityProviderDirectory.SaveAsync/RemoveAsync`; the permission
  `authentication.providers.configure` is the app's gate (no HTTP admin endpoint).
- Impersonation needs `MultiFactor` by default, which only exists with P5 (or a password + code session); options lower it.
- Farm invalidation of scoped providers is the 1-minute cache (or `Invalidate(scope)` from the app's change feed).

**Not done / pending:** Authorization reading `ClaimsIdentity.Actor` (request above); the desktop client side of the handoff (P4:
loopback listener, system browser); recorded real Google/Microsoft/Apple/gov.br ID tokens (no accounts on this PC); gov.br confirmation
with a real registration; back-channel/`sid` logout of required providers; BenchmarkDotNet budget for ID-token validation; Linux run.

### 2026-10-18 — Cryptography C1 + C5, then AN-P3a (external providers, first slice)

The user asked to build Cryptography C1 and C5 here ("add C1 (RSA verify for external keys) and C5 (fixed P-256 key import from
PKCS#8) to YFex.Cryptography with tests and the AOT check, then start AN-P3"); the cryptography sessions were idle. Log of that part:
cryptography 12 §5 (2026-10-18 entry): verify-only `SignatureAlgorithm.RsaPkcs1Sha256`/`RsaPssSha256`, `ExternalPublicKeys`,
`ImportedSigningKey` + `ISecretStore` extensions; crypto 190 tests, AOT 36/36.

AN-P3 is split. **P3a (built):** model + linking rules, the OIDC/OAuth 2 relying party, Google, Microsoft, Apple, GitHub, generic OIDC,
native ID-token exchange, account-page link/unlink, endpoints, `TestIdentityProvider`. **P3b (open):** per-scope providers
(`IIdentityProviderDirectory`, `IScopedProviderStore`, run-time Entra per tenant), `.Data` tables `authentication_external_logins` /
`authentication_providers`, gov.br (CPF subject sealed + `[Sensitive]`), the desktop/mobile handoff (`POST external/handoff`, 06 §2.2,
with P4), `ContactVerificationRequired` when a provider gives no trusted contact, `ProviderEnforcement.Required`, impersonation (07 §7, R4),
recorded real Google/Microsoft/Apple ID tokens (no accounts on this PC).

Before: 112 tests. After: **145 green** (+33 `OpenIdConnectTests`), solution 0 errors (only the existing NU1507 of YFex.Data), native AOT
smoke 0 IL warnings, exit 0 (now with a Google flow and the external endpoints). Backups: `C:\Temp\authentication-backup\P3a`.

**Built:**
- `.Public`: `ExternalIdentity`, `ExternalSignedIn`, `LinkRequired`, native unions `ExternalSignInResult` and `ExternalLinkResult`
  (`ExternalLoginLinked`/`Unlinked`/`ExternalLinkRefused` + `ExternalLinkRefusal`); `TokenRejection.NonceMismatch`/`AuthenticationTooOld`;
  events `ExternalLoginLinked`/`Unlinked`/`ExternalSignInFailed` (provider names only); `ExternalAccountKey.ToString` never prints the subject.
- Engine: `IExternalLoginStore` + in-memory (lookup key = MAC of realm ‖ provider ‖ issuer ‖ subject, lookup ring; the subject is not
  stored), `ExternalSignInService` with 06 §5 exactly: linked → sign in; trusted e-mail of a verified owner → auto-link (policy) or
  `LinkRequired` (MessageToken flow, consumed once, bound to the account and its security stamp); nobody → registration only with a trusted
  contact that satisfies `RequireVerified`; an untrusted e-mail is never looked up. Link from the account page within 5 minutes of the
  sign-in; unlink refused for the last way to sign in (password, PIN, passkey, code sign-in with a usable contact, another login).
  `AccountService.DeleteAsync` also removes the external logins. Session method = `AuthenticationMethod.External`.
- `YFex.Authentication.OpenIdConnect` (new package): sealed `state` (AEAD, ring `authentication/sealing`) carrying the PKCE verifier,
  nonce, browser binding, return URL and link target — nothing stored per pending flow, flow id consumed once; PKCE S256 always;
  RFC 9207 `iss` (required when advertised, template providers checked against the token); discovery cached a day and checked
  (issuer = authority, https endpoints); JWKS → `ProviderKeySet` via C1 (one algorithm per JWK: its `alg`, else RS256/ES256), refresh on
  an unknown `kid` at most once per 5 minutes; `IdTokenValidator` (spans; `alg` must name the key's algorithm; `typ` JWT or absent;
  `crit`/`enc`/`zip` refused; `iss` rule; `aud` + `azp` when several audiences; `exp`/`nbf`/`iat` with 2 min skew; `nonce`;
  `auth_time` vs `max_age`). Providers: Google (`gmail.com` or `hd` = domain trusts the e-mail, `RequireHostedDomain`), Microsoft
  (tenant template from `tid`, optional allowed tenants, e-mail trusted only with `xms_edov`, `TrustEmailForDomains(tenant, …)` or a
  verified consumer e-mail; `amr: mfa` → assurance hint), Apple (client secret = ES256 JWT signed with the C5 key from `ISecretStore`,
  cached 50 min; `form_post`; name from the first `user` field; relay e-mails trusted and marked), GitHub (OAuth 2; numeric id; primary +
  verified e-mail only), generic OIDC (`EmailTrust.Never` default, `IfVerifiedClaim` optionally per domain; auto-link off). Native exchange:
  `IssueNonceAsync` (MessageToken, single use) + `ExchangeIdTokenAsync` (web or native client ids; Apple's SHA-256 hex nonce).
  Endpoints `MapYFexExternalSignIn("/auth")`: `providers`, `external/{p}/start` (flow cookie `__Host-yfex.flow`, SameSite=None only
  for `form_post` providers), `external/{p}/callback` (GET/POST; browser → session cookie + 302, every failure a 302 with
  `authentication=…&reason=…`), `external/nonce`, `external/{p}/id-token`, `external/logins`, `external/link/complete`
  (`__Host-yfex.link` cookie), `external/unlink`.
- `.Testing`: `TestIdentityProvider` (in-process issuer as an `HttpMessageHandler`: discovery on any host, JWKS, token endpoint with
  PKCE and client checks, GitHub user APIs, key rotation, pluggable RS256/PS256 signer); `AuthenticationHarness.External`.
- `.AspNetCore` response helpers are `internal` (InternalsVisibleTo `.OpenIdConnect`).

**Acceptance (tests):** tampered tokens fail with the right `TokenRejection` (signature, `none`, `HS256`, RS256 header on an ES256 key,
PS256 on an RS256-only JWK, `at+jwt`, `crit`, audience, issuer, nonce, expiry and skew, future `iat`, `max_age`); **nOAuth** — an Entra
token carrying someone else's e-mail without `xms_edov` never links; Microsoft tenant template and allow-list; RS256 and PS256 ID tokens
validate through C1; key rotation refreshes once and garbage `kid`s do not hammer the provider; `TestIdentityProvider` full flow
in-process, over HTTP too (TestServer: start → 302 + cookie, callback → session cookie, logins list, replay refused); replayed, unbound,
tampered, expired, other-provider states refused; RFC 9207 mix-up; open-redirect return URLs; Apple client secret verified with the
`.p8` public key; GitHub key = numeric id; native nonce single use, Apple hashed nonce; link from the account page needs a fresh session;
last sign-in method kept; disabled account refused; deletion erases logins.

**Deviations / decisions:**
- C5 is a fixed `ImportedSigningKey` kept sealed in `ISecretStore` instead of "a key in a signing ring" (14 §2.2): rings rotate and
  number keys themselves; the Apple key must never rotate and is named by Apple's key id.
- Client secrets come from YFex.Cryptography's generic `ISecretStore` (`ClientSecretFromStore(name)`) or configuration; YFex.Web per-scope
  sealed secrets are for P3b's scoped providers.
- No `ContactVerificationRequired` yet: an unknown person whose provider gives no trusted contact is refused (`AccountUnavailable`)
  when the realm requires a verified contact (P3b).
- Non-browser clients get JSON from the callback for now; desktop/mobile use the handoff of 06 §2.2 (P3b/P4).
- `AssuranceHint` is mapped (Entra `mfa`) but not used for `acr` until P5 (step-up).

**Not done / pending:** the P3b list above; a BenchmarkDotNet budget for ID-token validation (13 §1); Linux run.

### 2026-10-18 — AN-P0 (core types) + AN-P1 + AN-P2

The user asked to start implementing Phases 1 and 2 ("start implementing"); every ⚑AN recommendation of README §7 was taken as the
decision. Repo `C:\Dev\YFex\YFex.Authentication` (`authentication.slnx`, `Directory.Build.props` like Offline/Installations). Before: 0
tests. After: **112 tests green** (Windows; Linux pending — no Linux machine), solution build 0 errors / 0 warnings in our projects.

**Core (P0):** new files only — `YFex/src/YFex/Contacts/EmailAddress.cs`, `PhoneNumber.cs` (`ICanonicalValue`, `ILogValue` = mask,
hand-written `IClassifiedData` as `MacAddress`, STJ converters; ITU 1/2/3-digit country-code split, 15-digit max, national numbers
with a default country code, Italian trunk 0 kept). `Email.ToEmailAddress()`/`Telefone.ToPhoneNumber()` in YFex.Domain **not done**
(owner's call).

**Built (P1):** `.Public` (all value objects and unions of 02 §3–§5, `PasswordPolicy`, `AuthenticationEvents`); engine: realms, accounts
(CAS on `Version`), peppered PBKDF2 verifiers with dummy verification for unknown accounts, concurrency cap and upgrade on sign-in,
operator code + PIN (trivial PINs refused, lock 5 → 5 min doubling per installation, optional unpeppered site verifier), sessions,
own JOSE (ES256 writer, span reader, `alg` from the key, `crit` refused), validator with per-node cache + revoked set + installation
binding, JWKS writer and JWKS → directory reader, rotating refresh tokens (SHA-256 at rest, family reuse → family + session revoked,
10 s grace returns the same sealed successor, installation binding), browser session handles, flow tokens (MessageToken, consumed ids),
in-memory throttling (11 §2 rules), `AuthenticationPermissions` (Authorization generator), in-memory stores; `.AspNetCore` (bearer +
`__Host-yfex.session` handler, `MapYFexAuthentication`, `MapYFexJsonWebKeys`, RequestDelegate endpoints + STJ source generation);
`.Rpc` (`ICallTokenValidator`, never widens the scope); `.Testing` (`AuthenticationHarness`, `CapturingSender`).

**Built (P2):** `ChallengeService` (6-digit codes stored as MAC per purpose, 5 attempts, single use, stamp-bound, 60 s cooldown, 3/15 min
and 10/day per destination, e-mail links as MessageTokens, decoy challenges for unknown identifiers, fallback WhatsApp → SMS, budgets
and country allow-list, provider-verified mode, security notices); passwordless code sign-in; contact verification (unique per realm,
the other owner notified); password change/reset (code or link; all sessions revoked; `SignInAfterReset` option); self-service
registration and invitations; admin creation, temporary passwords, disable/lock/delete (tombstone); `.Delivery` (SMTP, SendGrid/SES
e-mail, Twilio SMS/WhatsApp/voice, Twilio Verify, Meta WhatsApp authentication template, pt-BR/en/es templates, per-scope endpoints
through YFex.Web `IApiEndpointStore`); `.Data` (SQL stores with hand-written `IRowConsumer`s + `QueryParameterBuilder`, CAS on
version/attempts/used-at, unique indexes for contacts and operator codes, contacts sealed, schema for SQLite/PostgreSQL/SQL Server).

**Acceptance met (tests):** unknown vs wrong password same body/status (HTTP) and timing within 15 % (isolated timing test, medians);
refresh reuse revokes the family; installation-bound refresh refused elsewhere; RFC 7515 A.3 ES256 golden vector; `alg` confusion
(`none`, `HS256`, `RS256`, wrong `typ`, unknown `kid`); principal reaches Authorization (`NameIdentifier`, `ActorScope.SessionId` = session)
over HTTP and RPC; code works once, never across purposes, dies on password change; cooldown; destination limits; NotOnWhatsApp → SMS
(capturing sender and the real Meta adapter); budget exhaustion and country allow-list; no code/destination/secret in any event
(capturing operation engine); `.Data` flows on SQLite; native AOT smoke **0 IL warnings, exit 0** for Public, engine, AspNetCore,
Delivery, Testing (InvariantGlobalization off for NFC/IDN). With `-p:SmokeData=true` the only warnings are MemoryPack's (third
party, pulled by YFex.Data — same as YFex.Data's own smoke); exit 0.

**Deviations / decisions taken while building:**
- Engine options/builder are `YFexAuthenticationOptions`/`YFexAuthenticationBuilder`: `AuthenticationOptions`/`AuthenticationBuilder`
  clash with ASP.NET Core in every host.
- `ClientKind.Desktop = 0` (the default `SignInContext` is an app with tokens, not a browser).
- `iat` is whole seconds, so the signature time given to `SignatureVerifier` is `iat + 1 s` (a key created mid-second signed it).
- JWT JSON is written with relaxed escaping (`at+jwt`, non-ASCII scopes as-is).
- The common-password list is a compact built-in list (~170 entries, hashed with `StableHash64`); the 100 000-entry resource and
  `tools/build-common-passwords` are **pending**.
- Contacts' lookup ring `authentication/lookup` must not rotate (lookup keys would be orphaned) — documented on `AuthenticationKeys`.
- The resend cooldown and destination limits are per destination across purposes (a decoy counts too, so limits do not reveal accounts).
- Sign-up decoys for taken addresses send a security notice to the owner and return the same `VerificationPending` shape.

**Not done / pending:** Cryptography C3 not needed (JWKS read from the DER SPKI); C4 used as designed (pepper = MAC, no new primitive);
Authorization R5 (`AccessToken.IsExpired(TimeProvider)`) not requested yet — the engine never calls the clock-less overload; Tenancy-based
realm resolver for `.AspNetCore` (`IAuthenticationRealmResolver` is the hook; default = request field or the single shared realm);
installation assertion header reading over HTTP (hook `YFexAuthenticationSchemeOptions.InstallationOf`; Installations.Testing harness
not used — tests use `InstallationId` values); HIBP breached-password check (opt-in, not built); `IHumanVerification`; ASP.NET
rate-limiting middleware wiring; `.Rpc` not in the AOT smoke (Messaging.Rpc/ActualLab); SQL throttle store (throttle and budgets are
per node); Audit mapping of `AuthenticationEvents` (YFex.Audit request); BenchmarkDotNet budgets of 13 §1 not measured yet; Linux run.
