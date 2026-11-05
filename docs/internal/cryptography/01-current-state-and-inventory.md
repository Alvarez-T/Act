# 01 — Current state, inventory and defects

Searched 2026-10-03 across `C:\Dev\YFex\**` and `C:\Dev\YFex.Automation` for `AesGcm`, `ChaCha20Poly1305`, `HMACSHA*`, `ECDsa`,
`ECDiffieHellman`, `RSA`, `SHA*`, `MD5`, `HKDF`, `Rfc2898DeriveBytes`/`Pbkdf2`, `ProtectedData`, `IDataProtector`, `RandomNumberGenerator`,
`CryptographicOperations`, `CngKey`, `X509*`, `CertificateRequest`. Each hit was then read through the RoselYn MCP tools (line ranges
below are from those reads). Backups, `bin/obj`, worktrees, generated XSD types and third-party sources are excluded.

## 1. Inventory

Every use falls in one of three classes. The class decides what happens to it ([02](02-should-there-be-one-library.md) §4):

- **S — security cryptography:** protects against an adversary (confidentiality, integrity, authenticity, key handling). It moves to
  the library or is rebuilt on it.
- **P — protocol-mandated:** an external format dictates the algorithm (JA3 = MD5, ESP flash check = MD5, a payment provider's webhook
  HMAC format, Windows driver signing). It stays where the protocol lives.
- **N — non-adversarial hashing:** identity, cache keys, change detection. Nobody gains anything by forging it. It stays as is (or uses
  `StableHash64`).

