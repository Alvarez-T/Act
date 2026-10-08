# 07 — Modern .NET usage and API conventions

## 1. Spans first, one-shot statics, known sizes

- Every operation has a `ReadOnlySpan<byte>` in / `Span<byte>` out form, and sizes are computable up front (`GetSealedLength`,
  `SignatureLength`, `PasswordHashing.VerifierLength`). Callers `stackalloc` small buffers (≤ 256 bytes) and rent above that.
- Inside the library only the BCL **static one-shots** are used (`HMACSHA256.HashData`, `SHA256.HashData`, `HKDF.DeriveKey/Expand`,
  `Rfc2898DeriveBytes.Pbkdf2`, `CryptographicOperations.HashData`). They are allocation-free and thread-safe. `HashAlgorithm` instances,
  `IncrementalHash` (except for streams) and `ICryptoTransform` are not used.
- Rented buffers that held secrets are cleared before return: `ArrayPool<byte>.Shared.Rent` then `CryptographicOperations.ZeroMemory`
  in `finally`. `ArrayPool.Return(clearArray: true)` is not enough, because it is not guaranteed constant time and is easy to forget.
- `IBufferWriter<byte>` overloads exist where outputs are appended (canonical writer, sealing into a row buffer). The core
  `ArrayPoolBufferWriter` is reused.

Budget: `SealedValue.Seal/Open`, `MessageAuthentication.ComputeTag/VerifyTag`, `Hashing.Sha256` and `HashChain.Next` with cached keys
allocate **0 bytes** (11 §1).

## 2. Modern language and BCL features used (and where)

