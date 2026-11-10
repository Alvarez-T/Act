# 03 — Home, layering and dependencies

## 1. Three homes

| Home | Contains | Depends on | Why there |
|---|---|---|---|
| **YFex core** `YFex/src/YFex`, namespaces `YFex.Cryptography` and `YFex.Canonical` | **types only, no implementations** (⚑K2 as confirmed): the value types that appear **inside other libraries' contracts** (`Hash256`, `KeyId`, `SignatureAlgorithm`, `DetachedSignature`) and the contract `ICanonicalValue<TSelf>` | BCL only | Offline's `BundleHeader` (core namespace `YFex.Offline`), Audit's anchors (`YFex.Audit.Public`), Rules' decision digest and Authorization's `.Public` leaf must name these types **without** referencing an implementation library (same reasoning as `YFex.Privacy` declarations in core, ⚑P2) |
| **`YFex.Cryptography`** sibling repo `C:\Dev\YFex\YFex.Cryptography` | every implementation: canonical writer/reader, hashing, keys, rings, root-key contract, sealing, MACs, derivation, passwords, signatures, recipient sealing, chains, Merkle trees, certificates, capabilities, DI | core + `Microsoft.Extensions.DependencyInjection.Abstractions` only (no logging abstractions: events go through core probes, `YFex.Diagnostics.Operations`) | cross-platform, BCL only, AOT. One reviewed place |
| **`YFex.Cryptography.Windows`** | `CngRootKeyProvider`, `CngSigningKeyStore`, `WindowsKeyProtection` (TPM detection) | `YFex.Cryptography`, `net11.0-windows` or `[SupportedOSPlatform("windows")]` | CNG/TPM is Windows-only (CA1416). Linux/macOS/Android key stores come later as sibling packages |

### Repo layout (mirrors YFex.Logging and YFex.Privacy)

```
C:\Dev\YFex\YFex.Cryptography\
  cryptography.slnx
  Directory.Build.props                  net11.0, LangVersion preview, Nullable, IsAotCompatible, TreatWarningsAsErrors for IL2xxx/IL3xxx
  src\YFex.Cryptography\                 the library (references ..\..\YFex\src\YFex\YFex.csproj)
  src\YFex.Cryptography.Windows\
  src\YFex.Cryptography.Testing\         known-answer vectors, golden-format helpers, in-memory stores, deterministic keys for tests
  tests\YFex.Cryptography.Tests\         unit, KAT, golden, concurrency, architecture (banned API) tests
  tests\YFex.Cryptography.Windows.Tests\ CNG software KSP always; TPM tests skip when no TPM
  tests\YFex.Cryptography.AotSmoke\      native AOT app exercising every public entry point, 0 IL warnings
  benchmarks\YFex.Cryptography.Benchmarks\
```

No `.Generator` project: nothing needs generated code. Canonical encoding of value objects uses static-abstract interfaces
(`ICanonicalValue<TSelf>`), which the existing Privacy/Rules generators can implement where they already emit code.

## 2. Core contracts (normative)

```csharp
namespace YFex.Cryptography;

/// SHA-256 digest as a value: 32 bytes inline ([InlineArray]), no allocation, safe to persist and compare. TYPE ONLY (⚑K2): it does
/// not hash. Implemented 2026-10-04 in YFex/src/YFex/Cryptography/Hash256.cs. Moved here from Audit 06 §2 (⚑A31 → ⚑K11).
public readonly struct Hash256 : IEquatable<Hash256>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<Hash256>,
                                 ILogValue<Hash256>
{
    public const int Size = 32, HexLength = 64, ShortHexLength = 16;
    public Hash256(ReadOnlySpan<byte> bytes);                                    // exactly 32 bytes
    public static Hash256 Zero { get; }                                          // default = 32 zero bytes (chain genesis)
    public static Hash256 Read(ReadOnlySpan<byte> source);
    public static bool TryRead(ReadOnlySpan<byte> source, out Hash256 value);
    public ReadOnlySpan<byte> AsSpan();                                          // [UnscopedRef] view of the inline bytes
    public void WriteTo(Span<byte> destination);
    public bool FixedTimeEquals(in Hash256 other);                               // CryptographicOperations.FixedTimeEquals
    // ==, Equals, GetHashCode; text = 64 lower-case hex chars, format "S" = first 16 chars; parsing accepts either case
}

/// Identifies ONE key version. Rotation creates a new KeyId in the same ring (Guid version 7: sortable by creation).
public readonly record struct KeyId(Guid Value) : ISpanFormattable, IUtf8SpanFormattable
{
    public const int Size = 16;
    public static KeyId New() => new(Guid.CreateVersion7());
    public void WriteTo(Span<byte> destination);                                 // big-endian RFC 9562 byte order
    public static KeyId Read(ReadOnlySpan<byte> source);
}

/// Closed set owned by YFex.Cryptography. Written as one byte in every signed format (crypto agility, 08).
public enum SignatureAlgorithm : byte
{
    EcdsaP256Sha256 = 1,                // IEEE P1363, 64 bytes — the default
    // reserved, not implemented before ⚑K10 is revisited:
    MLDsa65 = 16,                       // FIPS 204, 3 309 bytes
    CompositeMLDsa65EcdsaP256 = 17,     // hybrid; BCL type still [Experimental("SYSLIB5006")]
}

/// A signature that travels apart from what it signs (bundles, envelopes, anchors, receipts, approval tokens v2).
public readonly record struct DetachedSignature(SignatureAlgorithm Algorithm, KeyId Key, ReadOnlyMemory<byte> Value);
```