| # | Where | What it does | Class | Notes |
|---|---|---|---|---|
| I1 | `YFex.Persistence/src/YFex.Persistence.Encryption/AesGcmValueProtector.cs` | AES-256-GCM, layout `0x01 ‖ nonce(12) ‖ ct ‖ tag(16)`, one cached `AesGcm`, **no associated data** | S | F2, C1, C10 |
| I2 | `…/Persistence.Encryption/DataProtectionKeyProvider.cs` | key = HKDF(randomized `IDataProtector.Protect(seed)`) | S | **F1** |
| I3 | `…/Persistence.Encryption/ProvidedKeyProvider.cs`, `IKeyProvider.cs`, `IValueProtector.cs` | caller-supplied 32-byte key; contracts | S | |
| I4 | `…/Persistence.Encryption/EncryptedKeyValueStore.cs` | `IKeyValueStore` decorator, value → `Protect`, `null` on any failure | S | C10 |
| I5 | `…/Persistence.Encryption/EncryptionServiceExtensions.cs` | `AddYFexStorageEncryption`, enum `EncryptionKeySource {OsKeyStore, Provided}`, `ActivatorUtilities.CreateInstance` | S | F12 (duplicate enum), C8 |
| I6 | `YFex.Messaging/src/YFex.Messaging.Rpc/YFexMessagingRpcClientOptions.cs:4-5,18-19` | enums `EncryptionMode {None, AesGcm}` + duplicate `EncryptionKeySource`, options `Encryption`, `EncryptionKeySource` | S | **C3** (never read) |
| I7 | `YFex.Messaging/src/YFex.Messaging.Rpc/EventChannel.cs:37-40,96,154-175` | audience tokens `"{scope}.{base64url HMAC-SHA256(scope)}"`, random per-process key | S | **C4** |
| I8 | `YFex.Rules/src/YFex.Rules/Approvals/OverrideTokens.cs` | `OverrideToken` canonical payload (`:29-44`), `HmacOverrideTokens` key ring by string key id (`:103-137`), `DeviceOverrideTokenIssuer` ECDsa P-256 (`:158-167`), `DeviceOverrideTokenVerifier` (`:174-208`), `IDeviceKeyStore` (`:146-150`) | S | C5, C6; moves to Authorization (⚑Z12) |
| I9 | `YFex.Rules/src/YFex.Rules/Engine/Evaluator.cs:738-774` | decision fingerprint = `StableHash64` (XXH64) over the outcome; the approval token binds to it | N used as S | **C5** |
| I10 | `YFex.Web/src/Web/YFex.Web.Data/Security/AesSecretProtector.cs` + `SecretProtectionOptions.cs` | AES-GCM `nonce ‖ ct ‖ tag` base64, new `AesGcm` per call; `TenantSalt` documented | S | **C2**, no AAD |
| I11 | `YFex.Web/src/Web/YFex.Web/Credentials/ISecretProtector.cs` | `string Protect(string)` / `Unprotect` contract | S | |
| I12 | `YFex.Web/src/Web/YFex.Web.Refit/Tokens/OAuthTokenSource.cs:78-80` | SHA-256 of a type name as a cache-key part | N | fine |
| I13 | `YFex.Profiling/YFex.Profiling/Telemetry/PrivacyScrubber.cs:40-43`, `Data/UrlCategorizer.cs:46`, `YFex.Profiling.Windows/WindowsHardwareFingerprint.cs:28` | `SHA256(value + salt)`, salt = config text `"yfex.telemetry"` | S (weak) | Privacy ⚑P11 replaces with keyed pseudonyms |
| I14 | `YFex.System/YFex.System.Windows/WindowsSystemInfoProvider.cs:49-52` | **unsalted** SHA-256 of MAC addresses | S (weak) | Privacy ⚑P11 |
| I15 | `C:\Dev\YFex.Automation/src/YFex.Automation.Core/Secrets/PortableSecretStore.cs:43`, `…Platform.Linux/LinuxSecretStore.cs:36-41` | secrets written as **plain text** files | S (missing) | **C7** |
| I16 | `C:\Dev\YFex.Automation/src/YFex.Automation.Platform.Windows/Secrets/DpapiSecretStore.cs:15,35,43-45` | DPAPI `ProtectedData` (package already used by Automation), fixed entropy | S | fine; root-key alternative in 06 §3 |
| I17 | `YFex.Payment/src/YFex.Payment/Providers/WebhookSecurity.cs:13-29` | HMAC-SHA256 hex, SHA-256 hex, constant-time string compare, Basic auth, thumbprints | P | stays; may call library helpers |
| I18 | `YFex.Payment/src/YFex.Payment/Internal/CreatePaymentValidator.cs:59-66` | idempotency fingerprint = SHA-256 of a `'|'`-joined string that contains the payer's CPF | N with personal data | **C9** |
| I19 | `YFex.Security/YFex.Security.Windows/Capture/Pcap/Ja3Calculator.cs:137` | MD5 (JA3 spec) | P | stays |
| I20 | `YFex.Security/…/FileSystem/FileScanner.cs:10-24` and a copy in `YFex/src/System/YFex.System.Windows.Security/FileSystem/FileScanner.cs:9-23` | SHA-256 file identity; Authenticode signer via obsolete `new X509Certificate2(X509Certificate)` | N / P | duplicate class (not a crypto issue); `X509CertificateLoader` when touched |
| I21 | `YFex.Devices/src/YFex.Devices/Firmware/Esp/EspRomUpdater.cs:436` | MD5 flash verification (ESP ROM protocol) | P | stays |
| I22 | `YFex.Devices/app/YFex.Devices.Manager/Modules/VirtualCom/SigningCertificates.cs`, `DriverPackages.cs` | self-signed code-signing certificate, PKCS#12, `X509Store` trust, `signtool` | P | stays (app tool); may use `CertificateAuthority` later |
| I23 | `YFex.Data/src/YFex.DataMining/Projects/ProjectSignature.cs:48-50` | SHA-256 of a schema description | N | fine |
| I24 | `YFex/src/YFex/Diagnostics/TraceId.cs:246-280` | non-crypto PRNG seeded from `RandomNumberGenerator` for trace ids | N | fine (trace ids are not secrets) |
| I25 | `YFex/src/YFex/Helpers/StableHash64.cs:8-11` | XXH64, documented "**Not** cryptographic" | N | the line between N and S, see C5 |
| I26 | `YFex.Logging/src/YFex.Logging.Format/Crc32C.cs` | CRC-32C frame checks in `.ylog` | N | fine |
| I27 | `YFex.Audit/src/YFex.Audit.Public/AuditValueEncoding.cs:227-231` (new, an Audit session started it on 2026-10-03) | `ValueTreatment.Digest` = SHA-256 + length of large values | N/S | alignment item A-1 below |
| I28 | `YFex.Axamly/YFex.Previewer.Protocol/Protocol/MetsysBson.cs:472-477` | MD5 host hash in vendored BSON ObjectId code | N | third-party, out of scope |

