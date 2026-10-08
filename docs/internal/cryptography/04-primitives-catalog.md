# 04 — Catalog of primitives (one modern API each)

Every entry: the **default** algorithm, the **API** (normative unless marked `// (sketch)`), **availability**, and the alternatives
considered. Every API takes spans and writes into caller-provided destinations. Array-returning overloads exist only as convenience
wrappers for cold paths. Formats are in [05](05-formats-and-canonical-encoding.md), keys in [06](06-keys-rings-rotation-and-storage.md),
and API conventions (results, AOT, threads) in [07](07-modern-dotnet-and-api-style.md).

Summary of defaults (⚑K5):

| Need | Default | Also available (BCL, net11) | Not in the BCL |
|---|---|---|---|
| Authenticated encryption | **AES-256-GCM**, random 96-bit nonce | ChaCha20-Poly1305 (algorithm id reserved, used only when AES-GCM is unavailable), AES-CCM | AES-GCM-SIV, XChaCha20-Poly1305 |
| Key derivation | **HKDF-SHA256** | SP 800-108 HMAC counter KDF (`SP800108HmacCounterKdf`), KMAC | — |
| Password verifier | **PBKDF2-HMAC-SHA256, 600 000 iterations** | PBKDF2-SHA512 | Argon2id, scrypt, bcrypt (⚑K14) |
| MAC | **HMAC-SHA256** (full 32-byte tag; ≥ 16 bytes when truncated) | HMAC-SHA384/512, HMAC-SHA3, KMAC128/256 | — |
| Hash | **SHA-256** | SHA-384/512, SHA3-256/384/512, SHAKE128/256 | BLAKE2/3 |
| Signature | **ECDsa P-256, SHA-256, IEEE P1363 (64 bytes)** | P-384, RSA-PSS, **ML-DSA-44/65/87**, composite ML-DSA (experimental), SLH-DSA (experimental) | Ed25519, Ed448 |
| Key agreement / sealing to a recipient | **ECDH P-256 + HKDF-SHA256 + AES-256-GCM** (ECIES) | **ML-KEM-512/768/1024** | X25519 |
| Random | `RandomNumberGenerator` | `GetString`, `GetHexString`, `GetInt32`, `Shuffle` | — |
| Certificates | `CertificateRequest`, `X509Chain` custom roots, `X509CertificateLoader` | PKCS#12 export with PBES2/AES | CMS/PKCS#7, RFC 3161 (`System.Security.Cryptography.Pkcs` package), XMLDSig (`System.Security.Cryptography.Xml` package) |

## 1. Authenticated encryption: `SealedValue`

**Why AES-256-GCM:** hardware AES on every x64 POS and ARMv8 device, 258 ns for a 64-byte value (01 §4), the algorithm all four designs
already chose, and FIPS-approved. ChaCha20-Poly1305 is equally safe and constant-time in software. It measured 304 ns here. Its id is
reserved so that a device without AES instructions (old Android handhelds) can seal with it **without a format change**: the header
names the algorithm (05 §1).

**Not chosen:** AES-GCM-SIV (nonce-misuse resistant) is not in the BCL. An own implementation would be unreviewed cryptography.
Random 96-bit nonces are safe up to 2³² messages per key (NIST SP 800-38D). Rings rotate long before that (06 §5).

