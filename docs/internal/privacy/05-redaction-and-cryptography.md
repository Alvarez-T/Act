# 05 — Redaction primitives & cryptography

All primitives live in `YFex.Privacy` (runtime), use BCL cryptography only, and write into caller-provided spans/buffer writers.
Consumers (logging consumer thread, persistence writers, AI gateway, export writer) call them. Applications rarely do.

## 1. Redaction actions and policy

```csharp
namespace YFex.Privacy;

/// Closed set: what may happen to a classified value at a destination.
public enum RedactionAction : byte
{
    Keep = 0,          // value as is
    Mask = 1,          // display mask (02 §7): ***.456.789-**
    Pseudonymize = 2,  // keyed HMAC pseudonym (§3): #h:9f2c41d07a3be1c2 — joinable, not reversible
    Encrypt = 3,       // AES-GCM envelope with the subject key (§6): reversible by authorised readers, shreddable
    Tokenize = 4,      // random token + vault (§7): reversible through the vault, deletable
    Erase = 5,         // <redacted:SensitivePersonal> — "a value existed", nothing else
}

/// Closed set: where data goes. The engine owns the handling per destination.
public enum DataDestination : byte
{
    DiagnosticLog = 0, AuditLog = 1, Telemetry = 2, CloudLanguageModel = 3, LocalLanguageModel = 4,
    ExternalSystem = 5, Display = 6, SubjectExport = 7, Search = 8,
}
```

`RedactionPolicy` is a `[DataDestination, DataSensitivity]` table of `RedactionAction` (9 × 7 bytes), resolved per tenant (04 §11) and
read with one array index on hot paths. Defaults are the table in 02 §2. **Hard limits** (configuration cannot relax them; attempts
fail at startup with a policy error):

- `Financial` and `Secret` → `Erase` for every destination except `Display` (`Mask` for `Financial`, hidden for `Secret`) and the
  credential store.
- `SensitivePersonal` → never `Keep`/`Pseudonymize` for `CloudLanguageModel`, `Telemetry`, `ExternalSystem`.
- A tenant may tighten any cell (e.g. `Personal` → `Erase` in diagnostic logs).

The result of redacting one value is a hot-path tagged union (no boxing, the `YFex.Cqrs/Result.cs` pattern):

```csharp
namespace YFex.Privacy;

[Union]
public readonly struct RedactedValue : IUnion
{
    private readonly Pseudonym _pseudonym;          // Pseudonymized
    private readonly DataToken _token;              // Tokenized
    private readonly int _start, _length;           // Kept / Masked / Encrypted: range in the caller's output buffer
    private readonly DataSensitivity _sensitivity;  // Erased
    private readonly byte _tag;                     // 0 = none (default struct), else (byte)RedactionAction + 1

    public bool HasValue => _tag != 0;
    public RedactionAction Action => (RedactionAction)(_tag - 1);    // only meaningful when HasValue
    public bool TryGetPseudonym(out Pseudonym pseudonym);
    public bool TryGetToken(out DataToken token);
    public bool TryGetWrittenRange(out int start, out int length);   // Keep / Mask / Encrypt wrote into the output buffer
    public bool TryGetErased(out DataSensitivity sensitivity);
    public object? Value { get; }                                     // IUnion; boxes only when used
}

public sealed class Redactor   // one per policy snapshot; thread-safe, immutable
{
    public RedactionAction ActionFor(in DataClassification classification, DataDestination destination);

    public RedactedValue Redact<T>(in T value, DataDestination destination, IBufferWriter<byte> output)
        where T : IClassifiedData<T>;

    public RedactedValue RedactText(ReadOnlySpan<char> value, in DataClassification classification,
                                    DataDestination destination, IBufferWriter<byte> output);
}
```

## 2. Masks at run time

Value objects mask through generated code (02 §8). Raw strings use presets (`Masks.TryWritePreset`). Tenant-defined patterns compile
once:

```csharp
namespace YFex.Privacy;

public sealed class MaskPattern
{
    public static MaskPattern Parse(string pattern);                         // validates; YFP0009 rules at run time
    public int MaxLength { get; }
    public bool TryApply(ReadOnlySpan<char> source, Span<char> destination, out int written);   // fail closed → hidden
}
```

