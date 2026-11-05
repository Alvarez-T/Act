# 05 — One-time codes, links and delivery (e-mail, SMS, WhatsApp)

## 1. One mechanism: the challenge

Every "we sent you a code" in the product — sign-in without password, second factor by SMS, verify a new e-mail, reset a password,
confirm a phone for WhatsApp, step-up, accept an invitation — is one **challenge**:

```csharp
public sealed record Challenge(
    ChallengeId Id, ChallengePurpose Purpose, AccountRealm Realm, AccountId? Account,   // null: sign-up / unknown identifier
    DeliveryChannel Channel, string DestinationKey,           // keyed hash of the destination (12 §2), never the raw number in the row
    ReadOnlyMemory<byte> CodeTag,                              // MAC of the code — the code itself is never stored
    long SecurityStamp, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    byte Attempts, byte MaxAttempts, ChallengeState State);   // Pending | Passed | Expired | Exhausted | Cancelled
```

```csharp
ChallengeStartResult start = await challenges.StartAsync(new ChallengeRequest(
    Purpose: ChallengePurpose.VerifyContact, Realm: realm, Account: carla.Id,
    Destination: ContactDestination.Phone(PhoneNumber.Parse("+5511987654321")), Channel: DeliveryChannel.WhatsApp), ct);

ChallengeCheck check = await challenges.CheckAsync(start.Id, VerificationCode.Parse(input), ct);   // [Union]: Passed | WrongCode(left) | Expired | Exhausted | Unknown
```

Flows (sign-in, reset, verification) wrap this; apps can also use `ChallengePurpose` values they declare
(`ChallengePurpose.Declare("confirm-payment")`) for their own "confirm with a code" screens, without touching the library.

## 2. Codes (⚑AN13)

| Property | Default | Why |
|---|---|---|
| alphabet / length | 6 decimal digits (scope option 6–8; e-mail may use 8 base32) | keypad-friendly, SMS/WhatsApp autofill |
| generation | `RandomNumberGenerator.GetInt32(0, 10^n)`, written into the `VerificationCode` inline buffer | uniform, no modulo bias, no string |
| stored | `MessageAuthentication` tag over (challenge id ‖ code), ring `authentication/codes`, **domain per purpose** (`yfex.authentication.code.sign-in.v1` …) | a leaked table gives nothing; a sign-in code cannot pass as a reset code |
| comparison | recompute tag, `CryptographicOperations.FixedTimeEquals` | no timing oracle |
| lifetime | 10 min (e-mail 15 min) | NIST: short-lived; e-mail is slower |
| attempts | 5 per challenge; then `Exhausted` | 10⁶ space / 5 tries → 1 in 200 000 per challenge |
| single use | `Passed` is terminal; a second check returns `Unknown` | |
| resend | new challenge (new code) after **60 s** cooldown; the old one is `Cancelled`; ≤ 3 per destination / 15 min, ≤ 10 / day | toll fraud, harassment |
| binding | `SecurityStamp` at issue; a password/contact change cancels pending challenges | 02 §4 |

**Brute force across challenges** is limited by throttling per destination and per account (11 §2), not by the code length.

## 3. Links (magic links, e-mail verification, reset)

A link is a `MessageToken` (ring `authentication/codes`, domain per purpose) carrying `ChallengeId ‖ SecurityStamp`, expiry
inside the token: `https://…/auth/link?t=<token>`.

- Used for **e-mail verification, password reset and invitations** — where the user is expected to switch to the mailbox anyway.
- **Not the default for sign-in** (`SignInWithCode` uses codes): a link opened on another device than the one that asked creates
  either a confused session on the wrong device or an "approve on this device" protocol. Opt-in `SignInWithLink(sameDeviceOnly:
  true)` binds the link to the browser that started it (a cookie with the flow id must be present).
- Opening a link only **shows** a confirmation page; the state change is a `POST` from that page (mail scanners pre-fetch `GET`s).
- Every link also shows the code in the same e-mail, so a user who reads mail on the phone can type it on the PC.

## 4. Delivery port (⚑AN14)

```csharp
namespace YFex.Authentication;

/// <summary>Sends one challenge message. One registered sender per channel (per scope, resolved at send time).</summary>
public interface IChallengeSender
{
    DeliveryChannel Channel { get; }
    ValueTask<DeliveryResult> SendAsync(ChallengeMessage message, CancellationToken ct);
}

public readonly record struct ChallengeMessage(
    DeliveryChannel Channel, ContactDestination Destination, ChallengePurpose Purpose,
    VerificationCode Code, string? Link, TimeSpan ExpiresIn, string Culture, string ScopeText, ChallengeId Id);

// cold path → native union
public sealed record Delivered(string? ProviderMessageId);
public sealed record DeliveryRefused(DeliveryRefusal Reason);       // UnsupportedDestination, NotOnWhatsApp, CountryNotAllowed, BudgetExhausted
public sealed record DeliveryFailed(string ProviderCode, bool Retryable);
public union DeliveryResult(Delivered, DeliveryRefused, DeliveryFailed);

/// <summary>Alternative per channel: the provider generates and checks the code (Twilio Verify).</summary>
public interface IVerificationProvider
{
    DeliveryChannel Channel { get; }
    ValueTask<DeliveryResult> StartAsync(ContactDestination destination, string culture, CancellationToken ct);
    ValueTask<bool> CheckAsync(ContactDestination destination, VerificationCode code, CancellationToken ct);
}
```