Not found anywhere: NF-e XML signatures (`SignedXml`), A1/A3 certificate handling in YFex.Fiscal, any ML-KEM/ML-DSA use, any
`ChaCha20Poly1305`, any password hashing (offline login is designed, not built). YFex.Privacy, YFex.Authorization, YFex.Jobs, YFex.Tenancy,
YFex.Logging (besides CRC) contain no cryptography today.

## 2. The same concept, defined several times

| Concept | Defined by | Shapes |
|---|---|---|
| Key identifier | Rules (`string KeyId`, "k2"), Privacy 05 (`KeyId(Guid, byte Version)`), Offline 05 (`string KeyId` in `BundleHeader`), Audit 06 (`string KeyId` in `NodeChainHead`, anchors) | 3 shapes, 4 owners |
| Authenticated encryption format | Persistence (`0x01`, no AAD), Web.Data (base64 `nonce‖ct‖tag`, no version byte), Privacy 05 (`0x02`, AAD `'|'`-joined) | 3 layouts |
| Key ring (current + previous) | Rules `HmacOverrideTokens`, Privacy 05 §5.3 `KeyRing`, Offline 05 §2.3 `IBundleSigner` ring, Audit 06 §10 signing keys | 4 |
| Root key / wrapping | Persistence `IKeyProvider`, Privacy `IRootKeyProvider`, Offline `CngRootKeyProvider` (proposed "for both designs") | 3 |
| Device signing key | Rules `IDeviceKeyStore` (raw `ECDsa`), Audit device key (DPAPI), Privacy device key, Offline `IInstallationKeyStore` (CNG/TPM) | 4 (Offline F11 already flags 3 identities) |
| SHA-256 value type | Audit `Hash256` (core, ⚑A31), Offline `Hash256 PayloadHash`, Audit code today uses raw `Span<byte>` | 1 planned, used by 3 |
| Canonical bytes to sign or hash | Rules (`int32` length + UTF-8, little endian), Audit `CanonicalWriter` (varint, NFC), Offline "canonical(Header)", Privacy `TryWriteCanonical` (planned on `IClassifiedData`, not in core yet) | 2 incompatible conventions |
| Domain separation | Offline (`"yfex.envelope.v1|"`), Audit (`"YFEX-AUDIT-NODE-v1"`), Privacy (`"yfex.pseudonym.v1|"`), Rules (none) | 3 spellings, 1 missing |
| Signature encoding | ECDsa P-256 IEEE P1363 64 bytes everywhere (good: consistent by accident) | 1 |

The four designs agree on the algorithms. They disagree on everything around them: ids, layouts, rings, separators, canonical rules.
That is where the bugs below come from.

## 3. Defects

### Known (offline 01)

- **F1 — storage key changes on every start.** `DataProtectionKeyProvider.cs:26-33`: `protector.Protect(...)` is randomized (fresh IV
  and MAC every call), and its output is used as HKDF input key material. Every process start derives a different AES key. Values
  written before a restart can no longer be opened. Because of C10 they read back as "missing", so **queued offline sales disappear
  silently** after a restart during an outage.
- **F2 — no associated data.** `AesGcmValueProtector.cs:56` and `:75` call `Encrypt/Decrypt` without AAD. A ciphertext copied from the key
  `outbox:42` to `outbox:43`, or from one scope partition to another, decrypts fine.
- **F12** (part) — `EncryptionKeySource` declared twice (`EncryptionServiceExtensions.cs:9-15`, `YFexMessagingRpcClientOptions.cs:5`).

### New (this inventory)

