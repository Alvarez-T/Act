# 14 — Persisted signing-key stores on Linux and macOS (`FileSigningKeyStore`, Apple, Linux TPM)

> **Status:** DESIGNED 2026-10-07, **nothing coded** (user: "for all this plan, I just want it designed, do not change anything in code
> yet"). The user approved the `FileSigningKeyStore` design as proposed (§2, including the `FileKeyRingStore` permission fix §3), the
> Apple store as a **later spike in `YFex.Cryptography.Apple`** (§5), and tests on Windows now with the Linux run later (§4).
> The Linux TPM 2.0 store is only noted (§6): a native dependency that needs the user's approval.
> **Requested by:** installations 09 §5 (YFex.Installations must run on Windows, Linux and macOS, user requirement 2026-10-07; it
> needs the named keys `"installation/transport"` and `"installation/evidence"`, ECDsa P-256, `KeyId = SHA-256(SPKI)[0..16)` on each OS).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`. §5 and §6 are sketches.

## 1. The gap

`ISigningKeyStore` (named private keys outside key rings, `Signing/SigningContracts.cs`) has one persisted implementation, and it is
Windows-only:

| Store | Package | OS | Persisted | Fits named installation keys |
|---|---|---|---|---|
| `CngSigningKeyStore` | `.Windows` | Windows | yes (CNG: TPM → software KSP) | yes |
| `InMemorySigningKeyStore` | `.Testing` | all | no | tests only |
| `SigningKeyRing` | library | all | yes (a key ring `For(KeyPurpose.Signing)`) | **no**: it rotates keys and shares them across server nodes. An installation key is one key per name, never shared, rotated by its owner (new certificate) |

After this design:

| OS | Best store | Fallback | `KeyProtection` |
|---|---|---|---|
| Windows | `CngSigningKeyStore` (built) | — | `Hardware` (TPM) / `OperatingSystem` |
| Linux | `FileSigningKeyStore` (§2) | — | `File` |
| Linux, later | TPM 2.0 through an OpenSSL provider (§6, needs approval) | `FileSigningKeyStore` | `Hardware` |
| macOS, signed app | `AppleSigningKeyStore` (§5, spike first): Secure Enclave → data-protection Keychain | `FileSigningKeyStore` | `Hardware` / `OperatingSystem` |
| macOS, unsigned build / launchd daemon | `FileSigningKeyStore` | — | `File` |

Which store an app gets is Installations' `UsePlatformKeyStore()` (installations 09 §1). This library only provides the stores.

## 2. `FileSigningKeyStore` (main library, BCL only, every OS)

### 2.1 API

```csharp
namespace YFex.Cryptography;

/// Named ECDsa P-256 signing keys in files: PKCS#8 sealed with this store's store key, which the configured root key wraps
/// (05 §7). For Linux and macOS until hardware stores exist (14 §1); on Windows prefer CngSigningKeyStore.
public sealed class FileSigningKeyStore : ISigningKeyStore, IDisposable
{
    public FileSigningKeyStore(string directory, IRootKeyProvider root);

    public string Directory { get; }

    // ISigningKeyStore
    public ValueTask<ISigningKey> GetOrCreateAsync(string keyName, SignatureAlgorithm algorithm = SignatureAlgorithm.EcdsaP256Sha256,
        CancellationToken cancellationToken = default);
    public ValueTask<ISigningKey?> TryGetAsync(string keyName, CancellationToken cancellationToken = default);
    public ValueTask DeleteAsync(string keyName, CancellationToken cancellationToken = default);

    /// certificate + the private key keyName, usable by SslStream on this OS (§2.5).
    /// InvalidOperationException: no such key. ArgumentException: the certificate was issued for another key.
    public ValueTask<X509Certificate2> CopyWithPrivateKeyAsync(X509Certificate2 certificate, string keyName,
        CancellationToken cancellationToken = default);

    public void Dispose();
}

/// A signing-key file is unreadable, foreign, or was modified. Never silently replaced by a new key.
public sealed class SigningKeyStoreException(string path, string reason, Exception? inner) : CryptographicException(…)
{
    public string Path { get; }
}
```

```csharp
// CryptographyBuilder (DependencyInjection): registers ISigningKeyStore as a singleton FileSigningKeyStore over the configured root key.
public CryptographyBuilder StoreSigningKeysInFiles(string directory);
```

Usage (a Linux terminal or central container):

```csharp
services.AddYFexCryptography(cryptography => cryptography
    .UseFileRootKey(Path.Combine(dataDirectory, "root.key"))          // 0600
    .StoreSigningKeysInFiles(Path.Combine(dataDirectory, "signing")));

ISigningKey evidence = await store.GetOrCreateAsync("installation/evidence");
DetachedSignature signature = SignedPayload.Sign(evidence, OfflineDomains.Envelope, payload);
```

- **Async `CopyWithPrivateKeyAsync`** (unlike `CngSigningKeyStore.CopyWithPrivateKey`): the first call per process may unwrap the store
  key through the root provider, which is asynchronous (`IRootKeyProvider.UnwrapAsync`).
- **Algorithms:** `EcdsaP256Sha256` only (the core `SignatureAlgorithm` today). Other values throw `NotSupportedException`, as in
  `CngSigningKeyStore`. The file records the algorithm, so ML-DSA (Phase 5, 08) can be added without a new file version.
- **Key names:** non-empty, at most 200 UTF-16 characters, case-sensitive (`"installation/transport"` ≠ `"Installation/Transport"`), like
  key-ring names.
- **Instances:** every `GetOrCreateAsync`/`TryGetAsync` returns a new `ISigningKey` that the caller disposes (as `CngSigningKeyStore`
  does). Opening is one AES-GCM open + one PKCS#8 import (tens of µs). The store key is unwrapped once per store instance.
- **No rotation inside the store.** Installations rotates the transport key by creating a new key and certificate and deleting the old
  name when the new certificate is active (installations 04). The store offers `DeleteAsync`; it never deletes on its own.

### 2.2 Directory and files

```
{directory}/                        mode 0700 on Unix (created by the store)
    store.key                       mode 0600 — store key wrapped by the root provider (same format as FileKeyRingStore, 05 §7)
    {readable}-{hash8}.signingkey   mode 0600 — one file per key name
```

- `{readable}` = the key name with every character other than ASCII letters, digits, `.` and `-` replaced by `_`; `{hash8}` = first 8 hex
  characters of SHA-256 of the exact UTF-8 name, so `"a/b"` and `"a_b"` never share a file (same rule as `.keyring` files).
- `store.key` is shared code with `FileKeyRingStore` (§3): an internal `StoreKeyFile` reads, creates (atomically, first writer wins) and
  unwraps it. A signing store and a ring store **may** share one directory and one store key; the different sealing contexts (§2.3)
  keep a ring key from opening as a signing key and the other way round. Recommended: a separate `signing` subdirectory.

### 2.3 File format (normative; also in 05 §7)

Canonical encoding (05 §10):

```
version 0x01 · key name (text) · algorithm (SignatureAlgorithm byte) · key id (16 bytes) · sealed private key (bytes)

sealed private key = sealed value v3 (05 §1) under the store key,
                     context "yfex.signing-key-wrap.v1" [key name, key id, algorithm]
plaintext          = PKCS#8 PrivateKeyInfo (RFC 5208) as ECDsa.ExportPkcs8PrivateKey writes it (unencrypted inside the seal)
```

- **No file tag** (unlike `.keyring` files): the file holds one key and no status to flip. Every header field is in the seal's context,
  so a renamed, copied or edited file fails to open.
- **Load checks, in order:** version → the name equals the requested name → the seal opens (AES-GCM tag) → the PKCS#8 imports as a
  P-256 key (`EcdsaP256.IsP256`) → `PublicKeyRecord.KeyIdOf(SPKI)` equals the header key id. Any failure →
  `SigningKeyStoreException` (never "create a new key instead": a lost identity must be visible and re-enrolled, not replaced in silence).
- The PKCS#8 buffer is pinned and zeroed after import, as ring-key material is.

### 2.4 Writes, concurrency and permissions

- **Create:** generate the key → seal → write a temporary file in the same directory (Unix `UnixCreateMode` 0600, `WriteThrough`,
  `Flush(flushToDisk: true)`) → `File.Move(temporary, path, overwrite: false)`. If the target already exists (another process created
  the same name at the same moment), delete the temporary file and **load the existing key**, so two processes always agree on one key.
- **In-process:** one `SemaphoreSlim` per store serializes create/delete; reads need no lock.
- **Delete:** `File.Delete`. A process that already holds the key keeps signing with its in-memory copy until it disposes it.
- **Directory fsync:** .NET has no API to fsync a directory, so a power loss right after the rename can lose a just-created key on some
  file systems. The next start then creates a new key, and enrollment notices the different key id. Accepted; installations 09 R5 also
  requires a local (not network) state directory.
- **Permissions:** Unix: directory 0700, files 0600 (owner = the process user). Windows: the files inherit the directory ACL (the user
  profile or `%PROGRAMDATA%\…` with Installations' ACL, installations 09 §2). The store never widens permissions.

### 2.5 Protection and TLS

**`Protection = KeyProtection.File`** for every key of this store, whatever the root provider. The private key is a software key that
is decrypted into process memory on every load. With `FileRootKeyProvider` (the Linux/macOS default) a copy of the disk is enough to
read it. Central policy (⚑O5, installations 04) can refuse `File` keys for brokers.

`CopyWithPrivateKeyAsync(certificate, keyName)` first checks that the certificate's SubjectPublicKeyInfo equals the key's
(`ArgumentException` otherwise), then:

| OS | How | Verified |
|---|---|---|
| Linux | `certificate.CopyWithPrivateKey(ecdsa)` (OpenSSL accepts the in-memory key) | expected; **pending the Linux run** |
| macOS | same call; .NET puts the key in a temporary keychain for SecureTransport | expected; verify in the Apple spike (§5.4) |
| Windows | **PKCS#12 round trip**: export the attached certificate to PKCS#12 in memory, `X509CertificateLoader.LoadPkcs12(…, DefaultKeySet)`, zero the PKCS#12 bytes. Schannel refuses ephemeral keys | **probed 2026-10-07** (C:\Temp\fsksprobe, .NET 11 preview 4, Windows 11): mutual TLS 1.3 with `CopyWithPrivateKey` alone → handshake fails (unexpected EOF); after the PKCS#12 round trip → `Tls13`, client certificate received |

On Windows the round trip briefly puts the key in the user's software key container, which is deleted when the returned certificate is
disposed. If the process crashes first, the container stays behind. Windows apps should use `CngSigningKeyStore`; this path exists for
development machines and tests.

### 2.6 Tests and acceptance (when built)

| Test | Where |
|---|---|
| create → sign → verify with `SignatureVerifier`; `Id == KeyIdOf(SubjectPublicKeyInfo)` | Tests |
| restart: a new store instance over the same directory returns the same `KeyId`, and old signatures still verify | Tests |
| `TryGetAsync` unknown → null; `DeleteAsync` → null; `GetOrCreateAsync` after delete → a new id | Tests |
| tamper: flipped byte in header / ciphertext / key id → `SigningKeyStoreException`; file copied to another name → refused; file from another store (other store key) → refused; `store.key` from another root provider → refused | Tests |
| concurrency: 8 parallel `GetOrCreateAsync` of one name on **two** store instances (simulated processes) → one `KeyId` | Tests |
| Unix: directory 0700, `store.key` and `.signingkey` 0600 (also for `FileKeyRingStore`, §3) | Tests, run only on Linux/macOS (**Linux run pending**: no Docker/WSL on the development PC) |
| `CopyWithPrivateKeyAsync`: wrong certificate → `ArgumentException`; mutual TLS 1.3 over loopback `SslStream` with two file-store keys and certificates from `CertificateAuthority` | Tests (Windows: round trip; Linux: direct) |
| AOT smoke: create, sign, reopen, verify | AotSmoke, **0 IL warnings** |
| architecture test unchanged and green (`ECDsa.Create` stays inside the library) | Tests |
| benchmark: open an existing key ≤ 200 µs (software sign as `SigningKeyRing`) | Benchmarks |

## 3. `FileKeyRingStore` permission fix (finding F-P1, 2026-10-07)

While reading the code for §2: `FileKeyRingStore` (`Keys/KeyRingStores.cs`) writes `store.key` with `File.WriteAllBytesAsync` and the
`.keyring` files with a `FileStream` **without `UnixCreateMode`**, and creates its directory without a mode. Under the default umask
they are 0644/0755, so any local user can read them. The contents are sealed (no key leaks without the root key), but this breaks the
platform rule (Unix 0600/0700, installations 09 §2) and lets other users see ring names, revisions and statuses.

Fix (with §2, same pass, approved): an internal helper shared by `FileRootKeyProvider`, `FileKeyRingStore` and `FileSigningKeyStore`:

```csharp
internal static class ProtectedFiles   // (sketch)
{
    static void CreateDirectory(string path);                       // Unix: Directory.CreateDirectory(path, UnixFileMode 0700)
    static ValueTask WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, bool overwrite, CancellationToken token);
                                                                     // temp 0600 + WriteThrough + Flush(true) + File.Move
    static void Tighten(string path);                                // Unix: File.SetUnixFileMode(path, 0600) if wider
}
```

Existing files are tightened when the store opens them (`store.key`) or rewrites them (`.keyring`). Bytes and formats do not change.

## 4. Test platforms

- **Windows:** all tests (this PC).
- **Linux:** the development PC has no Docker and no WSL (checked 2026-10-07). The Unix-mode and direct-`CopyWithPrivateKey` tests are
  recorded as **pending a Linux run** (a container with the .NET 11 SDK, `dotnet test` on the solution). User decision 2026-10-07:
  Windows now, Linux later.
- **macOS:** no machine; §5 is a spike for when one is available.

## 5. macOS Keychain / Secure Enclave store — evaluation (spike later, `YFex.Cryptography.Apple`)

### 5.1 Package

**`YFex.Cryptography.Apple`** in this repo (user 2026-10-07: "spike later in .Apple"):
- Security.framework is the same API on macOS and iOS (mobile is planned, installations README §9), so one package with
  `[SupportedOSPlatform("macos")]` (later also `"ios"`) instead of `.MacOS` + `.iOS`.
- Inside this repo the architecture test needs no allow-list entry (the scan skips `YFex.Cryptography/`); hosting it in
  `YFex.Installations.MacOS` would need one.
- Installations keeps `.MacOS` for its own OS integration (`IOPlatformUUID`, launchd notes) and references `.Apple` for keys.
- A Keychain-backed `IRootKeyProvider` (Phase 5, 12 §3) goes in the same package and shares its interop.

### 5.2 Design sketch

```csharp
[SupportedOSPlatform("macos")]
public sealed class AppleSigningKeyStore : ISigningKeyStore     // (sketch)
{
    public AppleSigningKeyStore(string applicationTagPrefix = "yfex.signing.", string? accessGroup = null, bool preferSecureEnclave = true);
    public static bool IsAvailable(string? accessGroup = null);  // probe: false without the entitlement (errSecMissingEntitlement)
    // ISigningKeyStore members; Protection = Hardware (Secure Enclave) or OperatingSystem (Keychain software key, not extractable)
}
```

| Operation | Security.framework call (P/Invoke) |
|---|---|
| create | `SecKeyCreateRandomKey` with `kSecAttrKeyType = kSecAttrKeyTypeECSECPrimeRandom`, `kSecAttrKeySizeInBits = 256`, `kSecAttrTokenID = kSecAttrTokenIDSecureEnclave` (when preferred and present), `kSecUseDataProtectionKeychain = true`, `kSecAttrAccessGroup`, private attributes `kSecAttrIsPermanent = true`, `kSecAttrApplicationTag = prefix + name`, `kSecAttrAccessControl = SecAccessControlCreateWithFlags(kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly, kSecAccessControlPrivateKeyUsage)` |
| open | `SecItemCopyMatching` (`kSecClassKey`, application tag, `kSecAttrKeyClassPrivate`, `kSecReturnRef`) |
| sign | `SecKeyCreateSignature(key, kSecKeyAlgorithmECDSASignatureDigestX962SHA256, hash)` → DER → IEEE P1363 (r ‖ s) with `System.Formats.Asn1` (in the BCL) |
| public key | `SecKeyCopyPublicKey` + `SecKeyCopyExternalRepresentation` (X9.63 point, 65 bytes) → SPKI (fixed P-256 prefix + point) → `KeyIdOf` |
| delete | `SecItemDelete` |

- **AOT-safe:** `[LibraryImport]` into `/System/Library/Frameworks/Security.framework/Security` and CoreFoundation; the `kSec…`
  constants are exported `CFStringRef` globals read through `NativeLibrary.GetExport`; `SafeHandle` for `CFTypeRef` (`CFRelease`). No
  Objective-C runtime, no reflection, blittable signatures only.
- **Requirements:** the Secure Enclave and the data-protection keychain need a **signed app with a `keychain-access-groups`
  entitlement** (Developer ID + provisioning profile). Unsigned and CLI builds get `errSecMissingEntitlement` (-34018) → `IsAvailable`
  is false → `UsePlatformKeyStore()` uses `FileSigningKeyStore`. The Secure Enclave exists on Apple silicon and on Intel Macs with a T2.
  P-256 only, which is our algorithm.
- **launchd daemons** running as root have no per-user data-protection keychain, so a macOS **broker** cannot use it. This matches
  installations 09 R4 (macOS brokers are not supported in v1).
- **Signing cost:** to measure in the spike (the Secure Enclave is expected to be in the TPM's range: tens of ms). Same rule as the TPM
  (06 §5): sign batches and heads.

### 5.3 TLS on macOS with .NET 11 (desk research 2026-10-07, not run)

- `SslStream` on macOS uses **SecureTransport by default: TLS 1.2 at most**.
- **.NET 10 added client-side TLS 1.3** through Network.framework, **opt-in**: AppContext switch
  `System.Net.Security.UseNetworkFramework` or `DOTNET_SYSTEM_NET_SECURITY_USENETWORKFRAMEWORK=1` (dotnet/runtime#117428; ".NET 10
  what's new — TLS 1.3 for macOS (client)").
- **Server side is not in .NET 11:** the Network.framework server path (dotnet/runtime#134260) is an open pull request, milestone
  12.0, experimental. It enforces `ClientCertificateRequired` inside Network.framework (a validation callback can no longer accept a
  client without a certificate) and cannot advertise a custom CA list (falls back to SecureTransport).
- **Consequence for installations ⚑I22 / 09 R1:** a Mac can **dial** a peer with TLS 1.3 (with the switch), but a Mac that **listens**
  (port 7443) negotiates TLS 1.2. The ⚑I22 fallback (TLS 1.2, ECDHE-ECDSA-AES-GCM only, mutual authentication, flagged) applies to
  macOS listeners. Macs are terminals (clients) in v1, so in practice: broker (Windows/Linux) listens with 1.3, the Mac dials with 1.3
  when Installations sets the switch.
- **Secure Enclave key in `SslStream`:** needs an `X509Certificate2` built from a `SecIdentityRef` (certificate added to the keychain,
  then `SecItemCopyMatching(kSecClassIdentity)`, then `new X509Certificate2(identityHandle)`). Not documented for either TLS path →
  **unverified**. Recommendation unchanged from installations 09 R2: **macOS transport key = `FileSigningKeyStore`** (rotated every 90
  days anyway), **Secure Enclave for the evidence key only**.

### 5.4 Spike steps (on a Mac, signed test app)

1. Signed test host with a `keychain-access-groups` entitlement; `IsAvailable` true; unsigned host → false (-34018).
2. Create/open/sign/delete; signatures verify with `SignatureVerifier`; `KeyIdOf` stable across restarts; measure the sign time.
3. `FileSigningKeyStore.CopyWithPrivateKeyAsync` with SecureTransport (server TLS 1.2) and Network.framework (client TLS 1.3).
4. Secure Enclave identity in `SslStream` (both paths) → decides whether 09 R2 can relax.
5. AOT publish of the test host: 0 warnings, runs.

## 6. Linux TPM 2.0 — note only (native dependency, needs the user's approval)

| Route | What we would write | Native dependency | TLS with the key | Verdict |
|---|---|---|---|---|
| **A. OpenSSL 3 provider** (`tpm2-openssl`) | no P/Invoke for signing: `SafeEvpPKeyHandle.OpenKeyFromProvider(…)` (BCL since .NET 9; a multi-provider overload with a property query was added later) → `ECDsaOpenSsl` → `ISigningKey`. Key creation: one P/Invoke into the libcrypto .NET already loads (`EVP_PKEY_Q_keygen` with `provider=tpm2`) or `tpm2_create` at install time. The TPM-wrapped key blob (useless elsewhere) is kept in our 0600 file | `tpm2-openssl` + `tpm2-tss` distro packages; `/dev/tpmrm0` (user in the `tss` group) | **yes, the same `ECDsaOpenSsl` works with `CopyWithPrivateKey` → `SslStream`**. The runtime has TLS tests with TPM keys (dotnet/runtime#113229). Known .NET 9 handshake failures with the `tpm2` provider (dotnet/runtime#111251) were caused by loading only `tpm2` without `default`, which the multi-provider overload (#113118) fixes. Avoid SHA-512 signature schemes (the TPM does not hash with SHA-512) | **recommended for `.Linux`**. It answers installations 09 R3 ("probably yes", to verify) |
| B. PKCS#11 (`tpm2-pkcs11` module) | a generic `Pkcs11SigningKeyStore` (`C_GenerateKeyPair` `CKM_EC_KEY_PAIR_GEN`, `C_Sign` `CKM_ECDSA` returns r ‖ s = P1363 directly); user PIN sealed under the root key | `tpm2-pkcs11` + its token database (`tpm2_ptool init` at install) | not with `SslStream` (OpenSSL would need the pkcs11 provider as well) | not for the TPM. The same store is the natural shape for **HSMs and ICP-Brasil A3 tokens** → consider it for open question 7 (13 §6), on every OS |
| C. `tpm2-tss` ESAPI directly | P/Invoke over a large API, persistent handle management | `tpm2-tss` | no | no: the most code for the least reach |

Spike when approved: swtpm in a container (or a Linux PC with a TPM), route A, sign + verify + mutual TLS 1.3, AOT publish.

## 7. What Installations gets (installations 09 §2, §4, §5)

| Request (installations 09 §5) | Answer |
|---|---|
| 1. `FileSigningKeyStore` | designed (§2), approved, not built; `KeyProtection.File`; `CopyWithPrivateKeyAsync` for `SslStream` on all three OSes |
| 2. macOS Keychain / Secure Enclave store | `YFex.Cryptography.Apple`, spike when a Mac is available (§5); v1 macOS = transport `File`, evidence Secure Enclave in signed apps, else `File` |
| 3. Linux TPM 2.0 | route A (§6), needs approval of the native dependency; v1 Linux = `File` |
| 4. Keychain `IRootKeyProvider` | still Phase 5, same `.Apple` package |
| R1 (TLS 1.3 on macOS) | answered by desk research (§5.3): client yes with an opt-in switch; listener TLS 1.2 in .NET 11 |
| R2 (Keychain key in `SslStream`) | unverified (§5.3); spike step 4 |
| R3 (Linux TPM key in TLS) | probably yes through route A (§6); to verify |