- **Fallback chain** per purpose: `WhatsApp → Sms → Voice` when the sender says `NotOnWhatsApp`/`Failed(retryable)`; the UI shows
  where it went (`MaskedDestination`). The user can always choose another verified contact.
- Sending is **outside** the request's critical path only for e-mail (queued, retried); SMS/WhatsApp send inline so the UI can
  report "not on WhatsApp" immediately (timeouts 5 s via YFex.Resilience policies).
- Templates: per (purpose, channel, culture, scope) with built-in pt-BR/en/es defaults. Placeholders `{code}`, `{minutes}`,
  `{app}`, `{link}` are parsed once into a span plan and written into a pooled buffer; no `string.Format`, no reflection.

## 5. Channel specifics

### 5.1 E-mail

- Subject and first line contain the code ("123456 é seu código FarmaViva") so it is visible in notifications.
- `From` per scope; SPF/DKIM/DMARC are deployment configuration (documented checklist, not code).
- Never include the account's other contacts or any personal data beyond the display name.

### 5.2 SMS

- GSM-7 text ≤ 160 characters; no accents in the default templates (a single `é` switches the whole message to UCS-2, 70 chars).
- Last line in **WebOTP / Android SMS Retriever** form `@api.yfexfarma.com.br #123456` (origin-bound autofill) when the realm has a web
  origin; iOS autofill recognises "código"/"code" in the text.
- **Country allow-list** per scope (default: the realm's home country only) and a **daily spend budget** (11 §3): SMS pumping
  (attackers trigger codes to premium numbers) is the most expensive abuse of an OTP endpoint.

### 5.3 WhatsApp (Meta Cloud API, or Twilio as a WhatsApp sender)

- Business-initiated messages must use a **pre-approved template**. Codes use Meta's **authentication** template category: fixed
  body ("{code} é o seu código de verificação."), optional security disclaimer and expiry footer, a **copy-code** button (or
  one-tap autofill on Android when the app's package/signature is registered). Authentication templates **cannot contain links**,
  so WhatsApp carries **codes only**, never magic links.
- Template name and language per scope (`codigo_acesso`, `pt_BR`); the sender maps culture → template language and falls back to
  the realm's default language.
- "Not on WhatsApp" is detected from the send error (no separate lookup call, which would itself be a number-enumeration oracle).
- The person **chose WhatsApp** for this destination in the UI; that choice is recorded with the contact (`PreferredChannel`) as the
  request to be contacted there. Meta's opt-in and messaging policies are the deployment's responsibility (checklist in 11 §6).
- Pricing is per delivered authentication message in most markets (check Meta's current rates): the daily budget of 11 §3 applies.

### 5.4 Voice

Only as a fallback for landlines/accessibility (Twilio `<Say>`), digits spoken twice. Off by default.

## 6. Adapters (`YFex.Authentication.Delivery`)

| Adapter | Channel(s) | Transport | Phase |
|---|---|---|---|
| `Smtp` | Email | BCL `System.Net.Mail.SmtpClient` (STARTTLS, auth), for an on-premises relay or a provider's SMTP endpoint. Microsoft marks `SmtpClient` "not recommended for new development" because of missing modern protocol features; for a relay that speaks STARTTLS + AUTH it is sufficient. MailKit only if the user approves the package | P2 |
| `HttpEmail` | Email | generic JSON e-mail API through YFex.Web (`AddApi`, per-scope `ApiKeyCredential`) with mappers for SendGrid, Amazon SES v2, Mailgun | P2 |
| `Twilio` | Sms, WhatsApp, Voice; `TwilioVerify` = `IVerificationProvider` | REST through YFex.Web (`BasicCredential` account SID/token per scope) | P2 |
| `MetaWhatsApp` | WhatsApp | Graph API `/{phone-number-id}/messages`, bearer per scope (YFex.Web) | P2 |
| `Zenvia`, `AmazonSns` | Sms, WhatsApp (Zenvia) | YFex.Web | later, on demand |
| `GsmModem` | Sms | YFex.Devices `AtClient` + `SmsPdu` (a shop modem) | later, on demand |
| `CapturingSender` | any | in memory (`YFex.Authentication.Testing`) | P1 |

Every adapter: AOT (STJ source-gen request/response types), no reflection, credentials only through YFex.Web's per-scope
endpoints (so FarmaViva can use its own Twilio account while another tenant uses the vendor's), and **never logs the code or the
full destination** (`VerificationCode` and `PhoneNumber` are `[Sensitive]`; the logging generator masks them).
