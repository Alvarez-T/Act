# 11 — Threats, abuse and privacy

## 1. Threats and answers

| Threat | Answer in this design | Where |
|---|---|---|
| credential stuffing / password spraying | throttling per IP and per account+IP, common/breached password blocks, optional human verification after N failures, MFA policy | §2, 04 §1, 09 §3 |
| online brute force of one account | exponential delay, temporary lock (15 min), lock counts per account **and** per (account, IP) so an attacker cannot lock everyone from one IP… | §2 |
| …and lock-out as a weapon | locks are temporary; a correct password from a **known device** (remembered device / bound installation) bypasses the per-account (not per-IP) lock | §2 |
| account enumeration | same response, same status code and similar timing for unknown accounts (dummy PBKDF2 verify, dummy challenge ids); sign-up and reset never say "exists" | 04 §2, 08 §1, §3 |
| offline guessing from a stolen database | PBKDF2 600 000 + pepper key outside the database; codes stored as MACs; refresh tokens as hashes | 04 §2, 05 §2, 07 §4 |
| OTP interception (SIM swap, WhatsApp takeover) | codes are not allowed as second factor for privileged roles; recovery changes notify every contact with a 7-day/24-hour window | 09 §2.4, 08 §2, §4 |
| **SMS pumping / toll fraud** | country allow-list, per-destination and per-IP limits, daily spend budget per scope, prefix anomaly detection (many codes to one number range), optional human verification before the first send | §3 |
| phishing | passkeys (phishing-resistant), WebOTP origin-bound SMS, provider buttons instead of password forms on third-party pages | 09 §2.3, 05 §5.2 |
| login CSRF / OAuth CSRF | `state` + flow cookie, `SameSite=Lax`, `Origin` check on POST, anti-forgery header for browser forms | 06 §2.1, 07 §9 |
| open redirect after sign-in | relative `returnUrl` or exact allow-list only | 06 §2.1 |
| authorization-code injection / mix-up | PKCE S256 everywhere, `iss` response parameter check (RFC 9207), per-provider redirect URIs | 06 §2 |
| ID-token forgery / `alg` confusion | algorithm from the key, never `none`/`HS*` for external keys, `aud`/`azp`/`nonce`/`iss` rules | 06 §4 |
| "nOAuth" (unverified Entra e-mail) | Microsoft e-mail untrusted unless `xms_edov` or a configured verified domain | 06 §3.2, ⚑AN19 |
| session fixation | a new session id and cookie at every sign-in and step-up; flow cookie discarded | 07 §1–§2 |
| token theft (XSS) | browser apps use HttpOnly `__Host-` cookies; SPAs use a BFF; access tokens short (10 min) | 07 §2 |
| refresh-token theft | rotation + reuse detection revokes the family; binding to installations | 07 §4 |
| stolen checkout disk image | refresh token sealed under the device root key (TPM) and bound to the installation | 10 §4–§5 |
| timing side channels | `FixedTimeEquals` for every secret comparison; constant work for unknown accounts | 04 §2, 05 §2 |
| a malicious admin | approvals for factor resets and impersonation; every admin action audited with the real actor | 08 §6 |
| replay of codes, links, flows | single use, short life, bound to the security stamp, flow ids consumed | 05 §2–§3, 07 §1 |

## 2. Throttling (⚑AN23)

```csharp
public enum ThrottleKind : byte { None = 0, Allowed = 1, Delayed = 2, Blocked = 3 }
[Union] public readonly struct ThrottleDecision : IUnion { /* Allowed | Delayed(TimeSpan) | Blocked(DateTimeOffset until) */ }

public interface IAuthenticationThrottle
{
    ThrottleDecision Check(in ThrottleKey key, DateTimeOffset now);          // sync, allocation-free on the in-memory store
    void RecordFailure(in ThrottleKey key, DateTimeOffset now);
    void RecordSuccess(in ThrottleKey key, DateTimeOffset now);               // resets the account counters, not the IP ones
}
public readonly record struct ThrottleKey(ThrottleSubject Subject, ulong Hash);   // AccountIp | Account | Ip | Destination | Installation
```