```csharp
namespace YFex.Cryptography;

public enum SealingAlgorithm : byte
{
    Aes256Gcm = 1,
    ChaCha20Poly1305 = 2,          // reserved: implemented, selected only when AesGcm.IsSupported is false
}

public static class SealedValue
{
    public const int Overhead = 47;                                         // header 31 + tag 16 (05 §1)
    public static int GetSealedLength(int plaintextLength) => plaintextLength + Overhead;

    /// Seals with ring.Current. The nonce is generated inside; callers never pass one.
    public static void Seal(KeyRing ring, ReadOnlySpan<byte> plaintext, SealContext context, Span<byte> destination,
                            byte keyClass = 0);
    public static byte[] Seal(KeyRing ring, ReadOnlySpan<byte> plaintext, SealContext context, byte keyClass = 0);

    /// Reads version, algorithm, key class and key id without a key (routing to the right ring or vault).
    public static bool TryReadHeader(ReadOnlySpan<byte> sealedValue, out SealedValueHeader header);

    public static OpenResult Open(KeyRing ring, ReadOnlySpan<byte> sealedValue, SealContext context, Span<byte> plaintext);
    public static OpenResult Open(SecretKey key, ReadOnlySpan<byte> sealedValue, SealContext context, Span<byte> plaintext);
}

public readonly record struct SealedValueHeader(byte FormatVersion, SealingAlgorithm Algorithm, byte KeyClass, KeyId Key);

/// The associated data: WHERE a value belongs. Length-prefixed canonical bytes, so ("ab","c") ≠ ("a","bc") (fixes privacy A-2).
public readonly ref struct SealContext
{
    public static SealContext Write(Span<byte> buffer, MessageDomain domain, params ReadOnlySpan<string> parts);
    public static int GetLength(MessageDomain domain, params ReadOnlySpan<string> parts);   // to size a stackalloc
    public ReadOnlySpan<byte> Bytes { get; }
}
```

`OpenResult` is a hot-path tagged union (07 §3): `Opened(int Written, KeyId Key, bool NeedsReseal)`, `KeyUnknown(KeyId)`,
`KeyDestroyed(KeyId)`, `AuthenticationFailed(KeyId)`, `UnsupportedFormat(byte Version)`, `AlgorithmUnavailable(SealingAlgorithm)`.
`NeedsReseal` is true when the key is not the ring's current key or the value was read from a legacy layout (09 §2), so callers
"re-seal on write" without a separate check.

## 2. Key derivation: `KeyDerivation`

**Why HKDF-SHA256:** one construction for every derivation, RFC 5869 test vectors, one-shot span APIs in the BCL, and Privacy already
uses it. HKDF-Expand alone is used when the input key is already uniformly random (a 32-byte ring key). Extract + Expand is used for
non-uniform input (ECDH shared secrets). SP 800-108 (`SP800108HmacCounterKdf`, 594 ns) is available. It would only become the default
if a customer demands the NIST-only list, and it can be added as a label variant without changing callers.

```csharp
public readonly record struct KeyDerivationLabel(string Value);           // "yfex.pseudonym.v1", "yfex.web.secrets.tenant.v1" (open set)

public static class KeyDerivation
{
    /// HKDF-Expand(SHA-256, PRK = master, info = label ‖ 0x00 ‖ context). master.Purpose must be Derivation.
    public static void DeriveKeyMaterial(SecretKey master, KeyDerivationLabel label, ReadOnlySpan<byte> context, Span<byte> destination);

    /// Same, returned as a SecretKey with a deterministic KeyId (first 16 bytes of HKDF(…, "id")), cached by the ring.
    public static SecretKey DeriveKey(SecretKey master, KeyDerivationLabel label, ReadOnlySpan<byte> context, KeyPurpose purpose);

    /// HKDF-Extract + Expand for non-uniform input (shared secrets). Zeroes nothing it does not own.
    public static void ExtractAndExpand(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, KeyDerivationLabel label,
                                        ReadOnlySpan<byte> context, Span<byte> destination);
}
```

> **As built (2026-10-04):** `DeriveKey(master, label, context, purpose)` binds the purpose into `info`, and a `params string` overload
> writes length-prefixed text context (`KeyDerivation.WriteContext`). Generic per-scope keys are `ScopedKeyRings` (06 §2, 10 §6):
> derived from a master ring by default, a dedicated ring per scope on demand, one merged `KeyRing` for callers.

**Deterministic derived key ids** matter for per-tenant keys derived from one master. A sealed value names the **derived** key, and the
reader can re-derive it from the master with the context that `SealContext` already contains. This fixes C2 without one stored key
per tenant (10 §6).