| Feature | Where | Why |
|---|---|---|
| `[InlineArray(32)]` | `Hash256` storage | 32 bytes inline, no array, `AsSpan()` without `unsafe` |
| `[UnscopedRef]` | `Hash256.AsSpan()`, `KeyId` views | safe span over `this` of a readonly struct |
| `params ReadOnlySpan<string>` (C# 13) | `SealContext.Write(buffer, domain, "farma", key)` | no array allocation for context parts |
| static abstract interface members | `ICanonicalValue<TSelf>` | value objects encode themselves with no reflection and no boxing; `MaxCanonicalLength` sizes the `stackalloc` |
| `IBinaryInteger<T>` / `IUnsignedNumber<T>` | `CanonicalWriter.WriteUnsigned<T>` | one varint routine for `byte`…`ulong`/`UInt128` |
| `IUtf8SpanFormattable`, `ISpanFormattable`, `ISpanParsable<T>` | `Hash256`, `KeyId` | hex directly into UTF-8 log buffers and JSON writers, no string |
| `Convert.ToHexStringLower`, `Base64Url` (`System.Buffers.Text`) | formatting, `MessageToken` | span-based, no `Replace('+','-')` tricks |
| `Guid.CreateVersion7()`, `Guid.TryWriteBytes(span, bigEndian: true)` | `KeyId` | sortable ids, RFC 9562 byte order |
| `GC.AllocateArray<byte>(n, pinned: true)` | `SecretKey` | pinned object heap: zeroing really erases |
| `TimeProvider` | rotation, validity, token expiry, certificate issuing | testable with `FakeTimeProvider`; Offline injects `TrustedTimeProvider` |
| `X509CertificateLoader`, `Pkcs12ExportPbeParameters` | certificates | replaces the obsolete constructors and the RC2/3DES PKCS#12 defaults |
| `CertificateRequest.LoadSigningRequest`, `X509SignatureGenerator.CreateForECDsa` | `CertificateAuthority` | CSRs and signing with non-exportable (TPM) keys |
| `MLKem`, `MLDsa`, `CompositeMLDsa` (`IsSupported`) | capability probes and reserved ids (08) | post-quantum readiness without committing early |
| `[SupportedOSPlatform("windows")]` | `.Windows` package | CA1416 analysis instead of runtime surprises |
| `allows ref struct` anti-constraint | `CanonicalWriter.Write<T>` helpers | lets ref-struct writers flow through generic helpers |

## 3. Results: unions for expected outcomes, exceptions for mistakes (⚑K9)

| Situation | Shape | Example |
|---|---|---|
| expected outcome with data, on a hot path | `[Union]` tagged struct (the `YFex.Cqrs/Result.cs` pattern: `byte _tag`, typed `TryGetValue` overloads, `Value` boxes only when used) | `OpenResult` |
| expected outcome without data | `enum : byte` | `SignatureCheck`, `TokenCheck`, `PasswordCheck` |
| expected outcome with data, cold path | native `union` (csharp-union-usage) | `CertificateCheck` |
| programmer error / misconfiguration | exception, at the first call or at startup | wrong key purpose, destination too small, missing root provider, ring configured for two purposes |

Never: `null` for "failed" (C10), `CryptographicException` for a wrong tag, `PlatformNotSupportedException` from deep inside a store.

```csharp
namespace YFex.Cryptography;

public readonly record struct Opened(int Written, KeyId Key, bool NeedsReseal);
public readonly record struct KeyUnknown(KeyId Key);
public readonly record struct KeyDestroyed(KeyId Key);
public readonly record struct AuthenticationFailed(KeyId Key);
public readonly record struct UnsupportedFormat(byte FormatVersion);
public readonly record struct AlgorithmUnavailable(SealingAlgorithm Algorithm);

public enum OpenOutcome : byte { Opened = 1, KeyUnknown, KeyDestroyed, AuthenticationFailed, UnsupportedFormat, AlgorithmUnavailable }

[Union]
public readonly struct OpenResult : IUnion
{
    private readonly KeyId _key;            // Opened, KeyUnknown, KeyDestroyed, AuthenticationFailed
    private readonly int _written;          // Opened
    private readonly byte _detail;          // NeedsReseal (Opened) · format version · algorithm
    private readonly byte _tag;             // 0 = none (default struct), else (byte)OpenOutcome

    public OpenOutcome Outcome => (OpenOutcome)_tag;          // exhaustive switch without boxing
    public bool HasValue => _tag != 0;
    public bool IsOpened => _tag == (byte)OpenOutcome.Opened;
    public bool TryGetValue(out Opened value);
    public bool TryGetValue(out KeyUnknown value);
    public bool TryGetValue(out KeyDestroyed value);
    public bool TryGetValue(out AuthenticationFailed value);
    public bool TryGetValue(out UnsupportedFormat value);
    public bool TryGetValue(out AlgorithmUnavailable value);
    public object? Value { get; }                               // IUnion; boxes only when used
    public static implicit operator OpenResult(Opened value);   // … one per case
}
```

24 bytes, no allocation, no boxing. Since .NET 11 preview the BCL ships `System.Runtime.CompilerServices.UnionAttribute` and `IUnion`,
so the library declares no polyfill (a local copy is error CS0436 under TreatWarningsAsErrors). Phase 0 added one case not listed above:
`Malformed(int Length)` (too short, unknown algorithm byte).

## 4. AOT and trimming

- `<IsAotCompatible>true</IsAotCompatible>` in every library project. IL2xxx/IL3xxx warnings are errors. `YFex.Cryptography.AotSmoke`
  publishes native AOT and exercises every public entry point (11 §4).
- **Banned inside the library:** `CryptoConfig`, `HashAlgorithm.Create(string)`, `Aes.Create(string)` and every name-based factory
  (reflection), `ActivatorUtilities` (C8), `SignedXml`, `BinaryFormatter`, `System.Text.Json` for anything signed. DI registrations are
  factories (`services.AddSingleton<IRootKeyProvider>(static sp => …)`).
- No generic virtual dispatch over value types on hot paths. Algorithm dispatch is a `switch` on the format byte.

## 5. Platform availability and fallbacks

| Target | Sealing | Signing | Root key | Notes |
|---|---|---|---|---|
| Windows 10/11 x64 (POS, broker) | AES-GCM | ECDsa (CNG); ML-DSA only on Windows builds with CNG PQC (verified on build 26200) | CNG TPM → software KSP | primary target |
| Windows Server | AES-GCM | ECDsa | CNG / environment / KMS seam | |
| Linux server (containers) | AES-GCM (OpenSSL) | ECDsa; ML-DSA/ML-KEM with OpenSSL ≥ 3.5 | environment / file / KMS seam | |
| Android (MAUI handhelds, later) | AES-GCM (or ChaCha20 where `AesGcm.IsSupported` is false) | ECDsa | Android Keystore provider: later package | |
| Browser (Blazor WASM) | **none** (`PreferredSealing == null`) | none | none | `AlgorithmUnavailable` outcomes; storage encryption on WASM is a WebCrypto design of its own (⚑K18) |

`CryptographyCapabilities` (04 §12) computes all of this once and reports it at startup.

## 6. Thread safety

- The static one-shots (hash, HMAC, HKDF, PBKDF2) are thread-safe.
- `AesGcm`, `ChaCha20Poly1305` and `ECDsa` **instances** are not documented as thread-safe (C1). The library keeps them in a per-key
  `AeadInstancePool`. The fast path is one slot taken with `Interlocked.Exchange(ref slot, null)` and returned with
  `Interlocked.CompareExchange`. When the slot is empty (contention), a second instance is created and disposed after use. With no
  contention there is no allocation. Under contention it costs one key schedule (~400 ns, 01 §4) instead of a race.
- `SignatureVerifier` caches imported public keys the same way (a pool per key id).
- `KeyRing` is immutable, `KeyRingProvider` swaps snapshots atomically, and readers never lock.

## 7. Naming (api-naming-and-annotation-style)

No abbreviations. Algorithm names keep their official spelling or the .NET type name (`Aes256Gcm`, `EcdsaP256Sha256`, `MLKem768`,
`ChaCha20Poly1305`, `HmacSha256` in docs). Rejected names and what replaced them:

| Rejected | Used |
|---|---|
| `Crypto`, `CryptoUtils`, `YFex.Crypto` | `YFex.Cryptography` |
| `Enc`, `Encryptor`, `AesGcmEnvelope` | `SealedValue` (the BCL already owns "Protect" for DataProtection, and "Envelope" is Offline's word for a signed offline operation) |
| `Kdf`, `Hkdf` helper | `KeyDerivation` |
| `Mac`, `HmacHelper` | `MessageAuthentication`, `MessageToken` |
| `Sig`, `Signer` | `SignedPayload`, `ISigningKey`, `SignatureVerifier` |
| `Kek`, `Dek` in type names | `KeyPurpose.Wrapping` / `Sealing` (the text may say "key-encryption key") |
| `Pwd`, `PasswordHasher` | `PasswordHashing`, `PasswordCheck` |
| `Ecies` | `RecipientSealing` |
| `Aad` | `SealContext` |

No attributes on members: what a value is (secret, classified) is declared once on the type (`[Sensitive(DataSensitivity.Secret)]` on
`SecretKey`).

## 8. Observability without leaking

The library emits core probes (`YFex.Diagnostics.Operations`) for: ring loaded or rotated, root provider and protection level chosen,
legacy value read, authentication failure (with key id, never content), `StorageKeyMismatch` (once per store), capability report.
Key ids are logged in short form (`01927c4e…`). Key bytes, plaintexts and tags are never captured. `SecretKey` is classified `Secret`, so
logging's capture refuses it even if someone passes it to a log call.
