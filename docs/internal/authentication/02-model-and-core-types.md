# 02 — Model and types

## 1. Five things that are not the same

| Thing | Owner | Lives | Example |
|---|---|---|---|
| **Person** | nobody (the real world) | — | Ana Souza |
| **Account** | YFex.Authentication | realm `0=farmaviva` | `AccountId 0192f…`, e-mail verified, password, operator code 0142, PIN, Google login |
| **Actor** | YFex.Authorization (`ActorIdentity`) | per unit of work | `User:0192f…@0=farmaviva/1=loja-centro` |
| **App user record** | the app (Entity keyed by `AccountId`) | app database | `Employee { AccountId, Store, Role display, Admission }` |
| **Session** | YFex.Authentication | until sign-out/expiry | Ana on CAIXA-02 since 08:01, `password` + `operator-pin`, `SingleFactor` |

An app never extends `Account` with its own columns (model-layering rule): its tables reference `AccountId`. Authentication's
account carries only what authentication needs: status, verified contacts, credentials, external logins, a display name and a
preferred locale for messages.

**Broker model (⚑AN2).** Google, Microsoft, a code sent to WhatsApp — each is a **method** that proves the same account. A person
who signs in with Google today and with an e-mail code tomorrow is one account with one `AccountId`, so Authorization, Audit and the
app see one subject. External providers never reach Authorization directly.

## 2. Realms (⚑AN4)

A **realm** is where an account (and its e-mail, phone and operator code) is unique. The app declares realms; the library never
assumes tenant/store:

```csharp
// YFex Farma: staff accounts belong to the chain (tenant) — FarmaViva's admins manage only their people,
// FarmaViva can bring its own Entra ID, deleting the tenant deletes its accounts.
auth.Realms(r => r.PerScopeLevel(0));                 // realm = first scope rung ("0=farmaviva")

// YFex Marketplace: one shopper account across every seller.
auth.Realms(r => r.Shared("marketplace"));

// An app with both: staff per tenant, customers shared — chosen per sign-in endpoint / client.
auth.Realms(r => r.PerScopeLevel(0).And(r.Shared("customers")));
```

```csharp
namespace YFex.Authentication;

/// <summary>Where an account is unique. Canonical text: "shared:marketplace" or "scope:0=farmaviva".</summary>
public readonly record struct AccountRealm : ISpanFormattable, ISpanParsable<AccountRealm>, ICanonicalValue<AccountRealm>
{
    public static AccountRealm Shared(string name);                    // [a-z0-9-]{1,32}
    public static AccountRealm ForScope(string canonicalScopePrefix);  // from ScopeIdentity at the declared level
    public bool IsShared { get; }
    public string Value { get; }
}
```

- The realm of a sign-in is resolved **before** the account lookup, from the tenant Tenancy resolved for the request (host, header,
  path) or from the client's configuration. A request without a resolvable realm cannot sign in (fail closed).
- Membership **inside** a realm (which stores, which roles) is Authorization's scoped roles/permissions; Authentication only
  records the account's **home scope** (`ActorIdentity.HomeScope`) when the app sets one.
- A person who works for two chains has two accounts. Linking them is an app feature (open question, not v1).

## 3. Value objects

### 3.1 YFex core (⚑AN3, types only)

```csharp
namespace YFex;

/// <summary>An e-mail address: trimmed, domain lower-cased (IDN → punycode), local part lower-cased (pragmatic, as Entity.Public.Email).</summary>
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{email}", Identifier = true)]
public readonly struct EmailAddress : IEquatable<EmailAddress>, ISpanFormattable, ISpanParsable<EmailAddress>,
    IUtf8SpanFormattable, ICanonicalValue<EmailAddress>
{
    public const int MaxLength = 254;
    public ReadOnlySpan<char> Local { get; }     // slices of one string, no allocation
    public ReadOnlySpan<char> Domain { get; }
    public string Value { get; }
    public static bool TryParse(ReadOnlySpan<char> text, out EmailAddress address);
}

/// <summary>A phone number in E.164: country calling code + national number, digits only, max 15 digits.</summary>
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{last:4}", Identifier = true)]
public readonly struct PhoneNumber : IEquatable<PhoneNumber>, ISpanFormattable, ISpanParsable<PhoneNumber>, ICanonicalValue<PhoneNumber>
{
    public ushort CountryCallingCode { get; }    // 55, 1, 351 …
    public ReadOnlySpan<char> NationalNumber { get; }
    public string E164 { get; }                  // "+5511987654321"
    /// <summary>Parses "+55 (11) 98765-4321", "0055…"; a number without country code needs <paramref name="defaultCountryCallingCode"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, ushort defaultCountryCallingCode, out PhoneNumber number);
}
```