## 3. Message authentication: `MessageAuthentication` and `MessageToken`

**Why HMAC-SHA256:** 264 ns per 64-byte message, universal, and every existing token uses it. KMAC256 is in the BCL now, but it rests on
SHA-3, which is 4–5× slower here and not available on every target. Domain separation does **not** concatenate strings at call time. Each
`(key, domain)` pair gets a subkey `HKDF-Expand(key, "yfex.mac|" ‖ domain)`, cached on the `SecretKey`. The MAC then is a single
one-shot `HMACSHA256.HashData(subkey, message, tag)`, with zero allocations and a domain that cannot be forgotten.

```csharp
public readonly record struct MessageDomain(string Value)                   // "yfex.approval.v2", "yfex.event-audience.v1" (open set)
{
    public static readonly MessageDomain Raw;                               // NO separation: legacy/protocol formats only (architecture-tested allow-list)
}

public static class MessageAuthentication
{
    public const int TagLength = 32;
    public static void ComputeTag(SecretKey key, MessageDomain domain, ReadOnlySpan<byte> message, Span<byte> tag);   // tag.Length 16..32 (truncation)
    public static bool VerifyTag(SecretKey key, MessageDomain domain, ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag);  // fixed time
    public static bool VerifyTag(KeyRing ring, KeyId keyId, MessageDomain domain, ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag);
}

/// Compact bearer token for server-issued capabilities (event audiences, short-lived links). Base64url text.
/// Layout: version 0x01 ‖ key id (16) ‖ expires (unix seconds, 8) ‖ payload ‖ tag (16, truncated HMAC). Domain is not on the wire.
public static class MessageToken
{
    public static string Issue(KeyRing ring, MessageDomain domain, ReadOnlySpan<byte> payload, DateTimeOffset expiresAt);
    public static TokenCheck Read(KeyRing ring, MessageDomain domain, ReadOnlySpan<char> token, DateTimeOffset now,
                                  Span<byte> payload, out int written);
}

public enum TokenCheck : byte { Valid, Expired, UnknownKey, InvalidTag, Malformed }
```

> **As built (2026-10-04):** also `MessageAuthentication.MinimumTagLength` (16), `ComputeTag(…)` returning a 32-byte array,
> `MessageToken.Issue(…, Span<char>)`, `GetTokenLength`, `GetMaxPayloadLength`, `TryReadHeader`. The tag is checked before the expiry.

## 4. Hashing

Digests are `Hash256` values (core type, 03 §2). The library computes them; core does not (⚑K2):

```csharp
public static class Hashing
{
    public static Hash256 Sha256(ReadOnlySpan<byte> data);                                              // SHA256.HashData into the inline value
    public static ValueTask<Hash256> Sha256Async(Stream stream, CancellationToken cancellationToken);   // CryptographicOperations.HashDataAsync
}
```

Leaf contracts that may not reference the library (Rules, Audit.Public, Authorization.Public) call `SHA256.HashData`
directly into a `Hash256`. YFex.Audit.Public does this since 2026-10-04.

SHA-384/512 and SHA-3 are not wrapped: no current use needs them, and adding them later is an additive enum value (`HashAlgorithmId` in
a format) rather than an API change. **SHA-3 availability**: supported on this Windows 11 build. On other targets it depends on the OS
crypto library (`SHA3_256.IsSupported`). It is therefore never a default.

**The line with `StableHash64`** is in [02](02-should-there-be-one-library.md) §4.2.

## 5. Signatures: `ISigningKey`, `SignedPayload`, `SignatureVerifier`

**Why ECDsa P-256 / P1363:** every design and the existing device tokens use it. CNG, the TPM (Platform Crypto Provider) and OpenSSL all
support it. Signatures are 64 bytes. Software verify takes 111 µs, TPM signing 32 ms (01 §4). **Ed25519 is not in the BCL** (the probe
throws `PlatformNotSupportedException`) and would need a package, and TPM 2.0 chips generally do not implement it. ML-DSA is the
post-quantum path ([08](08-post-quantum-and-crypto-agility.md)).