The two `MaskIpAddress` copies (Profiling, YFex.System) become `Masks.TryWriteIpAddress` (span in, span out; today they allocate
through `Split`/`Contains`/concatenation).

## 3. Pseudonyms (keyed HMAC, ⚑P11)

### 3.1 Why not `SHA256(value + salt)`

A hash is only as hard to reverse as the input space is large. Valid CPFs are about 10⁹, IPv4 addresses 2³², MAC addresses roughly
2²⁴ per vendor prefix, Brazilian mobile numbers about 10⁹. A laptop computes ~10⁹ SHA-256 per second, so a "salted" hash with a
salt from a configuration file (Profiling) or **no salt** (YFex.System MAC hash) is reversed in seconds to minutes. The fix is a
**keyed** function (HMAC) whose key never leaves the key vault. Without the key, enumeration is impossible. With the key, the
pseudonym is still joinable ("all operations of this customer"), which is the point.

### 3.2 Types

```csharp
namespace YFex.Privacy;

/// 128-bit truncated HMAC-SHA256. Stored as 16 bytes; displayed as "#h:" + first 16 hex chars (64 bits).
public readonly struct Pseudonym : IEquatable<Pseudonym>, ISpanFormattable, IUtf8SpanFormattable
{
    public byte KeyVersion { get; }                              // which domain key produced it (rotation, §5.4)
    public bool TryWriteBytes(Span<byte> destination);           // 16 bytes
    public bool TryFormat(Span<char> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider);   // "#h:9f2c41d07a3be1c2"; format "F" = 32 hex
}

/// Open set of pseudonym domains. Different domains give unlinkable pseudonyms for the same value.
public readonly record struct PseudonymDomain(string Key)
{
    public static readonly PseudonymDomain Log = new("log");               // diagnostic + audit joins
    public static readonly PseudonymDomain Telemetry = new("telemetry");   // Profiling (replaces TransportSaltKey)
    public static readonly PseudonymDomain Analytics = new("analytics");   // BI exports; rotated yearly
    public static PseudonymDomain Identifier(string typeKey) => new("identifier:" + typeKey);   // blind indexes (§4)
}

public sealed class Pseudonymizer   // per tenant scope; keys cached; thread-safe
{
    public Pseudonym Pseudonymize<T>(in T value, PseudonymDomain domain) where T : IClassifiedData<T>;   // canonical bytes → HMAC
    public Pseudonym Pseudonymize(ReadOnlySpan<char> normalizedText, PseudonymDomain domain);            // raw strings: caller normalizes
    public Pseudonym Pseudonymize(ReadOnlySpan<byte> canonical, PseudonymDomain domain);
}
```

**How it works:** `key(domain, version) = HKDF-SHA256(ikm: tenant pseudonym secret, info: "yfex.pseudonym.v1|" + domain.Key + "|" +
version)`. The 32-byte derived key is cached per (tenant, domain, version). `Pseudonymize<T>` writes `T.TryWriteCanonical` into a
`stackalloc` buffer (`T.MaxCanonicalLength`), calls `HMACSHA256.HashData(key, canonical, hash)` on a 32-byte stack span, and keeps
the first 16 bytes. Zero allocation, about 1 µs.

**Truncation:** 128 bits stored means no realistic collision. The 64-bit display form has a collision probability of about 3×10⁻⁶
across 10⁷ customers of one tenant, which is acceptable for reading logs, while joins use the full 128 bits.

**✍ Before** (Profiling) → **after**

```csharp
// before: reversible for low-entropy inputs, salt is configuration text
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid + _salt));
var deviceIdHash = Convert.ToHexString(hash).ToLowerInvariant();

// after
var deviceId = pseudonymizer.Pseudonymize(machineGuid, PseudonymDomain.Telemetry);   // Pseudonym; formats as #h:…
```

## 4. Blind indexes (search encrypted or sensitive columns)

A blind index is a pseudonym in the domain `identifier:<type key>`. It is **the same across tables** of one tenant, so it locates a
subject everywhere (07 §2) and supports equality search on encrypted columns:

```sql
-- cliente.cpf_cnpj is encrypted (envelope); cliente.cpf_cnpj_index is the blind index (binary(16))
SELECT id FROM cliente WHERE cpf_cnpj_index = @index;      -- @index = pseudonymizer.Pseudonymize(in cpf, PseudonymDomain.Identifier("cpf"))
```

Limits: equality only (no `LIKE`, no prefix, no range). For "search customer by the first digits of the CPF", keep a separate
**truncated** index (first 5 digits, low selectivity by design) or search by name. Making this trade-off visible is why encryption
of `PersonalIdentifier` columns is opt-in (⚑P15, 08 §3).

## 5. Keys (⚑P12)

### 5.1 Hierarchy

```
Root key ─────────────── IRootKeyProvider (environment variable, file + OS protection, DPAPI on Windows, cloud KMS later)
  └─ Tenant key-encryption key (KEK), versioned ── wrapped by the root key, stored in the vault
       ├─ Subject data keys (one per data subject)     ── wrapped by the tenant KEK; DELETE = crypto-shred that subject
       ├─ Field keys (per data category, versioned)    ── non-subject data at rest (credentials, financial tokens)
       ├─ Pseudonym secret (versioned)                 ── HKDF input for all pseudonym domains (§3)
       └─ Intake key pair (ECDH P-256)                 ── offline devices seal for the server (§5.5)
Device key pair (ECDsa P-256, per installation) ─────── signs offline consent/approvals (04 §7, validation D34)
Device storage key ───────────────────────────────────── today's IKeyProvider (Persistence.Encryption), now a vault key kind
```

### 5.2 Contracts

```csharp
namespace YFex.Privacy;

public readonly record struct KeyId(Guid Value, byte Version);       // 17 bytes on the wire

public enum KeyKind : byte { SubjectData = 1, Field = 2, PseudonymSecret = 3, Intake = 4, DeviceStorage = 5, DeviceSigning = 6 }

public interface IRootKeyProvider
{
    string Name { get; }
    ValueTask<int> WrapAsync(ReadOnlyMemory<byte> key, Memory<byte> wrapped, CancellationToken cancellationToken);
    ValueTask<int> UnwrapAsync(ReadOnlyMemory<byte> wrapped, Memory<byte> key, CancellationToken cancellationToken);
}

public interface IKeyVault
{
    /// Creates on first use. Returns a lease: key bytes live in pinned, zeroed-on-dispose memory.
    ValueTask<KeyLease> GetOrCreateSubjectKeyAsync(KeyScope scope, DataSubjectReference subject, CancellationToken cancellationToken);
    ValueTask<KeyLease?> TryGetKeyAsync(KeyScope scope, KeyId id, CancellationToken cancellationToken);   // null = destroyed/unknown
    ValueTask<KeyLease> GetCurrentAsync(KeyScope scope, KeyKind kind, string? purposeOrCategory, CancellationToken cancellationToken);

    /// Crypto-shredding. Irreversible. Writes a tombstone (who, when, request id) and signals caches on every instance.
    ValueTask<bool> DestroySubjectKeyAsync(KeyScope scope, DataSubjectReference subject, string requestId, CancellationToken cancellationToken);

    ValueTask RotateAsync(KeyScope scope, KeyKind kind, string? purposeOrCategory, CancellationToken cancellationToken);
}

public readonly record struct KeyScope(string Tenant);       // top scope key (controller); from IScopeContext by default

public sealed class KeyLease : IDisposable                    // pooled; CryptographicOperations.ZeroMemory on dispose
{
    public KeyId Id { get; }
    public ReadOnlySpan<byte> Key { get; }
}
```

Implementations: `InMemoryKeyVault` (tests), `FileKeyVault` (single desktop / dev, wrapped keys in a JSON file), `SqlKeyVault`
(YFex.Privacy.Data, server). Root providers: `EnvironmentRootKeyProvider`, `ProvidedRootKeyProvider`, `DataProtectionRootKeyProvider`
(adapter of today's `DataProtectionKeyProvider`, which uses the framework's ASP.NET Core DataProtection), `DpapiRootKeyProvider` in
the Windows platform package (`ProtectedData`, already used by Automation). Cloud KMS providers (AWS KMS, Azure Key Vault) are
**seams only**: each needs a NuGet package, so not without approval.

