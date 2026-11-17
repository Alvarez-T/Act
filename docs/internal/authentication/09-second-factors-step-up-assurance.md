# 09 — Second factors, step-up and assurance

> **As built (AN-P5, 2026-10-19; log in 14 §5).** `AssuranceLevel` lives in YFex.Authorization.Public (request R1: step-up
> requirements read `acr`); Authentication imports it with a global alias. Engine: `SecondFactorService` (enrolment, recovery codes,
> status, removal, the §3 policy), `SignInService.CompleteSecondFactorAsync` / `StartSecondFactorCodeAsync` /
> `StartAuthenticatorEnrolmentAsync(flow)` / `ConfirmAuthenticatorEnrolmentAsync(flow, …)` / `StepUpAsync` / `StartStepUpCodeAsync`,
> `SignInService` is the `IReauthentication`, `InlineApprovalService` (§4.2), `DeviceSignInService` (10 §5). Deviations are marked
> "as built" below.

## 1. Assurance is computed, never declared

```csharp
public enum AssuranceLevel : byte { None = 0, SingleFactor = 1, MultiFactor = 2, PhishingResistant = 3 }
```

| Session proved | Level | Note |
|---|---|---|
| nothing | `None` | anonymous |
| one method of any kind | `SingleFactor` | password; operator PIN; e-mail/SMS/WhatsApp code; magic link; an external provider without MFA hint |
| two **different factor categories** (knowledge + possession) | `MultiFactor` | password + authenticator; password + SMS code; PIN + enrolled installation (realm opt-in, 04 §4); external provider with an MFA hint (Entra `amr: mfa`, gov.br prata/ouro) |
| a passkey (user verification) | `PhishingResistant` | origin-bound; also when used as the second factor |

Each `AuthenticationMethod` declares its **category** (`Knowledge`, `Possession`, `Inherence`, `Federated`) and whether it is
phishing-resistant; the level is computed from the `AuthenticationMethodSet` of the session with bit operations (no allocation).
Two codes to two channels (e-mail + SMS) are two possession proofs of contacts, which counts as `MultiFactor` only when the realm
opts in (`CountTwoContactCodesAsMultiFactor`, off by default) — NIST treats SMS as a restricted authenticator.

## 2. Factors

### 2.1 Authenticator app (TOTP, RFC 6238)

- Enrol: `POST factors/authenticator/start` → secret (20 random bytes, base32) + `otpauth://totp/{issuer}:{masked account}?secret=…&issuer=…&period=30&digits=6`
  QR payload; `…/confirm` with a first code proves the app has it. Secret stored **sealed** (ring `authentication/factors`).
- Verify: `OneTimePassword.Totp` (request ⚑AN12, SHA-1 default for compatibility with Google Authenticator / Microsoft
  Authenticator), window ±1 step, **each step accepted once** (last accepted step stored, replay refused), throttled like PINs.
- *As built:* `OneTimePassword.VerifyTotp` (crypto 04 §13). The credential (`CredentialKind.AuthenticatorApp`) stores
  `0x01 ‖ last accepted step (int64) ‖ sealed secret` (domain `yfex.authentication.factor.totp.v1`, context = account id); each accepted
  code rewrites it with `ICredentialStore.ReplaceAsync` — a compare-and-set on the stored bytes (new store method; SQL `where verifier =
  @expected`) — so concurrent requests accept a step once, and the secret is re-sealed with the ring's current key at every use. The
  pending secret travels sealed in the enrolment token (10 min, bound to the security stamp); the confirmation's step counts as used.
  Throttle key: account + installation (5 wrong codes → lock, like PINs). The `otpauth://` label is the masked e-mail.

### 2.2 Recovery codes

10 codes × 10 base32 characters (shown once, downloadable), PBKDF2 100 000 each, single use; using one notifies all contacts.
Regenerating invalidates the previous set.

*As built:* Crockford alphabet (no I/L/O/U; typed look-alikes O→0, I/L→1, case and hyphens ignored), shown as `XXXXX-XXXXX`. One
credential (`CredentialKind.RecoveryCodes`) holds `0x01 ‖ count ‖ (hint ‖ verifier)×count`: each verifier is the peppered PBKDF2 of the
code (same pepper scheme as passwords, 100 000 iterations), the 2-byte hint is a pepper-keyed MAC of the code so a check costs one
PBKDF2, not ten. A used code is cut out with `ReplaceAsync` (compare-and-set). The first enrolment issues the set.