```csharp
// assembly YFex.Cryptography (implementation), namespace YFex.Canonical
namespace YFex.Canonical;

/// Deterministic binary encoding for anything hashed, MACed or signed (never JSON). Rules in 05 §5.
/// Spec owned by Audit 02 §9 / 06 §2 (⚑A31); this design asks for the neutral namespace YFex.Canonical so Offline, Privacy,
/// Authorization and Rules use it without an "Auditing" import (⚑K2).
public ref struct CanonicalWriter
{
    public CanonicalWriter(IBufferWriter<byte> output);
    public void WriteTag(byte tag);
    public void WriteUnsigned(ulong value);              // LEB128 varint
    public void WriteSigned(long value);                 // zigzag varint
    public void WriteFixed64(ulong value);               // little endian, for hashes of numbers that must stay fixed width
    public void WriteText(ReadOnlySpan<char> value);     // NFC, UTF-8, varint length prefix
    public void WriteBytes(ReadOnlySpan<byte> value);    // varint length prefix
    public void WriteGuid(Guid value);                   // 16 bytes, big endian
    public void WriteInstant(DateTimeOffset value);      // UTC ticks, signed varint
    public void WriteDecimal(decimal value);             // normalized (no trailing zeros), sign + scale + 96-bit magnitude
    public void Write<T>(in T value) where T : ICanonicalValue<T>;
}

// assembly YFex core (contract only, ⚑K2)
public interface ICanonicalValue<TSelf> where TSelf : ICanonicalValue<TSelf>
{
    static abstract int MaxCanonicalLength { get; }      // lets callers stackalloc
    static abstract bool TryWriteCanonical(in TSelf value, Span<byte> destination, out int written);
}
```

`Cpf`, `Cnpj`, `Money`, `Quantity`, `ValidityPeriod`, `InstallationId` implement `ICanonicalValue<TSelf>`. That is what Privacy 05 §3
calls `T.TryWriteCanonical`: the same member, now declared once in core instead of on `IClassifiedData<T>`.

**What is deliberately not in core:** the canonical writer/reader, any hashing, keys (`SecretKey`), anything async, anything with a store, any `AesGcm`/`ECDsa` usage, the sealing
format. A library that only needs to **name** a digest or a key id references core. A library that needs to **compute** a MAC,
signature or encryption references `YFex.Cryptography`. **Core computes nothing** (⚑K2 as confirmed: "so logic does not
bleed into core"). Leaf contracts that need an unkeyed digest but may not reference the library (YFex.Rules, YFex.Audit.Public,
YFex.Authorization.Public) call the BCL one-shot `SHA256.HashData` into a `Hash256`. Unkeyed hashing is not one of the operations
reserved to the library (02 §4.3), so this is allowed.

## 3. Who depends on whom

```
                                    YFex core (Hash256, KeyId, SignatureAlgorithm, DetachedSignature, ICanonicalValue: types only)
                                         ▲            ▲              ▲                 ▲
                 ┌───────────────────────┤            │              │                 │
        YFex.Cryptography ◄── YFex.Cryptography.Windows                    YFex.Rules (Hash256 type + BCL SHA-256)
                 ▲                                                         YFex.Authorization.Public (names Hash256/KeyId only)
   ┌─────────┬───┴─────┬──────────┬──────────────┬───────────────┬──────────────┬─────────────┐
 Privacy  Offline   Audit    Authorization   Persistence.     Messaging.Rpc     Web.Data     Automation
 runtime  (+Broker, engine   runtime         Encryption       (EventChannel)    (secrets)    (secret stores)
          Central)
```

Checks against cycles and layering rules:

| Library | References `YFex.Cryptography`? | Why / why not |
|---|---|---|
| YFex core | **no** (it is below) | core holds contracts only |
| YFex.Validation | no | no cryptography |
| YFex.Rules | **no** — core `Hash256` only | CLAUDE.md: Rules → core, Validation, Authorization.Public only. Approval tokens leave Rules with ⚑Z12; the decision digest is BCL `SHA256.HashData` into the core `Hash256` type |
| YFex.Authorization.Public | no | strict leaf (core + BCL, enforced by test, ⚑Z1). It may **name** `Hash256`/`KeyId` (core) |
| YFex.Authorization (runtime) | yes | HMAC and device approval tokens, key rings |
| YFex.Privacy (runtime, Tier 2) | yes | pseudonyms, vault, envelope, intake sealing. Privacy **Tier 1** (core declarations + generator) stays without it |
| YFex.Offline (+ `.Broker`, `.Central`, `.Windows`) | yes (`.Windows` → `YFex.Cryptography.Windows`) | installation keys, CA, bundles, envelopes, verifiers |
| YFex.Audit (engine/server) | yes | chain/Merkle, anchor signing. `YFex.Audit.Public` uses the core `Hash256` type + BCL SHA-256 for blob digests (done 2026-10-04) |
| YFex.Logging | **no** | logging never signs or encrypts by itself. Relayed batches are signed by Offline (evidence key) through a seam; pseudonyms come through Privacy |
| YFex.Persistence (abstractions) | no | |
| YFex.Persistence.Encryption | yes | becomes a thin adapter (09 §2) |
| YFex.Messaging.Rpc | yes | `EventChannel` audience tokens (C4). Already layer 4, heavy dependencies anyway |
| YFex.Cqrs | no | |
| YFex.Web / Web.Data | Web.Data yes; `ISecretProtector` moved to YFex.Cryptography (2026-11-05, atlas F08) | |
| YFex.Devices | optional later | remoting reuses Offline's installation certificates (⚑O6), not a direct dependency |
| YFex.Fiscal | later, certificates only | NF-e XMLDSig needs `System.Security.Cryptography.Xml` (package, ⚑K14). Stays in Fiscal |
| YFex.Payment | no (P-class) | webhook formats are provider-mandated; C9 fix uses core canonical encoding |
| YFex.Automation | yes (secret stores) | outside `C:\Dev\YFex`, same rules |

`YFex.Cryptography` itself references **only** core (and DI abstractions). It never references Privacy, Offline, Logging, Persistence,
Tenancy or Authorization. Its observability uses core probes (`Operations.Start`/`MarkWith` in `YFex.Diagnostics`), so YFex.Logging
records "key ring rotated", "authentication failed on open" without a dependency in either direction.

## 4. What each consumer keeps

| Consumer | Keeps (meaning, policy) | Takes from YFex.Cryptography (mechanism) |
|---|---|---|
| **Privacy** | `RedactionPolicy`, `Pseudonymizer` + `PseudonymDomain`, blind indexes, `DataToken`/`ITokenVault`, `IKeyVault` (subject keys, KEK hierarchy, tombstones, shredding signals), `KeyKind` enum (as the sealed value's `key class` byte) | `KeyRing`, `SecretKey`, `IRootKeyProvider`, `SealedValue`, `MessageAuthentication`, `KeyDerivation`, `RecipientSealing` (offline intake). **Moves out of privacy 05:** `KeyId`, `KeyLease`, `IRootKeyProvider`, `AesGcmEnvelope`, `EnvelopeOpenStatus`, `KeyRing` |
| **Offline** | installation identity and roles, the **two named keys** (transport, evidence) and their lifetimes, tenant CA **policy** (URN SANs, validity, roles), `SignedBundle` + `BundleKind`, `OfflineEnvelope`, `TrustedTimeProvider`, offline login policy (who, how long, lockout) | `ISigningKeyStore` (+ `CngSigningKeyStore`), `CertificateAuthority`, `CertificateTrust`, `DetachedSignature`, `SignedPayload`, `PasswordHashing`, `CngRootKeyProvider`, `SealedValue` for verifiers at rest |
| **Audit** | entry model, chain partitioning (tenant, class, day), sequencer, anchors and their cadence, gaps, verification jobs, exports | `HashChain`, `MerkleTree`, `ISigningKey` for anchors, `SignatureVerifier`, core `Hash256` |
| **Authorization** | `ApprovalToken` shape and semantics, kinds, policies, approver re-checks, events | `MessageAuthentication` + `KeyRing` (server tokens), `ISigningKey` + `SignatureVerifier` (device/site tokens), `MessageDomain` |
| **Persistence.Encryption** | `EncryptedKeyValueStore` decorator, the `AddYFexStorageEncryption` entry point | everything else |
| **Messaging.Rpc** | event audiences | `MessageAuthentication` tokens with expiry and a shared ring |
| **Web.Data / YFex.Web** | which credential columns are secret | `SealedValue` + a per-tenant ring |
| **Automation** | `ISecretStore` contract and its env-var-first lookup | `SealedValue` + root-key ladder |

## 5. CLAUDE.md and index changes (when ⚑K1 is confirmed, Phase 0)

- Solution map: `YFex/` core line gains "+ YFex.Cryptography / YFex.Canonical: Hash256, KeyId, SignatureAlgorithm, DetachedSignature,
  ICanonicalValue (types only) — implementations, canonical writer included, in sibling repo C:\Dev\YFex\YFex.Cryptography".
- Dependency layers: add "YFex.Cryptography (→ core only) sits beside Layer 0.5; YFex.Rules must not reference it".
- Fix F12 there at the same time: the `Messaging.Rpc.Sqlite/IndexedDb/Encryption` lines point to `Persistence.*`.
- Common tasks row: "Encrypt / sign / MAC / hash for security → `YFex.Cryptography` (docs/internal/cryptography/10); never `AesGcm`,
  `HMACSHA256`, `ECDsa` directly outside it (architecture test)".