### 5.3 `KeyRing` (generalized from `OverrideTokens`)

The Rules approval code already keeps "current key id + previous keys" for HMAC. `KeyRing` generalizes it: verify/decrypt with any
non-destroyed version, sign/encrypt only with the current one. `OverrideTokens` can adopt it with no behaviour change (optional
migration).

### 5.4 Rotation

| Key | Trigger | Cost | Effect |
|---|---|---|---|
| Root | yearly / on incident | re-wrap tenant KEKs (milliseconds) | none on data |
| Tenant KEK | yearly / on incident | re-wrap subject + field keys (batch job) | none on data |
| Subject key | on suspected compromise only | re-encrypt that subject's values (rights participants, 07 §3) | old version destroyed after re-encryption |
| Field key | yearly or after 2³¹ encryptions (AES-GCM random-nonce limit, NIST SP 800-38D) | new writes use the new key; old ciphertexts readable until re-encrypted lazily ("rewrap on touch") | key id in the envelope tells readers which key |
| Pseudonym secret | per domain policy: `analytics` yearly; `log` and `identifier:*` **never** within retention (would break joins and blind indexes) | none (new version for new values) | intentional unlinkability across versions |

### 5.5 Offline devices

A POS creating a customer offline cannot fetch a subject key. It **seals for the server**: ephemeral ECDH P-256 against the tenant's
**intake public key** (cached on the device, not secret) + HKDF + AES-GCM (ECIES, all BCL). Only the server can open it. During
sync the server re-seals the value with the subject key. Devices never hold subject keys or the KEK. Data the device itself must
show offline (the customer it just typed) stays in the device's encrypted local store (`DeviceStorage` key) and is purged by TTL
or by an erasure signal (07 §5).

## 6. AES-GCM envelope v2 (⚑P13)

### 6.1 Layout (normative)

```
offset  size  field
0       1     version          0x02 (0x01 is today's Persistence.Encryption layout; readers keep supporting it)
1       1     key kind         KeyKind
2       16    key id           Guid
18      1     key version
19      12    nonce            RandomNumberGenerator, per message
31      n     ciphertext       |plaintext|
31+n    16    tag
AAD = UTF-8 "yfex.envelope.v2|" + tenant + "|" + owner type key + "|" + member name
```

The AAD binds a ciphertext to its tenant and location. A value copied from `cliente.cpf_cnpj` of tenant A into another column or tenant
fails authentication instead of decrypting. Overhead: 47 bytes per value.

### 6.2 API

```csharp
namespace YFex.Privacy;

public static class AesGcmEnvelope
{
    public static int SealedLength(int plaintextLength) => plaintextLength + 47;
    public static void Seal(KeyLease key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> destination);
    public static EnvelopeOpenStatus TryOpen(IKeyResolver keys, ReadOnlySpan<byte> sealedValue, ReadOnlySpan<byte> associatedData,
                                             Span<byte> plaintext, out int written);
    public static bool TryReadHeader(ReadOnlySpan<byte> sealedValue, out EnvelopeHeader header);   // version, kind, key id (no key needed)
}

public enum EnvelopeOpenStatus : byte { Opened, KeyDestroyed, KeyUnknown, AuthenticationFailed, UnsupportedVersion }
```

`KeyDestroyed` is a normal outcome after crypto-shredding. Readers map it to `default(T)` plus a `Shredded` marker (08 §4), never to
an exception. `AesGcm` instances are cached per key in a small pool (constructing one per call costs a key schedule).

### 6.3 Existing implementations converge

| Today | After |
|---|---|
| `AesGcmValueProtector` (0x01, one device key, no AAD) | `IValueProtector` stays as the store-level contract; implementation = envelope v2 with `KeyKind.DeviceStorage`, AAD = store name; reads 0x01 through a legacy path; re-encrypts on write |
| `DataProtectionKeyProvider`, `ProvidedKeyProvider` (`IKeyProvider`) | become `IRootKeyProvider`s (or `DeviceStorage` key sources) |
| `AesSecretProtector` (Web.Data: nonce ‖ ct ‖ tag, base64, optional HKDF(TenantSalt)) | envelope v2 with a tenant `Field` key, category `credentials`; legacy rows recognized by a column marker (`secret_format = 0`) and re-sealed on next write |
| `OverrideTokens` HMAC ring | optional `KeyRing` adoption |
| `PortableSecretStore` (plain text files) | encrypts files with a `DeviceStorage` key from the best available root provider (DPAPI on Windows; file with OS permissions + warning elsewhere). Secrets remain app credentials (not personal data); only the root-key plumbing is shared |