### 2.3 Passkeys (WebAuthn level 2/3, phase AN-P6)

- Registration (`navigator.credentials.create`) and assertion (`get`) ceremonies with challenge from a `Challenge` row, RP id =
  the realm's web origin, **attestation `none`** (no device model collection; privacy), user verification required.
- Server parses `authenticatorData` and the COSE key with a **small span CBOR reader** (only the subset WebAuthn needs: maps,
  byte/text strings, integers, ES256 `-7` and RS256 `-257` keys); verification through YFex.Cryptography (ES256 native, RS256 via
  ⚑AN11). Sign counter checked when non-zero (cloned authenticator finding).
- Usable as sign-in (discoverable credential, "entrar com passkey") and as second factor. Desktop apps use the platform passkey UI
  through the system browser flow (06 §2.2); native platform APIs are an open item.
- *As built (AN-P6, 2026-11-12; 14 §5):* in the engine (`Passkeys/`), not a separate package. The challenge is a sealed ceremony token
  (purpose, account, stamp, realm, expiry) consumed once, not a `Challenge` row; all passkeys of an account sit in one
  `CredentialKind.Passkey` record (format v1) whose counter moves by compare-and-set. Relying party per deployment or per realm:
  `auth.Passkeys(p => p.RelyingParty("farmaviva.com.br", "https://app.farmaviva.com.br"))`.

### 2.4 Codes as second factor

`SmsCode`, `WhatsAppCode`, `EmailCode` (05) are allowed as second factors per realm (`SecondFactor(m => m.Allow(...))`), default
allowed for shoppers, **not** allowed for staff with `RequireFor(...)` roles (authenticator or passkey instead), because SIM swap
and WhatsApp account takeover are cheaper than stealing a phone.

## 3. When a second factor is required

```csharp
auth.SecondFactor(m => m
    .RequireFor(FarmaRoles.Manager, FarmaRoles.Administrator)       // roles resolved by Authorization for the account's home scope
    .RequireWhen(context => context.Client == ClientKind.Browser && !context.KnownDevice)    // risk rule (sketch)
    .RememberDevice(TimeSpan.FromDays(30))                         // browser: signed "remembered device" cookie bound to SecurityStamp
    .GracePeriodForEnrollment(TimeSpan.FromDays(7)));               // new managers get 7 days to enrol, then sign-in requires it
```

- The role check asks Authorization (`IPermissionDirectory`-like read of the account's roles), Authentication never stores roles.
- `RememberDevice` skips the second factor on the same browser, never on a new installation or after a password change.
- Accounts with a required-but-missing factor after the grace get `SecondFactorRequired` with `Allowed = enrolment methods` and can
  only enrol.

*As built:* builder `SecondFactor(m => m.RequireFor(roles).RequireWhen(rule).RememberDevice(d).GracePeriodForEnrollment(g).AllowCodes(…))`.
Roles come from Authorization's new `IRoleDirectory` (R6; `RequireFor` without one fails at startup; `Unavailable` counts as required —
fail closed). A sign-in already multi-factor needs nothing. An account **with** an authenticator is always asked (opt-in 2FA is honoured);
a rule alone never demands a factor the account does not have. The grace starts at the first sign-in that finds the account required
(a `CredentialKind.EnrolmentGrace` marker row), then `SecondFactorRequired { MustEnrol = true, Allowed = AuthenticatorApp }` and
`StartAuthenticatorEnrolmentAsync(flow)` → `ConfirmAuthenticatorEnrolmentAsync(flow, …)` opens the session. The remembered device is a
`MessageToken` (`yfex.authentication.remembered-device.v1`, account + security stamp) in the `__Host-yfex.device` cookie, browsers only.
A password reset no longer signs in an account that has (or owes) a second factor: the reset proved a password and a contact, never
the authenticator. `SecondFactorRequired` gained `MustEnrol`; the JSON lists `allowed`.

## 4. Step-up and re-authentication

### 4.1 For commands (request to Authorization, 14 §2.1 R1–R3)

```csharp
b.Command<ChangePriceTable, Unit>()
    .RequirePermission(PricingPermissions.ChangeTable)
    .RequireRecentAuthentication(maxAge: TimeSpan.FromMinutes(5), minimum: AssuranceLevel.MultiFactor);
```

