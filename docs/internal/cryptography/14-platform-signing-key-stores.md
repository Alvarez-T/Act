# 14 — Persisted signing-key stores on Linux and macOS (`FileSigningKeyStore`, Apple, Linux TPM)

> **Status 2026-10-18:** §2 `FileSigningKeyStore` and §3 F-P1 **BUILT** (user go 2026-10-18; log in 12 §5). Linux run still pending
> (no Linux environment). **Later the same day (user: "create it and keep the testing pending on those platforms"):** §6 Linux TPM route A
> and a Secret Service root key **WRITTEN** in `YFex.Cryptography.Linux`, §5 Apple Secure Enclave/Keychain store and Keychain root key
> **WRITTEN** in `YFex.Cryptography.Apple` — compiled and AOT-analysed on Windows, **NOT RUN on Linux/macOS**, opt-in only. The
> verification checklist and scripts are §8.
> **Original status:** DESIGNED 2026-10-07, nothing coded (user: "for all this plan, I just want it designed, do not change anything in code
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
| `CopyWithPrivateKeyAsync`: wrong certificate → `ArgumentException`; mutual TLS 1.3 over loopback `SslStream` with two file-store keys and certificates from `CertificateAuthority` | Tests (Windows: round trip; Linux: direct) — **Windows: Tls13 ✅ 2026-10-18** |
| AOT smoke: create, sign, reopen, verify | AotSmoke, **0 IL warnings** |
| architecture test unchanged and green (`ECDsa.Create` stays inside the library) | Tests |
| benchmark: open an existing key ≤ 200 µs (software sign as `SigningKeyRing`) | Benchmarks — **145 µs ✅** with the per-store cache of the decrypted key (file still read and compared on every call). The first open per store instance costs ≈ 1.7 ms on Windows: CNG imports an ephemeral P-256 key in 400–550 µs (probe 2026-10-18) |

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

## 6. Linux TPM 2.0 — route A approved 2026-10-18 (native dependency, optional at run time; spike not started)

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

## 8. Pending platform verification (written 2026-10-18 without Linux or macOS)

Everything below compiles on Windows with the AOT/trim analyzers (0 warnings) and its OS-independent parts are tested there; **none of it
has run on its target OS**. Until a run passes, the stores stay **opt-in** (explicit builder calls; nothing picks them automatically) and
their XML docs say "not yet run". Run the script, send the log (`scripts/verify-*.log`) to the cryptography session, which fixes what fails
and then updates this section, 12 §5 and the memory file. Make the scripts executable first if needed (`chmod +x scripts/*.sh`) or run
them with `bash scripts/verify-linux.sh`.

### 8.1 Linux — `bash scripts/verify-linux.sh [--require-tpm] [--require-secret-service]`

Prerequisites: .NET 11 SDK; OpenSSL 3 (`libcrypto.so.3`); for the TPM the distro packages **`tpm2-openssl`** and the `tpm2-tss` device
TCTI, `/dev/tpmrm0` readable and writable (user in the `tss` group: `sudo usermod -aG tss $USER`, then log in again); for the Secret
Service a desktop session (D-Bus session bus, unlocked GNOME Keyring or KWallet) and `libsecret-1`. Without `--require-…` a missing
feature is reported as `[SKIP]`, which is **not** a verification.

| # | What | Code | Pass when |
|---|---|---|---|
| L1 | Unix modes of the file stores (0700 directories, 0600 files, older 0644 files narrowed) and direct `CopyWithPrivateKey` (14 §4) | `FileStoreTests` (Unix test), `CertificateKeyStoreTests` | `YFex.Cryptography.Tests` green |
| L2 | TPM key creation: `EVP_PKEY_CTX_new_from_name(EC, provider=tpm2)` → `EVP_PKEY_generate` → `OSSL_ENCODER` PEM (`TSS2 PRIVATE KEY`) | `LinuxTpmSigningKeyStore.CreateKeyBlob` | `TPM_keys_sign_survive_a_restart_and_are_deleted` green; the log prints the creation time |
| L3 | Opening the blob with `SafeEvpPKeyHandle.OpenKeyFromProvider("tpm2", "object:<path>")` → `ECDsaOpenSsl`; sign, verify, reopen, delete; four concurrent creates agree on one key | same | the TPM tests green. **If opening fails**, the `object:` URI is the first suspect (tpm2-openssl store loader) |
| L4 | Mutual TLS 1.3 with TPM keys on both sides | `TPM_keys_attached_to_their_certificates_complete_mutual_TLS` | green. A handshake failure points at dotnet/runtime#111251 (only `tpm2` loaded without `default`, §6) |
| L5 | Secret Service root key: store, look up from a second instance, wrap a `FileKeyRingStore`, delete | `SecretServiceRootKeyProvider`, `Interop.Libsecret` (SecretSchema layout LP64) | `The_Secret_Service_root_key_persists…` green |
| L6 | Native AOT `linux-x64`/`linux-arm64`: publish with 0 IL warnings; the smoke runs, including its platform checks | `AotSmoke` | `VERIFY LINUX: PASSED`, no `[FAIL]` |
| L7 | ML-KEM on Linux (OpenSSL ≥ 3.5): the hybrid tests run instead of returning early | `HybridRecipientSealingTests` | with OpenSSL 3.5+, `CryptographyCapabilities.Describe()` says `ML-KEM: yes` in the smoke output |

### 8.2 Linux without a hardware TPM: swtpm

```bash
mkdir -p /tmp/swtpm && swtpm socket --tpm2 --tpmstate dir=/tmp/swtpm --server type=tcp,port=2321 --ctrl type=tcp,port=2322 --flags not-need-init,startup-clear --daemon
export TPM2OPENSSL_TCTI="swtpm:port=2321"
bash scripts/verify-linux.sh --require-tpm
```

`LinuxTpmSigningKeyStore.IsAvailable()` honours `TPM2OPENSSL_TCTI` (a TPM other than `/dev/tpmrm0`), so the TPM tests run against swtpm.
It proves the code path, not the protection of a real chip.

### 8.3 macOS — `bash scripts/verify-macos.sh [--require-keychain] [--require-secure-enclave]`

| # | What | Pass when |
|---|---|---|
| M1 | Unix modes of the file stores; direct `CopyWithPrivateKey` | `YFex.Cryptography.Tests` green |
| M2 | Keychain root key (login keychain): store, second instance reads it, delete. macOS may ask once to allow access: "Always Allow" | `The_Keychain_root_key_persists_and_wraps` green |
| M3 | Keychain software signing key (login keychain, `preferSecureEnclave: false`): create, sign, verify, reopen, delete | `Keychain_signing_keys_sign_reopen_and_are_deleted` green |
| M4 | Secure Enclave key (§5.4 steps 1–2): needs a **signed** test host with a `keychain-access-groups` entitlement (Developer ID + provisioning profile); an unsigned run reports `[SKIP]` (-34018). Record the signing time | `Secure_Enclave_keys_sign_reopen_and_are_deleted` green with `--require-secure-enclave` on the signed host |
| M5 | Native AOT `osx-arm64`/`osx-x64`: 0 IL warnings; the smoke runs | `VERIFY MACOS: PASSED` |
| M6 | TLS on macOS (§5.4 steps 3–4) | still a spike: not part of the script |

### 8.4 After a pass

The session that receives the log updates 12 §5, this section and the memory file, and removes "not yet run" from the XML docs. Whether
the automatic per-host choice should then include these stores (e.g. `UsePlatformKeyStore()` picking the TPM on Linux) is a decision for
the user (12 §4 question 1 style: automatic with manual overrides).

## 9. Android Keystore — `YFex.Cryptography.Android` (⚑K19, decided 2026-10-20; BUILT 2026-10-27, emulator-verified, real device pending)

**Requested by:** installations 11 (Android handhelds that work offline, ⚑I26, ⚑I32). The user chose a **variant package of this
library** ("a variant of the Crypto"), like `.Windows`, `.Apple` and `.Linux`.

### 9.1 Package

- `YFex.Cryptography.Android`, target `net11.0-android`. It is a .NET-for-Android project (Java interop with
  `java.security.KeyStore` "AndroidKeyStore"), which is why it cannot live in the BCL-only main package (12 Phase 5 note).
- No NuGet package beyond the Android workload. It is trim-clean (0 warnings under the Android build's trimming/AOT settings). NativeAOT for
  Android is evaluated in the spike, not assumed.
- Lives in this repo, so the architecture test needs no allow-list entry (as `.Apple`, §5.1).

### 9.2 Design sketch

```csharp
[SupportedOSPlatform("android")]
public sealed class AndroidKeystoreSigningKeyStore : ISigningKeyStore      // (sketch)
{
    public AndroidKeystoreSigningKeyStore(string aliasPrefix = "yfex.signing.", bool preferStrongBox = true);
    // P-256 (secp256r1), PURPOSE_SIGN, SHA-256, non-exportable. Order: StrongBox (setIsStrongBoxBacked) → TEE → software Keystore.
    // KeyProtection reported: Hardware for StrongBox and TEE (KeyInfo.getSecurityLevel), OperatingSystem for the software Keystore.
    // DER signatures from the platform are converted to P1363 (r ‖ s) like every other store.
}

[SupportedOSPlatform("android")]
public sealed class AndroidKeystoreRootKeyProvider : IRootKeyProvider      // (sketch)
{
    // AES-256-GCM key in the Keystore wraps the YFex root key file in the app's private files directory
    // (storage encryption, sealed installation state, refresh tokens).
}

[SupportedOSPlatform("android")]
public static class AndroidKeyAttestation                                 // (sketch) record only, never a decision here
{
    public static ImmutableArray<byte[]> ReadChain(string alias);        // certificate chain of the key, empty when unsupported
}
```

### 9.3 Rules

- **Hardware is preferred, never required** by this package (user, 2026-10-20). Requiring it is a policy at central (installations
  ⚑I32, `IEnrollmentPolicy`).
- Attestation chains are returned as bytes. Parsing and checking them against Google's roots is the policy's job (later; it needs a
  maintained root list).
- **Spike (installations 09 §4 R7), on a real device:** sign + verify, StrongBox and TEE signature time, Keystore key as `SslStream`
  client credential (fallback: software transport key under this root key), AOT/trim warnings, behaviour after a factory reset and after
  adding a screen lock (keys must not require user authentication, or background signing breaks).
  *Emulator results (2026-10-21, `C:\Dev\YFex\_ignore\androidKit-runs\`, Android 15 and 16, software KeyMint):* a Keystore P-256 key
  **works as the TLS 1.3 client credential** with `SslStream` (`new X509Certificate2(PrivateKeyEntry handle)`, dotnet/runtime#103337;
  mutual TLS, server saw the Keystore certificate), provided the key also allows `DIGEST_NONE` (Conscrypt signs the handshake hash with
  `NONEwithECDSA`). Keystore sign 3–9 ms median, key generation 37–53 ms, with attestation 130–160 ms; no StrongBox. Hardware numbers
  still need a real device (`androidKit probe --serial …`).

### 9.4 Decisions for the build (2026-10-27, user: "you can start it")

**Shape as built (replaces the 9.2 sketch where they differ):**

- `AndroidKeystoreSigningKeyStore : ICertificateKeyStore`. Alias `{prefix}{name}`. P-256, `PURPOSE_SIGN`, digests `NONE` + `SHA-256`,
  no user authentication, no unlocked-device requirement. `SignHash` signs the 32-byte hash with `NONEwithECDSA` (the library hashes,
  ⚑K8), so every key also works for TLS. Order StrongBox → TEE/software Keystore. `KeyProtection.Hardware` for StrongBox and TEE,
  `OperatingSystem` for software KeyMint.
- **TLS without touching the stored chain:** `CopyWithPrivateKeyAsync` builds an **in-memory** `KeyStore.PrivateKeyEntry` (the
  Keystore key + the issued certificate) and wraps it in an `X509Certificate2`. The certificate chain stored with the key stays the
  attestation chain.
- **Attestation:** the store's `GetOrCreateAsync(name, attestationChallenge)` overload sets the challenge at creation, and
  `ReadAttestationChain(name)` returns the DER chain (empty when none). This replaces the static `AndroidKeyAttestation` of 9.2,
  so callers never handle aliases. Record only (9.3).
- **Root key:** `AndroidKeystoreRootKeyProvider : IRootKeyProvider`. A non-exportable AES-256-GCM Keystore key (alias
  `yfex.root-key.{name}`) wraps each blob itself, the way the CNG root key does. The YFex root key is never in a file. Provider id
  `RootKeyFormat.AndroidKeystore = 0x08` (05 §7). Protection comes from the key's security level.

**Gap 1: Auto Backup and device-to-device transfer.** Keystore keys are never backed up, but app files are. Restoring sealed state onto
another device gives files whose wrapping key does not exist there. The rule: **key material and sealed installation state live in
`Context.NoBackupFilesDir`**, which Auto Backup and device transfer always skip. The package gives `AndroidKeyPaths.Directory("keys")`
(a folder under `NoBackupFilesDir/yfex/`) for `StoreKeyRingsInFiles` / `StoreSecretsInFiles`. Apps that also back up other files must
not list these folders in their `dataExtractionRules`/`fullBackupContent`.

**Gap 2: lost or invalidated keys.** Keys are not bound to user authentication or biometrics, so `KeyPermanentlyInvalidatedException`
does not apply. They disappear on uninstall, "clear data", factory reset, or a restore onto another device. The root provider then
throws `AndroidKeystoreKeyLostException` (a `CryptographicException`), whose message says "the Keystore key is gone; re-enroll", when
blobs exist but its key does not, or when the key cannot open them. A missing signing key is simply absent (`TryGetAsync` → null).
Recovery is the app's and Installations' job: wipe local state and enroll again with a new QR (installations 11 §4.3).

**Gap 3: runtime.** The package makes no runtime assumption. It is trim-clean (`IsTrimmable`, analyzers on, 0 warnings) and uses only
the generated `Mono.Android` bindings, no reflection. v1 tests run on the default .NET for Android runtime. NativeAOT for Android is
evaluated with the handheld app (installations I6), not in this package.

### 9.5 Verification (2026-10-27)

`tests/YFex.Cryptography.Android.DeviceTests` is an Android app whose instrumentation runs 11 tests and reports JSON. Run it with
`androidKit test tests/YFex.Cryptography.Android.DeviceTests/YFex.Cryptography.Android.DeviceTests.csproj [--atd | --serial <id>]`.
Every alias and file carries a per-run suffix and is deleted at the end.

| Test | Proves |
|---|---|
| create / reopen / sign / verify / delete | Keystore `NONEwithECDSA` over the hash → P1363 verifies with `SignatureVerifier`; same id on reopen; tamper → `Invalid`; delete is idempotent |
| concurrent creation | 4 parallel `GetOrCreateAsync` → one key |
| attestation | with a challenge: chain ≥ 2, leaf = the key; without: empty; missing key: empty |
| mutual TLS | a Keystore CA key signs a root and a leaf (`CertificateAuthority`); `CopyWithPrivateKeyAsync` → `SslStream` TLS 1.3 client credential; server sees the issued certificate; **the stored attestation chain is unchanged** |
| mismatch | a certificate for another key → `ArgumentException`; missing key → `InvalidOperationException` |
| root key | blob `0x08 ‖ 12 ‖ ct ‖ 16`; fresh nonce per wrap; second instance unwraps; tampered / other id / truncated → `CryptographicException` (not "lost") |
| lost root key | key deleted, provider recreated, old blob → `AndroidKeystoreKeyLostException` |
| restart | `FileKeyRingStore` under the Keystore root in `AndroidKeyPaths`: same ring key and the sealed value opens after a new root provider + store + ring provider |
| builder | `UseAndroidKeystoreRootKey` + `StoreSigningKeysInAndroidKeystore` + `StoreSecretsInFiles` resolve; secret round trip |
| key folders | `AndroidKeyPaths.Directory` is under `NoBackupFilesDir/yfex/`; a path is refused |

| Run | Result | Facts |
|---|---|---|
| Android 16 ATD emulator (API 36), run `20261027-153003-emulator-5586` | **11/11 pass** | protection `OperatingSystem` (software KeyMint), sign median 4.0 ms, attestation root "Droid Unregistered Device CA" (test root), TLS 1.3 |
| Android 15 emulator (API 35), run `20261027-153157-emulator-5584` | **11/11 pass** | sign median 5.8 ms; first handshake 7.3 s (emulator warm-up, includes JIT) |
| Release build of `cryptography.android.slnx` (trimming on) | 0 warnings | |
| Real handheld | **pending** | StrongBox/TEE protection and timing, Google attestation root, `NONE` digest with StrongBox: run with `--serial` when a device is connected |

Real-device question to settle: some StrongBox implementations accept only `SHA-256` for EC keys. If StrongBox refuses `DIGEST_NONE`,
the store already falls back to the TEE (`StrongBoxUnavailableException`). A refusal surfacing as another exception type would need
a fix, so the first handheld run decides it.


## 10. Named recipient (key-agreement) keys — `IRecipientKeyStore` (BUILT 2026-10-27)

**Requested by:** installations I5 (08 §2 dependency table) and crypto 13 §6: each installation needs a third key, an ECDH P-256
**key-agreement key** next to its signing keys, so central can seal a secret (an A1 certificate) to that installation alone. Ring
recipient keys (`RecipientKeyRing`) are server-side and rotate; an installation's key is a single named key, like its signing keys.

```csharp
public interface IRecipientKeyStore                         // main library
{
    ValueTask<IRecipientKey> GetOrCreateAsync(string keyName, CancellationToken cancellationToken = default);
    ValueTask<IRecipientKey?> TryGetAsync(string keyName, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string keyName, CancellationToken cancellationToken = default);
}
// IRecipientKey gained KeyProtection Protection => KeyProtection.Memory (default member; stores override it).

services.AddYFexCryptography(cryptography => cryptography
    .UseCngRootKey()
    .StoreSigningKeysInCng()
    .StoreRecipientKeysInCng());                            // Windows: TPM first, else the software KSP
//  .StoreRecipientKeysInFiles(AppPaths.Data("recipient-keys"))   // every OS: sealed secrets under the root key
//  .StoreRecipientKeysIn(sp => …)                                 // custom store

IRecipientKey agreement = await recipientKeys.GetOrCreateAsync("installation/key-agreement");
// enrollment publishes agreement.PublicKey; central: RecipientSealing.Seal(publicKey, pfx, context)
OpenResult opened = RecipientSealing.Open(agreement, sealedA1, context, plaintext);
```

| Store | Where | Protection | Algorithm |
|---|---|---|---|
| `SealedRecipientKeyStore(ISecretStore, KeyProtection, PostQuantumKeys)` (main library) | any `ISecretStore`; `StoreRecipientKeysInFiles` builds it over a `FileSecretStore` | as given (`File` from the builder) | **hybrid `0x11`** when the host runs ML-KEM (`PostQuantumKeys.Automatic`), else classic |
| `CngRecipientKeyStore` (`.Windows`) | CNG key `YFex.KeyAgreement.{name}`, `ExportPolicy None`, user or machine scope | `Hardware` (TPM) / `OperatingSystem` (software KSP) | classic `0x01` (a TPM has no ML-KEM) |

- **Key id:** `PublicKeyRecord.KeyIdOf(SPKI of the P-256 public key)` in every store, as for signing keys.
- **Stored record** (`SealedRecipientKeyStore`, secret `recipient-key/{name}`, 05 §7): `0x01 ‖ P-256 scalar (32) [‖ ML-KEM-768 seed (64)]`.
- **TPM finding (probe 2026-10-27, `C:\Dev\YFex\_ignore\cng-ecdh-probe`):** the Platform Crypto Provider creates ECDH P-256 keys only
  without `NCRYPT_ALLOW_KEY_AGREEMENT_FLAG` (the default usage works), and `ECDiffieHellmanCng.DeriveRawSecretAgreement` cannot
  use the key: it imports the peer key with flags the PCP refuses ("Invalid flags" / "handle invalid"). `NCryptImportKey(flags 0)` →
  `NCryptSecretAgreement` → `NCryptDeriveKey("TRUNCATE")` works on the TPM and the software KSP. The secret comes back little-endian
  and is reversed. So `.Windows` calls NCrypt through `LibraryImport` (AOT-safe, `AllowUnsafeBlocks`). One TPM agreement takes about 47 ms;
  the first use of a reopened key takes about 1 s (key load).
- **Same rules as ring keys:** an off-curve or malformed ephemeral point → `false` (the library's `RecipientSealing.IsOnCurve`, shared
  with `.Windows` through `InternalsVisibleTo`), never an exception. Creation proves an agreement against a software peer before the
  key is trusted; a TPM that fails falls back to the software KSP.
- **Across processes:** CNG creation is by name (first creator wins). `SealedRecipientKeyStore` serializes creation within a process;
  across processes the last writer wins, so the installation's own setup creates the key.
- **Not yet:** Android (Keystore `PURPOSE_AGREE_KEY`, API 31+), Linux TPM and Apple stores. Until then those OSes use
  `StoreRecipientKeysInFiles`.

**Tests (2026-10-27):** cryptography 241 (+6 `RecipientKeyStoreTests`: classic and hybrid round trip after reopen, id from the
public key, concurrent creation, record version check, builder + restart with files); Windows 20 (+5 `CngRecipientKeyStoreTests`: software
KSP **and TPM** seal → reopen → open, tamper refused, id + no export, shared secret = software ECDH, off-curve/compressed point
refused, no ML-KEM half, builder); AOT smoke (win-x64) 0 IL warnings with both stores.