| # | Defect | Evidence | Impact | Fixed in |
|---|---|---|---|---|
| **C1** | One `AesGcm` instance is shared by every concurrent store call. .NET documents no instance thread safety for `AesGcm`. The OpenSSL implementation (Linux, Android) keeps one cipher context per instance. **PLAUSIBLE**: to confirm with a concurrency test on Linux in Phase 0 | `AesGcmValueProtector.cs:26,31-40` (lazy singleton), registered as singleton `EncryptionServiceExtensions.cs:43` | corrupted ciphertexts or spurious authentication failures under parallel writes (outbox + cache) on Linux/Android; on Windows CNG it appears to work, but nothing guarantees it | Phase 0 (`AeadInstancePool`, 07 §6) |
| **C2** | `SecretProtectionOptions.TenantSalt` promises "effective key = HKDF(key, salt) so each tenant can isolate its secrets". **No code reads it.** | `SecretProtectionOptions.cs:12-16`; `AesSecretProtector.cs:31` uses `options.Key` only; the only `TenantSalt` reference in the repo is its declaration | every tenant's stored API keys, passwords and refresh tokens share one key while the configuration says they don't. A secret copied between tenants' rows also decrypts (no AAD) | Phase 1 |
| **C3** | `YFexMessagingRpcClientOptions.Encryption` and `.EncryptionKeySource` are never read. | declarations at `:18-19`; no other reference in `YFex/src` | an app sets `Encryption = EncryptionMode.AesGcm`, and the outbox and client cache stay **plain text** on disk | Phase 0 (remove or wire, ⚑K15) |
| **C4** | Event-channel audience tokens never expire, carry no key id and no domain, and the default key is random per process. | `EventChannel.cs:37-40,96`; token = `scope + "." + HMAC(scope)` at `:154-175` | a token obtained once keeps receiving that scope's events for the life of the server process, even after the user lost access to that store or tenant. On several nodes, tokens fail unless the app remembers to share a key | Phase 1 |
| **C5** | Approval tokens bind to a **non-cryptographic** 64-bit XXH64 fingerprint, and the signed payload has **no domain separation**. | `Evaluator.cs:738-774` (`StableHash64.Create(0xDEC1510)`), `StableHash64.cs:11` ("Not cryptographic"), `OverrideTokens.cs:23-24,29-44` | no practical attack is claimed. XXH64 is simply not designed to resist inputs chosen by the person who wants a collision (the cashier controls the sale). The same device key is planned to sign envelopes and audit heads (offline 03 §2), so an undomained payload could in principle be replayed as another kind | Phase 2 together with ⚑Z12 (⚑K12) |
| **C6** | The device-token verifier creates and imports a new `ECDsa` per verification and accepts any curve in the stored SPKI. | `OverrideTokens.cs:191-195` | measured 513 µs per verify instead of 111 µs (§4). Without a P-256 check a weaker registered key is accepted | Phase 2 (`PublicKeyCache`) |
| **C7** | Linux and portable secret stores write secrets as plain text with the default file mode. | `LinuxSecretStore.cs:36-41` (`File.WriteAllText`), `PortableSecretStore.cs:43` | with the usual umask 022 the files are world-readable (0644) on Linux | Phase 1 |
| **C8** | `AddYFexStorageEncryption` resolves the inner store with `ActivatorUtilities.CreateInstance` and pulls `Microsoft.AspNetCore.DataProtection` into every POS client. | `EncryptionServiceExtensions.cs:61`, csproj `PackageReference` | reflection on an AOT path (trim warnings), and a server-oriented dependency on terminals, only to produce F1 | Phase 0 |
| **C9** | Payment idempotency fingerprint: `'|'`-joined text (ambiguous: an external reference containing `'|'` shifts fields) hashed with **unkeyed** SHA-256, including the payer's CPF. | `CreatePaymentValidator.cs:59-66` | if the fingerprint is stored or logged, it is a confirmable hash of a CPF combined with mostly known fields. Collisions by field shifting are possible | Phase 1 (canonical encoding; keyed if persisted) |
| **C10** | Authentication failure, unknown version and "never written" are the same `null`. | `AesGcmValueProtector.cs:64-65,78-81`; `EncryptedKeyValueStore.cs:22-27,35-36` | turns F1 into silent loss. Tampering is indistinguishable from absence. `ExistsAsync` says "no" for a tampered value | Phase 0 (`OpenResult`, 07 §3) |
| **C11** | Blazor WASM + `YFex.Persistence.IndexedDb` + the encryption decorator: AES-GCM is not available in the browser runtime. **PLAUSIBLE**: verify in Phase 0 | `AesGcm` use at `AesGcmValueProtector.cs:39` with no `IsSupported` check | `PlatformNotSupportedException` at the first stored value in a WASM app that enables storage encryption | Phase 0 (capability check, clear `AlgorithmUnavailable`) |