- Authorization gets a new requirement case reading `auth_time` and `acr` from the caller's principal, and a new denial
  `DenialKind.ReauthenticationRequired` carrying `(maxAge, minimum)`; the Cqrs `Error` maps it like `ApprovalRequired`, so clients
  get a typed reason over RPC/HTTP.
- The client calls `stepUp.ReauthenticateAsync(method, proof)` → `POST step-up` → the **same session** gets a new `auth_time`/methods
  and new tokens (no new session, no sign-out of other tabs).
- *As built:* `SignInService.StepUpAsync(caller, method, proof, minimum, context, flow, challenge)` / client `StepUpAsync`. Each call
  proves one method (password, PIN, offline secret, authenticator, recovery code, or a `step-up` purpose contact code). Below
  `minimum` with a factor that lifts it → `SecondFactorRequired` with a single-use `StepUp` flow carrying what was proved; the second call
  completes it. The session's methods become **the methods of this step-up** and `auth_time` now — the pair always describes the latest
  proof (an old password plus a fresh TOTP is not "multi-factor 1 minute ago"). Only a new access token is issued (refresh family,
  cookie and session id stay; refreshes carry the new facts). An account that cannot reach `minimum` is stepped up as far as it can
  and the command denies again. Impersonated sessions are refused. Authorization: `DenialKind.ReauthenticationRequired`,
  `ErrorKind.ReauthenticationRequired` (Cqrs, RPC v7), 401 + RFC 9470 `insufficient_user_authentication` challenge over HTTP.

```csharp
public sealed record SteppedUp(AuthenticatedSession Session, IssuedCredentials Issued);
public union StepUpResult(SteppedUp, SecondFactorRequired, LockedOut, SignInRejected);
```

### 4.2 Inline approvals (Authorization `ApprovalSource.Inline`)

A supervisor approves a void **on Ana's checkout**: Authorization's approval flow needs "the approver proved who they are right
now". Authentication supplies:

```csharp
public interface IReauthentication
{
    /// <summary>Verifies a person's proof without creating a session (PIN, password, TOTP, passkey). Throttled and audited.</summary>
    ValueTask<ReauthenticationResult> VerifyAsync(AccountRealm realm, ReauthenticationProof proof, CancellationToken ct);
}
// ReauthenticationResult: [Union] Verified(AccountId, AuthenticationMethod, AssuranceLevel) | Rejected | LockedOut — checked per approval attempt
```

The Approve command (YFex.Cqrs.Approvals) then issues its `ApprovalToken` for the verified approver. Offline, the same proof is
checked by Offline against cached/site verifiers (⚑O15) — Authentication only produced those verifiers.

*As built:* `ReauthenticationProof(Method, Secret, OperatorCode, Identifier, SecondFactorCode, Installation, IPAddress)`; the result is
the existing `ReauthenticationResult(Reauthenticated, LockedOut, SignInRejected)` where `Reauthenticated` gained `Account`, `Methods`
and `Assurance` (PIN + authenticator code = multi-factor). Throttled per person and installation like a PIN sign-in; the event is
`authentication.reauthenticated` with reason `inline`. `InlineApprovalService.ApproveAsync(realm, proof, permission, digest, requester,
minimum)` does the whole §4.2 at central without a session: proof → Authorization's `IAuthorizationContextFactory` for a session-less
principal of the approver (no `sid`) → must hold the permission, not be the requester, not be impersonated → `IApprovalTokenIssuer`
signs the same token `Approvals.Commands.Approve` would. No HTTP endpoint: apps expose it to tills through their own RPC command (the
token has a Messaging.Rpc formatter, no JSON codec).

### 4.3 Off-site re-authentication (⚑AN27, 2026-10-20, design)

The optional off-site rule of a session profile (07 §2.1) reuses §4.1: past `after` without verified presence, the refresh answers
`ReauthenticationRequired(minimum)` and the client steps up the **same session**. Presence is a witness-signed Installations statement
(installations 11 §5), never the network. Per command, Authorization's `RequireSitePresence(maxAge)` (⚑Z20) can refuse instead of
asking for a step-up.

## 5. Sensitive operations inside Authentication itself

Always require a step-up within 5 minutes (or the current password): change password, change/remove a contact, add/remove a
factor, regenerate recovery codes, link/unlink an external login, delete the account, start impersonation. Impersonated sessions
cannot do any of them (07 §7).
