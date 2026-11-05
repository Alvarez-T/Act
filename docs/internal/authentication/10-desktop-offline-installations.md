# 10 — Desktop, shared terminals, offline and installations

> **As built (AN-P4, 2026-10-19):** `YFex.Authentication.Client` (§1, §2, §4), `YFex.Authentication.Offline` (§3 terminal side) and
> `YFex.Authentication.Offline.Central` (§3 site verifiers). Differences from the text below: RPC uses the per-call token
> (`ctx.SendToken(AuthenticationClient.RpcToken)`) instead of reconnecting; unlock calls `POST session/reauthenticate` (offline: the kept
> verifier); `CompleteSecondFactorAsync` comes with P5. None of these packages is a service: they run inside the app (hosting:
> [hosting](../hosting/README.md)). Log: 14 §5.

## 1. The desktop client (`YFex.Authentication.Client`)

```csharp
public interface IAuthenticationClient
{
    ValueTask<SignInResult> WithPasswordAsync(string identifier, Password password, CancellationToken ct);
    ValueTask<SignInResult> WithOperatorPinAsync(string operatorCode, Password pin, CancellationToken ct);
    ValueTask<ChallengeStartResult> StartCodeAsync(string identifier, DeliveryChannel channel, CancellationToken ct);
    ValueTask<SignInResult> CompleteCodeAsync(ChallengeId challenge, VerificationCode code, CancellationToken ct);
    ValueTask<ExternalSignInResult> WithProviderAsync(IdentityProviderName provider, CancellationToken ct);   // system browser + loopback (06 §2.2)
    ValueTask<SignInResult> CompleteSecondFactorAsync(SignInFlowToken flow, AuthenticationMethod method, VerificationCode code, CancellationToken ct);
    ValueTask SignOutAsync(CancellationToken ct);

    AuthenticatedSession? Current { get; }
    event Action<AuthenticationClientChange>? Changed;      // SignedIn, Refreshed, Locked, Unlocked, SignedOut, Expired, Revoked
}
```

What the client does after a `SignedIn`, in order (one place, so every app gets it right):

1. Seal and store the refresh token (§4), keep the access token in memory only.
2. `AmbientPrincipalAccessor.SignIn(principal, accessToken)` → Authorization resolves the caller; `ActorContext.Session` is set.
3. Ask Messaging.Rpc to (re)connect with the new token (⚑AN20); restore Authorization's signed permission snapshot policy as today
   (3b) — offline sign-in uses it, online sign-in refreshes it.
4. Schedule the silent refresh at 80 % of the access-token lifetime; on `Revoked`/reuse detection, sign out locally and raise
   `Changed(Revoked)`.
5. Hand the online proof to `YFex.Authentication.Offline` (§3) when that package is installed.

At start-up the client tries a **silent** session restore from the stored refresh token (online) before showing the login screen;
offline it never restores silently (Authorization P3: "no snapshot, no offline login" — Offline decides).

## 2. Shared terminals (checkouts)

| Feature | Behaviour |
|---|---|
| **Lock** after N minutes idle (default 5) or on demand (F12) | the session stays; the screen requires the **same person's** PIN/password to unlock (verified online by central, or by the broker/terminal per Offline rules). Unlock is a re-authentication (`IReauthentication`), not a new sign-in |
| **Quick switch** | "Trocar operador": the current session is **signed out** (not kept in the background — one person per till for attribution), the next operator signs in with code + PIN. Takes < 1 s online |
| **Supervisor at the till** | not a switch: an inline approval (09 §4.2) inside Ana's session |
| **End of shift** | sign-out ends the session at central; the till returns to the login screen |
| **Remember me** | never on shared terminals (`SharedTerminal()` disables it) |

## 3. Offline sign-in — what Authentication contributes to ⚑O12 (⚑AN18)

Offline owns the modes, the site verifier bundle, the offline session and the broker's lockout. Authentication owns the credentials
and makes sure verifiers exist:

| Offline needs | Authentication provides |
|---|---|
| "verifiers come from an app-chosen credential source" | `ICredentialVerifierSource` (contract in `.Public`; Offline's interface if it already defined one — the Offline session decides the exact shape) implemented by `YFex.Authentication.Offline` |
| **site** verifiers for the store's staff (operator code + PIN / offline secret) | central-side `OperatorVerifierPublisher`: for each account whose home scope is the site and that holds an operator credential, the **site verifier** of the PIN/offline secret (peppered verifiers cannot be checked without the central pepper key, so a separate unpeppered PBKDF2 verifier is written at PIN set/change, 04 §4). Offline signs and ships them in `authentication.verifiers` |
| **terminal cached** verifier from the last online sign-in (passwords only, ≤ 20 users, ≤ 7 days) | the client, after a successful **online** password (or offline-secret) sign-in on this installation, computes a local verifier from the secret it just held (`PasswordHashing.CreateVerifier`, local salt) and hands it to Offline's store; the server never sends a password-equivalent |
| "capturing the secret when the app's online login never sees it is the app's job" | Authentication does it: when the online sign-in used an **external provider** or a **code**, and the realm enables offline sign-in, the client asks once for an **offline secret** (a password ≥ 10 characters for standalone terminals, or a PIN for broker-verified sites) and registers it as an `OfflineSecret` credential (04 §5) |
| revocation of a dismissed employee | `DisableAsync` removes the account's verifiers from the next site bundle and signals the terminals' caches through Offline's revocation path |

Nothing about offline **sessions**, envelopes or lockout counters lives here.

## 4. Storing tokens on a device

| OS | Where the refresh token lives | Through |
|---|---|---|
| Windows | sealed with the device storage ring, root key in CNG (TPM when present) | YFex.Cryptography.Windows + YFex.Persistence `IKeyValueStore` with storage encryption |
| macOS | sealed; root key in the Keychain (Apple store spike pending in YFex.Cryptography) or the file root key (0600) | same |
| Linux | sealed; file root key (0600, `FileSigningKeyStore`/`FileKeyRingStore` design, crypto 14) | same |
| Android | sealed; root key wrapped by an Android Keystore key (`YFex.Cryptography.Android`, ⚑K19, designed 2026-10-20) | same |
| iOS | Keychain — later (`YFex.Cryptography.Apple`) | — |
| Browser (WASM) | **no refresh token in the browser**: BFF cookie (07 §2) | — |

The access token is never persisted. A copied disk image on an enrolled installation still cannot refresh elsewhere (§5).

## 5. Installations

- **Installations authenticate machines, Authentication authenticates people.** A checkout without a signed-in person acts as
  `ActorKind.Device` through Installations' assertion — Authentication is not involved.
- **Binding (⚑AN16):** sessions opened on an enrolled installation record its `InstallationId`; refresh tokens are accepted only with
  that installation's assertion; access tokens carry `installation`, and Authorization's scope pinning (⚑I11) already refuses a call
  whose scope is outside the installation's enrolled scope.
- **Android handhelds (2026-10-20, installations [11](../installations/11-mobile-handhelds.md)):** the QR there **enrolls the device**
  (Installations). This section's QR signs in a **person**. They are different flows. On an enrolled handheld people sign in as on any
  terminal (password, operator code + PIN, offline verifiers when offline), with the optional session profile and off-site rule of
  07 §2.1 (⚑AN26–AN27).
- **Sign in on another device (optional, phase AN-P5):** a terminal without a keyboard or a kiosk shows a QR code / short code; the
  person approves on a phone already signed in (the RFC 8628 device-authorization *shape*, served by our own endpoints:
  `POST device/start` → `user_code`; phone `POST device/approve`; terminal polls `POST device/token`). Useful for manager
  authorisation at a till without typing a password in front of customers.
- *As built (AN-P5, 2026-10-19):* `DeviceSignInService` + `IDeviceSignInStore` (in-memory; a SQL table is pending), opt-in
  `auth.DeviceSignIn()`. Device code = 256-bit random (stored as SHA-256), user code = 8 RFC 8628 consonants shown `XXXX-XXXX`, 10 min,
  poll interval 5 s (`slow_down` when faster), single use, redeemed only from the installation that started it. The approving phone's
  session must not be an impersonation and must be in the realm; the terminal gets **its own session** bound to its installation with
  the phone session's methods plus `AuthenticationMethod.DeviceApproval` (the phone session already passed the second-factor policy).
  Endpoints `POST device/start`, `device/approve` (signed-in), `device/token`.
