# 04 — Passwords, PINs and local credentials

## 1. Password policy (⚑AN22)

Follows NIST SP 800-63B §5.1.1 (memorized secrets) and OWASP ASVS V2 in shape; the numbers are per-scope options with hard bounds.

| Rule | Default | Bounds | Why |
|---|---|---|---|
| minimum length | **10** | 8–64 | length is what resists guessing; 8 is the NIST floor |
| maximum length | 128 | 64–256 | accept passphrases; bound the PBKDF2 input |
| characters | any Unicode, **NFC-normalised** (`PasswordHashing` already does it) | — | "José" typed on two keyboards must match |
| composition rules | **none** | — | NIST: they push users to predictable patterns |
| forced periodic change | **none** (change only on evidence of compromise) | — | same |
| blocked lists | bundled **common-password list** (top 100 000, lower-cased) + the account's own e-mail local part, display name, realm name | app may add words | cheap, offline, no data leaves |
| breached-password check | **off**; opt-in `BreachedPasswords.HaveIBeenPwned()` | — | k-anonymity: only 5 hex chars of SHA-1 leave; still an external call → opt-in per deployment |
| show strength meter | client concern | — | |

```csharp
auth.Passwords(p => p
    .MinimumLength(10)
    .BlockCommonPasswords()                         // embedded list, 13 §3
    .BlockWords("farmaviva", "yfex")                // app words
    .CheckBreached(BreachedPasswords.HaveIBeenPwned(), onBreached: BreachedAction.RequireChange));
```

`PasswordPolicy.Check(ReadOnlySpan<char> candidate, in PasswordContext context) → PasswordPolicyResult` is a `[Union]` with
`Accepted | TooShort(int min) | TooLong(int max) | Common | ContainsPersonalData | Breached(long count)` — evaluated on every
keystroke by clients (the same rules ship in `.Public` so the UI shows the reason before the server is asked; the server re-checks).

## 2. Storing a password

```
verifier = PasswordHashing.CreateVerifier( MAC_pepper(nfc(password)) , iterations: 600 000 )
           └ YFex.Cryptography: PBKDF2-HMAC-SHA256, random 16-byte salt, 54-byte versioned verifier
```

- **Pepper** = `MessageAuthentication` tag of the normalised password under ring `authentication/pepper` (domain
  `yfex.authentication.pepper.v1`), computed into a stack buffer and passed as the "password" bytes (base64url of the 32-byte tag, so
  PBKDF2 input stays text and NFC is idempotent). A stolen database without the key ring is useless for offline guessing. The pepper
  key **never rotates** silently: a new pepper key id is written next to the verifier (`PasswordCredential.PepperKey`) and old
  verifiers keep their key until re-hashed at the next sign-in.
- **Upgrade on sign-in**: `PasswordCheck.MatchNeedsUpgrade` (iterations raised) or an old pepper key → re-hash in the same request.
- **Timing**: an unknown identifier still runs one full `Verify` against a fixed dummy verifier, so "no such account" and "wrong
  password" take the same time (11 §1).
- **Buffers**: the password arrives as `ReadOnlySpan<char>`; intermediate UTF-8/MAC buffers are `stackalloc`/`ArrayPool` and zeroed
  with `CryptographicOperations.ZeroMemory` in `finally` (the BCL `string` from JSON cannot be zeroed — accepted, documented).
- PBKDF2 600 000 ≈ 150–250 ms of CPU per check (measure, 13 §1). Throttling (11 §2) is what keeps this from being a DoS lever; the
  endpoint also caps concurrent verifications per process (`ConcurrencyLimiter`, default `Environment.ProcessorCount`).

## 3. Changing a password

| Path | Proof needed | Effect |
|---|---|---|
| signed-in change | current password **or** a step-up within 5 min (09 §4) | new verifier, `SecurityStamp++`, other sessions revoked (option `KeepCurrentSession = true`), notification to verified contacts |
| reset (08 §3) | a `ResetPassword` challenge + the second factor if the account has one | same, **all** sessions revoked |
| admin-set temporary password | `authentication.accounts.manage` + approval (default) | `PasswordChangeRequired(AdminReset)` at next sign-in |
| policy tightened / breached detected | — | `PasswordChangeRequired(PolicyChanged | Breached)` after a **correct** sign-in |

## 4. Operator code + PIN (shared terminals, ⚑AN18)

Retail checkouts sign in many times per shift; typing an e-mail and a long password at a till is not realistic. The **operator
credential** is a separate credential on the same account:

| Part | Rule |
|---|---|
| operator code | 1–12 digits/letters, **unique per realm**, not secret (printed on badges); assigned by an admin or generated |
| PIN | 4–6 digits (scope option), stored like a password (PBKDF2 + pepper); common PINs blocked (`0000`, `1234`, birthdays cannot be checked → not attempted) |
| where a PIN is accepted | **only where the verifier is guarded by online rate limiting**: central, or a site broker (Offline ⚑O12). A standalone offline terminal requires the full password or an **offline secret** (10 §3), because a stolen 4-digit PIN verifier falls to brute force in seconds |
| lockout | 5 wrong PINs → 5 min, doubling, per (account, installation) **and** per account; an admin or manager unlock is audited |
| site verifier | when the realm enables offline sign-in, setting/changing a PIN also writes a **second, unpeppered** PBKDF2 verifier with its own salt, used **only** in Offline's signed site bundle (`authentication.verifiers`) — a site broker cannot hold the central pepper key. Its exposure is bounded by the broker's storage protection, rate limit and lockout (⚑O12); it is never accepted by central |
| assurance | `SingleFactor` on its own. **PIN + enrolled installation** may count as `MultiFactor` when the realm opts in (`OperatorPinOnEnrolledInstallation = MultiFactor`): the installation's TPM key is a second, possession factor bound to the session (⚑AN16) |

```csharp
await operators.AssignAsync(account: ana.Id, code: OperatorCode.Parse("0142"), ct);       // admin
await operators.SetPinAsync(ana.Id, pin, proof: currentPassword, ct);                      // Ana, first time, online
```

## 5. Credential kinds (one table, one store)

| Kind | Secret stored | Verified by | Used for |
|---|---|---|---|
| `Password` | PBKDF2 verifier + pepper key id | `PasswordHashing.Verify` | sign-in, step-up |
| `OperatorPin` | same | same | sign-in at terminals, inline approvals (Authorization `ApprovalSource.Inline`) |
| `OfflineSecret` | same (optional, 10 §3) | Offline's cached verifier on the terminal | offline sign-in when the online method never saw a password (external provider) |
| `AuthenticatorApp` | TOTP secret, **sealed** (`SealedValue`, ring `authentication/factors`) — must be readable to compute codes | `OneTimePassword.Totp` (⚑AN12) | second factor, step-up |
| `RecoveryCodes` | 10 × PBKDF2 verifiers (fewer iterations: 100 000; codes are 10 random base32 characters, 50 bits) | `PasswordHashing.Verify`, single use each | lost second factor |
| `Passkey` | public key (COSE), credential id, sign counter, transports | WebAuthn assertion (09 §2.3) | phishing-resistant sign-in / second factor |

External logins are **not** credentials (no secret is stored); they live in their own table (12 §2).