- **No carrier/line-type metadata** in core (libphonenumber-sized data). Whether a number can receive SMS/WhatsApp is the sender's
  answer (05 §5). National formats (`Telefone` with DDD) stay in YFex.Domain and convert: `telefone.ToPhoneNumber()`.
- `Email` in `YFex.Entity.Public` keeps working; it gets `ToEmailAddress()` and the domain library may later alias it (owner's call).

### 3.2 `YFex.Authentication.Public`

| Type | Shape | Notes |
|---|---|---|
| `AccountId` | `readonly record struct` over `Guid` (v7) | canonical text "D" lower-case = `ActorIdentity.Subject`; `ICanonicalValue` 16 bytes; `ILogValue` (not personal by itself) |
| `SessionId` | `readonly record struct` over `Guid` (v7) | **not a secret** (appears in tokens `sid`, `ActorScope.SessionId`, audit); the secret is the cookie/refresh token |
| `AccountStatus` | closed `enum : byte { PendingVerification, Active, Locked, Disabled, Deleted }` | library owns the set |
| `AccountRealm` | §2 | |
| `IdentityProviderName` | value object, `[a-z0-9-]{1,32}`, well-known `Google`, `Microsoft`, `Apple`, `GovBr`, `GitHub` + app-declared | per realm configuration (06 §6) |
| `ExternalAccountKey` | `(IdentityProviderName Provider, string Issuer, string Subject)` | the stable key of an external login; **never** e-mail (06 §5). Microsoft: `Issuer` = tenant issuer, `Subject` = `sub` |
| `AuthenticationMethod` | interned value object (ordinal, like `PermissionKey`), well-known `Password`, `OperatorPin`, `EmailCode`, `SmsCode`, `WhatsAppCode`, `VoiceCode`, `EmailLink`, `AuthenticatorApp`, `Passkey`, `RecoveryCode`, `External` (+ provider in the session), app-declared | maps to RFC 8176 `amr` (`pwd`, `pin`, `otp`, `sms`, `hwk`/`swk`, `mfa`) for tokens |
| `AuthenticationMethodSet` | 32-bit bitset over method ordinals | allocation-free "which methods did this session use / may this step use" |
| `AssuranceLevel` | closed `enum : byte { None, SingleFactor, MultiFactor, PhishingResistant }` | computed, never declared by callers (09 §1) |
| `DeliveryChannel` | interned value object, well-known `Email`, `Sms`, `WhatsApp`, `Voice` + app-declared (`push`, `telegram`) | domain-agnostic: an app can add a channel with a sender |
| `ChallengePurpose` | interned value object, well-known `SignIn`, `SecondFactor`, `VerifyContact`, `ResetPassword`, `ChangeContact`, `StepUp`, `Invitation` + app-declared (`confirm-payment`) | one MAC domain per purpose: a sign-in code never resets a password |
| `ChallengeId` | `Guid` v7 | |
| `VerificationCode` | `readonly struct`, ≤ 10 ASCII digits/letters in an `[InlineArray(10)]` buffer + length; `[Sensitive(Secret)]`; `ToString()` = `"******"` | no string for a code on the server path; `FixedTimeEquals` compares |
| `Password` | `readonly struct` over `string`, `[Sensitive(Secret)]`, `ToString()` redacted, `AsSpan()` | DTO-friendly wrapper; engine APIs take `ReadOnlySpan<char>` |
| `RefreshToken`, `SessionHandle` | `readonly struct` over the encoded text, `[Sensitive(Secret)]`, redacted | |
| `SignInFlowToken` | sealed `MessageToken` text (flow state), redacted | stateless multi-step flows (07 §1) |
| `AuthenticatedSession` | `sealed record` (`SessionId`, `AccountId`, `AccountRealm`, `HomeScope`, `AuthenticationMethodSet Methods`, `AssuranceLevel`, `DateTimeOffset AuthenticatedAt`, `InstallationId?`, `ActorIdentity? RealActor`, expiries) | what every host turns into a principal |
| `MaskedDestination` | `readonly record struct (DeliveryChannel, string Masked)` | "+55 11 9****-4321", "a***@farmaviva.com.br" — what the UI may show |

Value-object rules are the ecosystem's (Authorization README §10): `Parse`/`TryParse`/`ToString`/`.Value`, `TypeConverter`,
System.Text.Json converter (source-generated context), explicit string conversion only.

## 4. The account aggregate

```csharp
// (sketch) the engine's state; the SQL entity is in 12 §2
public sealed record Account(
    AccountId Id, AccountRealm Realm, AccountStatus Status,
    string DisplayName, string? PreferredCulture, string? HomeScope,
    ImmutableArray<VerifiedContact> Contacts,        // EmailAddress / PhoneNumber + verified at + primary + usable for sign-in
    ImmutableArray<CredentialSummary> Credentials,   // kind + id + created + last used (never the verifier)
    ImmutableArray<ExternalLogin> ExternalLogins,    // ExternalAccountKey + linked at + last used
    long SecurityStamp,                              // bumps on password/contact/factor change → old links, codes, refresh families die
    DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);
```

`SecurityStamp` is the one counter every outstanding artefact binds: magic links, reset links, refresh-token families and
remembered-device tokens carry the stamp they were issued at; a mismatch is a rejection. Changing a password therefore kills every
pending reset link and every other session in one write (08 §3).

## 5. Results: native `union` for flows, `[Union]` for hot paths (⚑AN24)

### 5.1 Cold — sign-in and friends (native `union`, clarity first)

```csharp
public sealed record SignedIn(AuthenticatedSession Session, IssuedCredentials Issued);       // cookie or tokens, per host
public sealed record SecondFactorRequired(SignInFlowToken Flow, AuthenticationMethodSet Allowed, MaskedDestination? CodeSentTo);
public sealed record ContactVerificationRequired(SignInFlowToken Flow, MaskedDestination CodeSentTo);
public sealed record PasswordChangeRequired(SignInFlowToken Flow, PasswordChangeReason Reason);   // Breached, PolicyChanged, AdminReset
public sealed record LockedOut(TimeSpan RetryAfter);
public sealed record NotAvailableOffline();                                                  // same meaning as Offline 05 §5
public sealed record SignInRejected(SignInRejection Reason);   // InvalidCredentials | AccountUnavailable | ProviderFailed | FlowExpired | HumanVerificationRequired

public union SignInResult(SignedIn, SecondFactorRequired, ContactVerificationRequired, PasswordChangeRequired,
                          LockedOut, NotAvailableOffline, SignInRejected);
```

- `SignInRejection.AccountUnavailable` (disabled/locked by an admin) is returned **only after** the secret was correct;
  before that every failure is `InvalidCredentials` (11 §1).
- Other cold unions: `RegistrationResult`, `ExternalSignInResult` (adds `LinkRequired(ExternalAccountKey, MaskedDestination)`),
  `ChallengeStartResult`, `RecoveryResult`, `StepUpResult`. Each is listed with its flow (04–09).

### 5.2 Hot — every request (`[Union]` tagged struct, no boxing)

```csharp
public enum TokenValidationKind : byte { None = 0, Accepted = 1, Rejected = 2 }
public enum TokenRejection : byte
{
    Malformed = 1, UnsupportedAlgorithm, UnknownKey, BadSignature, Expired, NotYetValid,
    WrongIssuer, WrongAudience, WrongType, SessionRevoked, BindingMismatch,
}

public readonly record struct TokenAccepted(AuthenticatedCaller Caller);     // cached class: principal + session facts
public readonly record struct TokenRejected(TokenRejection Reason);

[Union]
public readonly struct TokenValidation : IUnion, IEquatable<TokenValidation>
{
    private readonly AuthenticatedCaller? _caller;
    private readonly byte _tag;      // TokenValidationKind
    private readonly byte _reason;   // TokenRejection
    public TokenValidationKind Kind { get; }
    public bool TryGetValue(out TokenAccepted value);
    public bool TryGetValue(out TokenRejected value);
    public object? Value { get; }    // boxes — never on the hot path (memory: csharp-union-usage gotcha: no property patterns)
}
```

Same pattern: `ThrottleDecision` (`Allowed` | `Delayed(TimeSpan)` | `Blocked(DateTimeOffset until)`, 11 §2) and `ChallengeCheck`
(`Passed(ChallengeId)` | `WrongCode(int attemptsLeft)` | `Expired` | `Exhausted` | `Unknown`), both checked on every attempt.

## 6. Events

`AuthenticationEvents` (core probes, `Operations.MarkWith`, like `AuthorizationEvents`): `SignedIn`, `SignInFailed` (reason, never
the secret), `SignedOut`, `SessionRevoked`, `ChallengeSent` (channel, purpose, masked destination), `ChallengeFailed`, `PasswordChanged`,
`PasswordReset`, `ContactVerified`, `ContactChanged`, `FactorAdded`, `FactorRemoved`, `ExternalLoginLinked/Unlinked`, `AccountLocked`,
`AccountDisabled`, `ImpersonationStarted/Ended`, `RefreshTokenReuseDetected`. YFex.Logging records them; YFex.Audit maps the
security-relevant ones to durable entries (11 §5).