```csharp
public enum KeyProtection : byte { Memory = 0, File = 1, OperatingSystem = 2, Hardware = 3 }   // 06 §3; Hardware = TPM / secure element; recorded at enrollment

/// A private key that may be non-exportable (TPM). The library hashes; the key signs the hash.
public interface ISigningKey : IDisposable
{
    KeyId Id { get; }
    SignatureAlgorithm Algorithm { get; }
    KeyProtection Protection { get; }
    int SignatureLength { get; }
    ReadOnlyMemory<byte> SubjectPublicKeyInfo { get; }
    void SignHash(ReadOnlySpan<byte> hash, Span<byte> signature);           // ECDsa.SignHash(…, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
}

public interface ISigningKeyStore                                            // InMemory + File (wrapped by the root key) here; Cng in .Windows
{
    ValueTask<ISigningKey> GetOrCreateAsync(string keyName, SignatureAlgorithm algorithm, CancellationToken cancellationToken);
    ValueTask<ISigningKey?> TryGetAsync(string keyName, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string keyName, CancellationToken cancellationToken);
}

public static class SignedPayload
{
    /// signature over SHA-256(domain ‖ 0x00 ‖ payload). Hash first, so a TPM signs 32 bytes and long payloads stream.
    public static DetachedSignature Sign(ISigningKey key, MessageDomain domain, ReadOnlySpan<byte> payload, Span<byte> signatureBuffer);
    public static SignatureCheck Verify(SignatureVerifier verifier, MessageDomain domain, ReadOnlySpan<byte> payload,
                                        in DetachedSignature signature, DateTimeOffset signedAt);
}

public enum SignatureCheck : byte { Valid, Invalid, UnknownKey, KeyRevoked, KeyNotValidAt, AlgorithmNotAllowed, AlgorithmUnavailable, Malformed }

/// Public keys by KeyId, imported once (fixes C6: 111 µs instead of 513 µs), curve pinned to the algorithm id.
/// allowedAlgorithms: per object kind (08 §4); default = { EcdsaP256Sha256 }.
public sealed class SignatureVerifier(IPublicKeyDirectory directory, ReadOnlySpan<SignatureAlgorithm> allowedAlgorithms = default,
                                      int capacity = 1024);

public interface IPublicKeyDirectory                                         // Offline: installation registry; Audit: signing keys; tests: in-memory
{
    bool TryFind(KeyId id, out PublicKeyRecord record);                      // synchronous: directories are cached snapshots
}

public readonly record struct PublicKeyRecord(KeyId Id, SignatureAlgorithm Algorithm, ReadOnlyMemory<byte> SubjectPublicKeyInfo,
                                              ValidityPeriod Validity, DateTimeOffset? RevokedAt);
```

> **As built (2026-10-04):** `SignedPayload.Sign(key, domain, payload)` returns a `DetachedSignature` (new 64-byte array) and
> `int Sign(key, domain, payload, Span<byte>)` writes into a caller buffer (a span cannot become the `ReadOnlyMemory` inside
> `DetachedSignature`). `SignedPayload.ComputeHash` exposes the signed hash. Rotating software keys are a **`SigningKeyRing`**
> (a ring of `KeyPurpose.Signing` keys that is its own `IPublicKeyDirectory`, 06 §5); `ISigningKeyStore` is for named
> non-exportable keys (`CngSigningKeyStore`). `PublicKeyRecord.KeyIdOf(spki)` numbers keys a store did not number itself.