## 7. Tokenization (⚑P14)

```csharp
namespace YFex.Privacy;

/// "tok_" + 26 Crockford base32 chars (128 random bits). Reveals nothing about the value; not derived from it.
public readonly struct DataToken : IEquatable<DataToken>, ISpanFormattable, IUtf8SpanFormattable { … }

public interface ITokenVault
{
    /// Same value + same subject → same token (looked up by blind index), so joins on tokens work.
    ValueTask<DataToken> TokenizeAsync<T>(KeyScope scope, DataSubjectReference subject, T value, CancellationToken cancellationToken)
        where T : IClassifiedData<T>;

    /// Requires an access context: caller permission + purpose. Every call is audited (who, which token, why).
    ValueTask<DetokenizeResult> DetokenizeAsync(KeyScope scope, DataToken token, PrivacyAccess access, CancellationToken cancellationToken);

    ValueTask<int> DeleteForSubjectAsync(KeyScope scope, DataSubjectReference subject, CancellationToken cancellationToken);
}

public sealed record PrivacyAccess(string Principal, Purpose Purpose, string? Justification);

public readonly union DetokenizeResult(Detokenized, TokenDeleted, AccessDenied);   // cold path: native union is fine
public sealed record Detokenized(ReadOnlyMemory<byte> Canonical, DataClassification Classification);
public sealed record TokenDeleted(DateTimeOffset At);
public sealed record AccessDenied(string Reason);
```

Uses: sharing references with external systems (a delivery partner gets `tok_…` and asks the API when it needs the phone); financial
values kept out of business tables; the AI gateway's long-lived placeholders (08 §8). The vault stores the value sealed with the
subject key, so crypto-shredding also kills tokens.

**Format-preserving encryption** (a "CPF-shaped" ciphertext) is **not** proposed: FF1/FF3-1 are not in the BCL, and an in-house
implementation of a format-preserving cipher is a risk we should not take without review.

## 8. Crypto-shredding end to end

1. Values that must survive in immutable stores (audit entries, event payloads, backups) are sealed with the **subject key** at write
   time (audit: logging 07 §4; events: 08 §4; vault tokens: §7).
2. Erasure calls `IKeyVault.DestroySubjectKeyAsync`. The vault deletes the wrapped key, writes a tombstone and publishes
   `KeyDestroyed(scope, keyId)`. Every instance drops the key from its cache within the signal latency, or within at most the cache
   TTL (default 5 min) if a signal is lost.
3. From then on, `TryOpen` returns `KeyDestroyed`. Hash chains stay valid (the ciphertext is unchanged). Projections and readers
   show `default`/`<erased>`.
4. **Backups:** a backup taken before the erasure still contains the wrapped subject key. Two rules close this: the **vault is
   backed up separately with a short retention** (default 35 days, so shredding is complete when the last vault backup expires),
   and every restore replays the **erasure log** (07 §6) before the system goes live.

## 9. Performance budgets (benchmarks gate phase 2)

| Operation | Budget | Allocation |
|---|---|---|
| `Classified.TryMask(in Cpf)` | ≤ 60 ns | 0 B |
| `Pseudonymizer.Pseudonymize(in Cpf, Log)` (key cached) | ≤ 1.2 µs | 0 B |
| `AesGcmEnvelope.Seal` 64-byte value (`AesGcm` pooled) | ≤ 1.5 µs | 0 B |
| `ConsentSnapshot.Allows(purpose)` | ≤ 2 ns | 0 B |
| `Redactor.ActionFor` | ≤ 2 ns | 0 B |
| `IKeyVault.TryGetKeyAsync` cache hit | ≤ 100 ns | 0 B (`ValueTask` completed synchronously) |