**Status update 2026-10-04.** This inventory was taken early on 2026-10-03. The offline session's Phase 0 (same day) then fixed
**F1, F2, C3, C10** in place: `IKeyWrapper` + `CngKeyWrapper` (TPM → software KSP) + `DataProtectionKeyWrapper` + `WrappedKeyProvider`
(random storage key generated once), layout `0x02` with AAD `"yfex.store|" + store + "|" + key`, `UnprotectStatus`, and the RPC
`Encryption` options removed. **Still open:** C1 (`AesGcmValueProtector` still caches one `AesGcm`), C2, C4–C9, C11, and C8's
`ActivatorUtilities` (`EncryptionServiceExtensions.cs:122`) and DataProtection package. Line numbers above refer to the files as
they were before that change.

### Alignment items (not defects)

- **A-1 — Audit digests.** `AuditValueEncoding.WriteDigest` stores an unkeyed SHA-256 of large values (I27). For public documents this is
  right (anyone can check "this is the XML we sent to SEFAZ"). For a large **personal** text, an unkeyed digest lets someone confirm a
  guess. Recommendation for the Audit session: keep `Digest` for non-personal content; personal content uses the Privacy treatment
  (`Encrypted`/`Pseudonymized`) as already reserved in `ValueTreatment`. Uses core `Hash256` once it exists.
- **A-2 — Privacy envelope AAD.** Privacy 05 §6.1 builds AAD as `"yfex.envelope.v2|" + tenant + "|" + owner + "|" + member`. A tenant or
  member name containing `'|'` makes two different locations produce the same AAD. The converged format uses length-prefixed context
  (05 §2).
- **A-3 — Rules approval keys** use string key ids ("k1", "k2"). The legacy token codec keeps them byte-identical (golden test). New key
  rings use `KeyId`.

## 4. Measured baseline (this machine)

Windows 11 build 26200, .NET 11.0.0-preview.4, Release, `Stopwatch` loop after warm-up. These are indicative numbers, not
BenchmarkDotNet results. The benchmark project re-measures them (11 §2).

| Operation | Time | Allocated |
|---|---:|---:|
| `AesGcm.Encrypt` 64 B, cached instance | 258 ns | 0 B |
| `new AesGcm` + `Encrypt` + `Dispose` 64 B (what Web.Data does) | 653 ns | 72 B |
| `ChaCha20Poly1305.Encrypt` 64 B | 304 ns | 0 B |
| `RandomNumberGenerator.Fill` 12 B | 72 ns | 0 B |
| `SHA256.HashData` 64 B / 1 KiB | 169 / 414 ns | 0 B |
| `SHA3_256.HashData` 1 KiB | 1 854 ns | 0 B |
| `HMACSHA256.HashData` 64 B | 264 ns | 0 B |
| `HKDF.DeriveKey` 32 B | 690 ns | 32 B |
| `SP800108HmacCounterKdf.DeriveBytes` 32 B | 594 ns | 64 B |
| ECDsa P-256 sign (P1363), software CNG | 125 µs | 256 B |
| ECDsa P-256 verify, cached key | 111 µs | 112 B |
| ECDsa create + import SPKI + verify (Rules today) | 513 µs | 952 B |
| ECIES seal core: ephemeral ECDH P-256 key + derive | 1.19 ms | 1 864 B |
| ML-DSA-65 sign / verify | 549 µs / 44 µs | 0 B |
| ML-KEM-768 encapsulate / decapsulate | 11 µs / 18 µs | 0 B |
| PBKDF2-SHA256, 600 000 iterations | 55 ms | — |
| **TPM** ECDsa P-256 sign (Platform Crypto Provider, persisted, non-exportable) | **31.8 ms** | — |
| **TPM** RSA-2048 OAEP-SHA256 wrap / unwrap 32 B | 2.6 ms / **75 ms** | — |

Two consequences shape the design. **TPM signatures cost ~32 ms**, so per-entry TPM signing is out: sign batch heads and envelopes, never
every audit entry. **TPM unwrap costs ~75 ms**, so root-key unwrap happens once per process start, never per value.

Platform availability measured on the same machine: AES-GCM, AES-CCM, ChaCha20-Poly1305, SHA-3, SHAKE, KMAC, `HMACSHA3_*`, ML-KEM and
ML-DSA are **supported and not experimental**. `CompositeMLDsa` is supported (`MLDsa65WithECDsaP256`) but marked
`[Experimental("SYSLIB5006")]`. `SlhDsa` is experimental **and not supported** on this Windows build. The `ed25519` curve throws
`PlatformNotSupportedException` (Ed25519 is not in the BCL). `ProtectedData` is not in the shared framework (it is a package).