`KeyNotValidAt` checks `signedAt` (a **trusted** time from the payload, e.g. an envelope's `TrustedInstant`) against the record's
`ValidityPeriod` and revocation. "Valid if signed before the revocation" (offline 03 §5) is then one comparison, not per-feature code.

## 6. Sealing to a recipient (key agreement): `RecipientSealing`

Used by Privacy's offline intake (05 §5.5): a terminal seals a customer's CPF for the **server** with the tenant's public intake key, so
the terminal can never read it back.

```
ephemeral = ECDH P-256 key pair (new per message)
shared    = ECDH(ephemeral.private, recipient.public)                       DeriveRawSecretAgreement
key‖nonce = HKDF-SHA256(IKM = shared, salt = ephemeral.public ‖ recipient key id, info = "yfex.recipient-seal.v1" ‖ 0x00 ‖ context) → 32 + 12 bytes
output    = header ‖ ephemeral.public (65, uncompressed) ‖ AES-256-GCM(key, nonce, plaintext, AAD = header ‖ context)
```

```csharp
public enum RecipientSealingAlgorithm : byte
{
    EcdhP256Aes256Gcm = 1,
    HybridMLKem768EcdhP256Aes256Gcm = 17,   // reserved (⚑K10): both shared secrets go into HKDF; secure if either holds
}

public readonly record struct RecipientPublicKey(KeyId Id, RecipientSealingAlgorithm Algorithm, ReadOnlyMemory<byte> PublicKey);

public interface IRecipientKey : IDisposable                                 // the private side, server only (vault / CNG)
{
    KeyId Id { get; }
    RecipientSealingAlgorithm Algorithm { get; }
    void DeriveSharedSecret(ReadOnlySpan<byte> ephemeralPublicKey, Span<byte> sharedSecret);
}

public static class RecipientSealing
{
    public static int GetSealedLength(RecipientSealingAlgorithm algorithm, int plaintextLength);   // + 99 bytes for algorithm 1
    public static void Seal(in RecipientPublicKey recipient, ReadOnlySpan<byte> plaintext, SealContext context, Span<byte> destination);
    public static OpenResult Open(IRecipientKey key, ReadOnlySpan<byte> sealedValue, SealContext context, Span<byte> plaintext);
}
```

Cost: 1.2 ms per seal on Windows (ephemeral key generation dominates). That is fine for "customer registered at the counter" (one per
minute at most) and wrong for anything per item. Deriving the nonce from the one-time key removes the nonce field and any nonce-reuse
risk.

> **As built (2026-10-05, Phase 2).** Same format and derivation as above (05 §3). API differences from the sketch:
> - `IRecipientKey.TryDeriveSharedSecret(…) : bool` (false for a point that is not on the curve: a tampered value is an outcome, not an
>   exception) and `IRecipientKey.PublicKey` (the half to publish).
> - Software private keys are **key rings** like signing keys: `KeyPurpose.KeyAgreement` (6) holds the 32-byte ECDH P-256 scalar, and
>   `RecipientKeyRing.For(ring)` / `rings.GetRecipientKeysAsync(name)` give `Current` (whose `PublicKey` goes into the manifest),
>   `PublicKeys` and `TryGetRecipientKey(id)`. Rotation, propagation, wrapping at rest and retirement come from the ring.
>   Key-agreement keys are never derived (`KeyDerivation.DeriveKey` refuses them).
> - Extra members: `Seal(…) : byte[]`, `Open(RecipientKeyRing, …)` (picks the key by id, `NeedsReseal` when not current, `KeyDestroyed`
>   after shredding), `TryReadHeader` → `RecipientSealedHeader(Algorithm, Key)` (route to the right tenant ring without a key),
>   `GetPlaintextLength`, `CryptographyCapabilities.IsSupported(RecipientSealingAlgorithm)`.
> - Points are checked against the curve equation by the library itself before import (identical on every platform; Windows CNG
>   reports a rejected point as `PlatformNotSupportedException`, which would otherwise escape).
> - The sealer keeps the last imported recipient key per thread (it seals to the same intake key every time): 1.17 ms per seal.
> - The reserved hybrid algorithm `0x11` throws `NotSupportedException` when sealing and opens as `Malformed` (Phase 5).
>
> ```csharp
> // server: one ring per tenant
> services.AddYFexCryptography(c => c.KeyRingsMatching("privacy/intake/*", ring => ring.For(KeyPurpose.KeyAgreement)
>     .RotateEvery(TimeSpan.FromDays(365)).PropagateFor(TimeSpan.FromMinutes(10))));
> RecipientKeyRing intake = await rings.GetRecipientKeysAsync(new KeyRingName($"privacy/intake/{tenant}"), ct);
> RecipientPublicKey publish = intake.Current.PublicKey;                       // → signed manifest (Offline bundles)
> // checkout
> byte[] sealedCpf = RecipientSealing.Seal(in manifest.IntakeKey, cpfBytes, context);   // 110 bytes for a CPF
> // server, at sync
> OpenResult opened = RecipientSealing.Open(intake, sealedCpf, context, plaintext);
> ```

## 7. Password verifiers: `PasswordHashing`

Offline login (offline 05 §5) and site verifier bundles.

```csharp
public static class PasswordHashing
{
    public const int DefaultIterations = 600_000;                            // OWASP 2023 figure for PBKDF2-HMAC-SHA256; 55 ms here
    public const int VerifierLength = 54;                                     // 05 §4
    public static void CreateVerifier(ReadOnlySpan<char> password, Span<byte> verifier, int iterations = DefaultIterations);
    public static PasswordCheck Verify(ReadOnlySpan<char> password, ReadOnlySpan<byte> verifier, int minimumIterations = DefaultIterations);
}

public enum PasswordCheck : byte { Match, MatchNeedsUpgrade, Mismatch, UnsupportedFormat }
```

> **As built (2026-10-04):** `MinimumIterations` = 10 000 (creation), `MaximumIterations` = 10 000 000 (verification refuses more as
> `UnsupportedFormat`); `CreateVerifier(password, iterations)` array overload.

- The password is normalized to NFC before UTF-8 (`"José"` typed on two keyboards must match). Buffers are zeroed in `finally`.
- `MatchNeedsUpgrade` is returned when the stored iteration count is below `minimumIterations`. The caller then re-creates the verifier
  while it holds the plain password, which is the only moment that is possible.
- **Short PINs** are not protected by any hash against offline guessing (10⁶ PINs × 55 ms = 15 hours on one core, minutes on a GPU
  farm). The policy stays in Offline (⚑O12: PINs only where the broker rate-limits).
- **Argon2id** (memory-hard, the modern recommendation) is **not in the BCL**. The format reserves an algorithm byte for it, and the
  package choice is ⚑K14 (not now).

## 8. Random numbers

No wrapper. Rules, checked by review and by the architecture test:

| Need | Use |
|---|---|
| keys, nonces, salts, tokens | `RandomNumberGenerator.Fill` / `GetBytes` (inside the library) |
| human codes (enrollment "7Q4K-M2XD", pairing) | `RandomNumberGenerator.GetString("0123456789ABCDEFGHJKMNPQRSTVWXYZ", 8)` (Crockford alphabet, no I/L/O/U) |
| numeric codes (SAS, OTP) | `RandomNumberGenerator.GetInt32(0, 1_000_000)` (no modulo bias) |
| identifiers | `Guid.CreateVersion7()`: **never** a secret (contains a timestamp); tokens use 128 random bits |
| sampling, jitter, trace ids | `Random.Shared` / existing `DiagnosticRandom` (not secrets) |

## 9. Constant time and zeroing

- Every comparison of a tag, digest, verifier or token uses `CryptographicOperations.FixedTimeEquals` (wrapped by `VerifyTag`,
  `Hash256.FixedTimeEquals`, `PasswordHashing.Verify`). `==` on `Hash256` exists for dictionaries and is documented as not constant time.
- Key material lives in `SecretKey` (pinned, zeroed on final release, 06 §1). Temporary secrets inside the library (shared secrets,
  derived subkeys, decrypted verifier bytes) are `stackalloc`ed and cleared with `CryptographicOperations.ZeroMemory` in `finally`.
- Plaintext returned to callers is the caller's buffer. The library documents that callers holding secrets clear it themselves, and
  `SealedValue.Open` clears the destination when authentication fails (no partially decrypted bytes leak out).

## 10. Certificates: `CertificateAuthority`, `CertificateTrust`

Generic X.509 mechanics for Offline's tenant CA, YFex.Devices remoting (⚑O6) and later Fiscal's A1/A3 certificate loading. The
**policy** (which SANs, roles, lifetimes) belongs to the caller.

```csharp
[Flags]
public enum CertificateUsage : byte { ClientAuthentication = 1, ServerAuthentication = 2, DigitalSignature = 4, CertificateAuthority = 8 }

public sealed record CertificateIssueRequest(
    X500DistinguishedName Subject,
    ReadOnlyMemory<byte> SubjectPublicKeyInfo,           // or from a PKCS#10 CSR: CertificateRequest.LoadSigningRequest
    ImmutableArray<Uri> UriNames,                         // e.g. urn:yfex:installation:… (meaning defined by Offline)
    CertificateUsage Usage,
    TimeSpan Lifetime,
    int? PathLength = null);

public sealed class CertificateAuthority(X509Certificate2 certificate, ISigningKey key)   // key may live in the TPM
{
    public static X509Certificate2 CreateRoot(ISigningKey key, X500DistinguishedName subject, TimeSpan lifetime, TimeProvider time);
    public X509Certificate2 Issue(CertificateIssueRequest request, TimeProvider time);   // random 128-bit serial, AKI/SKI, basic constraints
}

public sealed class CertificateTrust(IReadOnlyCollection<X509Certificate2> roots)          // X509ChainTrustMode.CustomRootTrust, OS store ignored
{
    public CertificateCheck Validate(X509Certificate2 certificate, X509Certificate2Collection? intermediates, DateTimeOffset at,
                                     Func<X509Certificate2, bool>? isRevoked = null);
}

// cold path (handshakes, enrollment): the native union is fine (csharp-union-usage)
public sealed record CertificateTrusted(X509ChainElementCollection Chain);
public sealed record CertificateUntrusted(X509ChainStatusFlags Status);
public sealed record CertificateExpired(DateTimeOffset NotAfter);
public sealed record CertificateRevoked(string Reason);
public readonly union CertificateCheck(CertificateTrusted, CertificateUntrusted, CertificateExpired, CertificateRevoked);
```

- Signing with a non-exportable key goes through `X509SignatureGenerator.CreateForECDsa` over the `ECDsa` the key store exposes (CNG
  keys included). The root CA private key never needs to be exported.
- Loading uses `X509CertificateLoader` (the `X509Certificate2` constructors are obsolete; I20 still uses one). PKCS#12 export uses
  `Pkcs12ExportPbeParameters` with PBES2/AES-256 and no legacy RC2/3DES.
> **As built (2026-10-05, Phase 3).** As above, with these differences:
> - `CertificateAuthority(certificate, ISigningKey)` signs through a custom `X509SignatureGenerator` over `ISigningKey.SignHash` (P1363
>   → DER), so ring keys **and** non-exportable CNG/TPM keys work. It checks that the key is the certificate's and that the certificate
>   is a CA. Issuers are ECDsa P-256 only.
> - `Issue` enforces the issuer's path length, refuses CA + TLS mixes, clamps the end to the issuer's end, starts 5 minutes in the past
>   (`Backdate`), and adds SKI/AKI, critical basic constraints and key usage, EKU from the usage, and URI SANs.
> - `CertificateIssueRequest.FromSigningRequest(pkcs10, …)` verifies the CSR's signature (proof of possession) and ignores its
>   subject and attributes.
> - `CertificateTrust.Validate(…, requiredUsage)` checks the TLS role through the chain's application policy.
> - `CertificateTrusted` carries copies of the chain (`ImmutableArray<X509Certificate2>`). `CertificateExpired(Subject, NotAfter)` and
>   `CertificateRevoked(Subject, SerialNumber)` say which certificate.
> - `CertificateFiles` provides `Load` (DER/PEM), `LoadPkcs12` (ephemeral key by default) and `ExportPkcs12` (PBES2/AES-256/SHA-256).
> - `.Windows`: `CngSigningKeyStore.CopyWithPrivateKey(certificate, keyName)` attaches a non-exportable key to its certificate for TLS.
>   Tested with mutual TLS 1.3 over `SslStream`.

- **2026-10-05 update:** XML signatures now live in the satellite package `YFex.Cryptography.Xml` (13). Fiscal keeps only the NF-e
  rules. The paragraph below is the original reasoning.
- **Not here:** XML signatures for NF-e (`SignedXml` needs the `System.Security.Cryptography.Xml` package and uses `CryptoConfig`
  reflection, which conflicts with trimming). Fiscal owns it (⚑K14). It uses this library only to load and validate the ICP-Brasil certificate.

## 11. Chains and Merkle trees: `HashChain`, `MerkleTree` (⚑K11)

```csharp
public static class HashChain
{
    public static Hash256 Genesis(MessageDomain domain, ReadOnlySpan<byte> chainKey, in Hash256 previousChainFinal);
    /// SHA-256(domain ‖ 0x00 ‖ previous ‖ index (8, LE) ‖ entry). Stack buffers; streaming for entries > 4 KiB.
    public static Hash256 Next(MessageDomain domain, in Hash256 previous, ulong index, ReadOnlySpan<byte> entry);
}

public static class MerkleTree                                               // RFC 6962 hashing
{
    public static Hash256 LeafHash(ReadOnlySpan<byte> entry);                // SHA-256(0x00 ‖ entry)
    public static Hash256 NodeHash(in Hash256 left, in Hash256 right);       // SHA-256(0x01 ‖ left ‖ right)
    public static Hash256 ComputeRoot(ReadOnlySpan<Hash256> leaves);
    public static int WriteInclusionProof(ReadOnlySpan<Hash256> leaves, int index, Span<Hash256> proof);
    public static bool VerifyInclusion(in Hash256 leaf, long index, long treeSize, ReadOnlySpan<Hash256> proof, in Hash256 root);
}
```

> **As built (2026-10-05, Phase 3):** exactly these members, plus `HashChain.Replay(domain, head, firstIndex, entries)` and
> `MerkleTree.GetInclusionProofLength` / `MaximumProofLength` (63). The formulas are in 05 §12. `MerkleTree.VerifyInclusion` is RFC 9162
> §2.1.3.2. **The tree size is authenticated only when it is signed together with the root**: a proof for leaf 0 of 3 leaves also
> "verifies" for size 4 against the 3-leaf root. Audit's day seal must therefore sign the entry count with the Merkle root.
> No consistency proofs (no consumer needs one yet).

Audit 06 §2 had `HashChain.Next(domain, previous, index, timestampTicks, payload)`. Here the timestamp is part of the canonical entry
bytes, because the generic chain should not know about time. Offline's installation sequence and the Privacy consent ledger use the same
two calls.

## 12. Capabilities

```csharp
public static class CryptographyCapabilities
{
    public static bool IsSupported(SealingAlgorithm algorithm);              // AesGcm.IsSupported, ChaCha20Poly1305.IsSupported
    public static bool IsSupported(SignatureAlgorithm algorithm);            // ECDsa always except browser; MLDsa.IsSupported …
    public static bool IsSupported(RecipientSealingAlgorithm algorithm);
    public static SealingAlgorithm? PreferredSealing { get; }                // AES-GCM, else ChaCha20-Poly1305, else null (browser)
}
```

Computed once at type initialization. Results are logged at startup through a core probe ("sealing: Aes256Gcm; signing: EcdsaP256Sha256,
MLDsa65; root key: Hardware"). An unsupported algorithm is an `AlgorithmUnavailable` outcome, never a `PlatformNotSupportedException`
from deep inside a store (fixes C11's symptom; WASM storage encryption itself is ⚑K18).