| Key | Default rule |
|---|---|
| account + IP | delay 2^(n−3) s after 3 failures (max 30 s), lock 15 min after 10 |
| account (any IP) | lock 15 min after 20 failures in 1 h (bypassed by a known device, §1) |
| IP / IPv6 /64 | 100 failures per 10 min → blocked 10 min; honours `X-Forwarded-For` only from configured proxies |
| destination (e-mail/phone) | 3 challenges per 15 min, 10 per day |
| operator PIN on an installation | 5 failures → 5 min, doubling (= Offline ⚑O12 numbers) |

- Keys are hashed (`MessageAuthentication` with a process key) so the throttle store holds no IPs or e-mails in clear.
- In-memory store: fixed-size sliding counters in a `ConcurrentDictionary<ulong, Counter>` with periodic trimming — no allocation on
  `Check`. Farms use the `.Data` store (atomic upsert) or an app adapter; counters are best-effort (a node may let a few extra tries
  through during a partition, never fewer).
- Endpoint-level limits use BCL `System.Threading.RateLimiting` partitioned limiters through ASP.NET's rate-limiting middleware
  (`429` + `Retry-After`).
- `IHumanVerification` (optional): after N failures from an IP, or before the first SMS of an anonymous visitor, endpoints require a
  verification token the app's adapter checks (Turnstile/reCAPTCHA are app adapters until reused, README §8).

## 3. Spend control for SMS/WhatsApp/voice

`DeliveryBudget` per (scope, channel): daily maximum messages (default 500 per tenant per channel), country allow-list (default: the
realm's country calling code), and a per-prefix rate (e.g. ≤ 20 per hour to one 6-digit number range). Exceeding it returns
`DeliveryRefused(BudgetExhausted | CountryNotAllowed)` and raises an operational alert event; e-mail stays available as fallback.

## 4. What is personal data here (YFex.Privacy)

| Data | Classification | Kept |
|---|---|---|
| `AccountId`, `SessionId`, `ChallengeId` | not personal by themselves (opaque) | account lifetime / session history retention |
| e-mail, phone, display name | `Personal` (`contact`), masked in logs and UI by the core value objects | while the account exists |
| gov.br `sub` (CPF) | `PersonalIdentifier` | keyed hash + sealed (12 §3) |
| IP address, user agent, coarse location of a session | `Personal` | session history: 90 days default (scope option), then erased |
| passwords, PINs, codes, TOTP secrets, refresh tokens, pepper | `Secret` (`DataCategory.Credentials`) — erased always, never logged, never audited in value | verifiers only |

The legal basis for processing authentication data is the deployment's assessment (typically contract execution and legitimate
interest in security under LGPD art. 7 — **to be confirmed by the controller**, not decided by the library). The library never
sends personal data to third parties except: the destination to the chosen delivery provider, and (opt-in) 5 hex characters of a
password's SHA-1 to the breached-password service.

## 5. Audit and logging

- `AuthenticationEvents` (02 §6) through the core probes; YFex.Logging logs them with masked values.
- YFex.Audit durable entries for: sign-in success/failure (aggregated failures per account per hour to bound volume), password
  change/reset, contact change, factor add/remove/reset, external login link/unlink, impersonation, admin actions, refresh-token
  reuse, account disable/delete.
- Correlation: `SessionId` = `ActorScope.SessionId`, so audit entries made during a session name it.

## 6. Deployment checklist (documented, not code)

SPF/DKIM/DMARC for the sending domain · WhatsApp Business account, verified business, approved authentication templates per
language · SMS sender registration rules of each country (Brazil: short codes / alphanumeric sender rules of the provider) · TLS-only
public endpoints, HSTS · proxies listed for `X-Forwarded-For` · key-ring backups (`authentication/pepper` loss = every password
must be reset) · provider client secrets rotated yearly.
