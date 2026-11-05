# 12 — Phases, acceptance and implementation log

## 1. Working rules for implementing sessions

- `C:\Dev\YFex` is **not** a git repository. Back up every file you change to `C:\Temp\cryptography-backup\<phase>\` (same relative
  path) before editing.
- Other sessions work in parallel: Audit (`YFex.Audit` started 2026-10-03, `YFex.Audit.Public`), Authorization (phases in
  authorization-consolidation/09), Logging Phase 2, Privacy Tier 1 follow-ups. Before touching a shared file (core `YFex.csproj`,
  `Persistence.Encryption`, `Messaging.Rpc`, `Rules/Approvals`), check its modification time and the other design's implementation log.
- Build with `--artifacts-path C:\Temp\yfex-art` (the RoselYn MCP may lock generator DLLs in `bin/`). Build projects individually if
  `yfex.slnx` restore fails.
- Write multi-line edit scripts as Python files in `C:\Temp\yfex-edits\` with the Write tool (bash heredocs mangle backslashes).
- **No NuGet package** beyond those already approved (DI abstractions, BenchmarkDotNet in benchmarks, xunit in tests). Anything else is ⚑K14.
- After each phase: append to the implementation log (§5) with date, files, deviations, gotchas, test counts and benchmark numbers;
  update the memory file `yfex-cryptography.md`.

## 2. Dependencies on other designs

| This design needs | From | Status |
|---|---|---|
| `CanonicalWriter` spec (02 §9) | Audit ⚑A31 | the audit engine built its own entry codec (audit format, stays). The shared writer is built in the library (⚑K2/K11 confirmed 2026-10-04); the audit codec may adopt it later |
| `ValidityPeriod` | core (⚑Z13) | **done** (`YFex/src/YFex/ValidityPeriod.cs`) |
| `[Sensitive]`, `DataSensitivity` | core Privacy Tier 1 | **done** |
| approval token move | Authorization ⚑Z12 | **done (Authorization 4b, 2026-10-16)**: v2 tokens on `MessageAuthentication` (ring `approvals/server`) and `SignedPayload` (evidence key); v1 dropped by the user (Authorization A9), so the v1 golden and `MessageDomain.Raw` verification are not used |
| `TrustedTimeProvider` | Offline ⚑O10 | pending; the library takes any `TimeProvider` |

| Other designs need from this one | Their phase | This phase |
|---|---|---|
| Offline Phase 0 (F1, F2 fix, ⚑O22) | 0 | **0** |
| Offline installation keys, CA, bundles, envelopes, verifiers | 1–3 | 2–3 |
| Privacy Tier 2 (pseudonyms, vault, intake sealing) | Tier 2 / Phase 4 | 1–2 |
| Audit chains, anchors, Merkle proofs | 4–6 | 3 |
| Authorization approval tokens v2 | Z12 phase | 2 |
| Authentication C1 verify-only RSA, C5 imported P-256 key (⚑AN11) | AN-P3 | **done 2026-10-18** (§5) |
| Authentication C2 `OneTimePassword` TOTP/HOTP (⚑AN12) | AN-P5 | **done 2026-10-19** (§5, 04 §13) |

## 3. Phases

### Phase 0 — ✅ DONE 2026-10-04 — core value types, minimal library, storage encryption on the library (F1/F2/C3/C10 interim-fixed by offline Phase 0; C1, C8, C11 here)

Needs ⚑K1, K2, K4, K6, K7, K13, K15.

0.0 **Goldens first:** with the current code, generate `persistence-v1.hex` (L1), `web-secret-v1.txt` (L2) and `approval-token-v1.hex`
    (L3) as described in 09 §3.
0.1 Core (**types only**, ⚑K2): `Hash256` (✅ 2026-10-04), `KeyId`, `SignatureAlgorithm`, `DetachedSignature` (`YFex.Cryptography`
    namespace) and the `ICanonicalValue<TSelf>` contract (`YFex.Canonical`) + tests in `YFex.Tests`. The writer/reader come in 0.3.
0.2 Repo `C:\Dev\YFex\YFex.Cryptography` scaffold (03 §1), `Directory.Build.props`, `cryptography.slnx`.
0.3 Library: `CanonicalWriter`/`CanonicalReader`, `Hashing`, `SecretKey`/`KeyLease`, `KeyPurpose`, `KeyRing`/`KeyRingProvider` (rotation by time and count, two-phase, reload on unknown
    key), `IRootKeyProvider` + `Environment`/`Provided`/`File` providers, `FileKeyRingStore` (05 §7), `SealedValue` v3 + `SealContext` +
    `OpenResult`, `LegacyFormats.OpenPersistenceVersion1`/`OpenPersistenceVersion2`, `AeadInstancePool`, `CryptographyCapabilities`, `AddYFexCryptography`.
0.4 `.Windows`: `CngRootKeyProvider` (TPM → OAEP-SHA1 fallback → software KSP), `UseCngRootKey()`.
0.5 Persistence.Encryption moved from offline Phase 0's interim code onto the library (09 §1 update; keep the CNG key name and the
    wrapped storage key, read `0x01`/`0x02` as legacy): keyed inner registration, `StorageIntegrityException`, `TryGetAsync`, `LegacyKey`,
    DataProtection package removed, `yfex-storage report`. (Messaging.Rpc dead options: already removed by offline Phase 0.)
0.6 Tests: AES-GCM KATs, goldens, negative cases, restart (F1), AAD (F2), concurrency on Windows **and** Linux (C1; a Linux container if
    available, otherwise recorded as a gap), WASM capability check (C11: `PreferredSealing == null`), architecture test, AotSmoke,
    benchmark rows for sealing.

**Acceptance:** a value written, the host restarted and the value read back opens (software KSP and, where present, TPM). A value moved to
another key returns `AuthenticationFailed`. 16-thread seal/open passes on Windows and Linux. L1 golden values open and re-seal. Unreadable
legacy values raise `StorageIntegrityException`, and `StorageKeyMismatch` is logged once. `YFex.Persistence.Encryption` no longer
references `Microsoft.AspNetCore.DataProtection` and has no IL2026 warnings. AotSmoke 0 warnings. Seal/Open budgets met or logged as gaps.
Existing Persistence and Messaging tests stay green.

### Phase 1 — ✅ library + Web.Data (C2) + EventChannel (C4) DONE 2026-10-04; Payment (C9) written 2026-10-05, not wired (Payment rewrite); Automation (C7) deferred by the user; core `ICanonicalValue` on value objects pending — MACs, tokens, derivation, passwords + their consumers (C2, C4, C7, C9)

Needs ⚑K5, K16.

`MessageDomain`, `MessageAuthentication` (subkey cache), `MessageToken`, `KeyDerivation` (deterministic derived ids), `PasswordHashing`,
`Hashing`, recipient-free KATs (HKDF, HMAC, PBKDF2). Consumers: `EventChannel` audience tokens (I7), Web.Data `SealedSecretProtector`
+ `ISecretProtector` location overloads + L2 reader + derived per-tenant keys (I10, I11), Automation `SealedFileSecretStore` with plain-file
migration (I15), Payment fingerprint on canonical bytes (I18, Payment owner), `ICanonicalValue<T>` on `Cpf`, `Cnpj`, `Money`, `Quantity`,
`ValidityPeriod`.

**Acceptance:** an audience token expires and is refused after expiry. Tokens verify on a second node sharing the ring. Two tenants' secrets
use different derived keys (C2 test) and an L2 secret opens and is re-sealed with `y3.`. Linux secret files are 0600 and no plain
`*.secret` remains after first read. KATs pass. Budgets for MAC/token/derivation met.

### Phase 2 — ✅ library DONE (signing slice 2026-10-04, recipient sealing 2026-10-05); approval tokens v2 = Authorization Phase 4b — signatures, recipient sealing, approval tokens v2 (C5, C6)

Needs ⚑K8, K9, K12 and the ⚑Z12 implementation slot.

`ISigningKey`, `ISigningKeyStore` (in-memory, file, `CngSigningKeyStore`), `SignedPayload`, `SignatureVerifier` + public-key pool,
`IPublicKeyDirectory`, `PublicKeyRecord`, `RecipientSealing` (ECIES, 05 §3), `IRecipientKey` (software). Authorization: `ApprovalToken`
v2, `ApprovalTokenVersion1Codec`, `Decision.ComputeDigest()` in Rules (core only).

**Acceptance:** v1 golden payloads byte-identical, v1 signatures verify. v2 tokens fail on a changed decision digest, wrong domain or revoked
key. Device-token verify ≤ 125 µs with a cached key. Recipient-seal golden and negative cases pass. A CNG signing key is non-exportable
(`ExportPolicy None`), and the TPM test runs where a TPM exists. AotSmoke covers signing.

### Phase 3 — ✅ DONE 2026-10-05 — certificates, hash chains, Merkle trees

Needs ⚑K11.

`CertificateAuthority`, `CertificateTrust`, `CertificateCheck`, PKCS#12 export helpers, `HashChain`, `MerkleTree` (+ RFC 6962 vectors).

**Acceptance:** root → issuing CA → leaf with URN SANs built with a TPM-held CA key, and validated with custom root trust while the OS store
is ignored. Expired and revoked cases. Merkle inclusion proofs verify for trees of 1…1 025 leaves. Chain genesis/next goldens.

### Phase 4 — consumer adoption (owned by the other designs)

Privacy Tier 2 (pseudonyms, vault, intake sealing), Offline (installation keys via `CngSigningKeyStore`, tenant CA, bundles, envelopes,
offline verifiers), Audit (sequencer chains, anchors, day seals). This design only tracks that they use the library and add no parallel
primitives (architecture test).

### Phase 5 — post-quantum and platforms (⚑K10, ⚑K18)

Per-recipient capability (published with the recipient's public key; no fleet survey, open question 1) → hybrid recipient sealing `0x11` when the recipient supports ML-KEM. ChaCha20-Poly1305 path
for devices without AES-GCM. Android Keystore and Linux libsecret providers. Re-check `CompositeMLDsa` status. WASM storage encryption
design (WebCrypto interop).

**Status 2026-10-18 (§5):**
- ✅ Hybrid recipient sealing `0x11` — **BUILT and tested** (Windows 11 with ML-KEM): `PostQuantumKeys` per ring (Automatic default),
  automatic sealer choice with a forced overload, 96-byte hybrid ring keys, frozen golden + spec re-derivation test.
- ✅ ChaCha20-Poly1305 path — built in Phase 0 (`SealingAlgorithm.ChaCha20Poly1305`, used only without AES-GCM).
- ⏳ Linux libsecret root key (`SecretServiceRootKeyProvider`) and Linux TPM store — **WRITTEN, not run on Linux** (14 §8.1).
- ⏳ Apple Keychain root key and Secure Enclave store — **WRITTEN, not run on macOS** (14 §8.3).
- Open: Android Keystore (needs a .NET-for-Android project with Java interop, so not in a BCL-only package), WASM design, `CompositeMLDsa`
  (still `[Experimental]` in .NET 11).
- **2026-10-20:** Android Keystore **designed** as the variant package `YFex.Cryptography.Android` (⚑K19, 14 §9; requested by installations
  11 for offline handhelds). Not coded; a real-device spike comes first (installations 09 §4 R7).
- ✅ **2026-10-27:** `YFex.Cryptography.Android` **BUILT** (14 §9.4–9.5): 11/11 device tests on the Android 15 and 16 emulators;
  real-handheld run pending (`androidKit test … --serial`).

**Platform signing-key stores (14; requested by installations 09 §5):**
- ✅ `FileSigningKeyStore` + `StoreSigningKeysInFiles` + the `FileKeyRingStore` permission fix F-P1 — **BUILT 2026-10-18** (§5). Acceptance
  14 §2.6 met on Windows; the Linux run (Unix modes, direct `CopyWithPrivateKey`) is still pending.
- ✅ Generic `ISecretStore` + `FileSecretStore` (open question 3) — **BUILT 2026-10-18**. Automation's adoption is Automation's step.
- ✅ Certificate sources (open question 7) — **BUILT 2026-10-18** (§5): `ISigningCertificate`/`ISigningCertificateSource`,
  `Pkcs12CertificateSource` (sealed secrets), `.Windows` `WindowsCertificateStoreSource` (A1 installed / A3 token), builder
  `SigningCertificatesFrom*`. `.IcpBrasil` with Fiscal; PKCS#11 on demand.
- ⏳ `YFex.Cryptography.Apple` (`AppleSigningKeyStore`, `KeychainRootKeyProvider`) — **WRITTEN 2026-10-18, verification pending on a Mac**
  (14 §8.3, `scripts/verify-macos.sh`).
- ⏳ Linux TPM 2.0 through `tpm2-openssl` (`YFex.Cryptography.Linux` `LinuxTpmSigningKeyStore`) — **WRITTEN 2026-10-18, verification pending
  on Linux** (14 §8.1–8.2, `scripts/verify-linux.sh`; swtpm works).

## 4. Open questions for the user (not blocking Phase 0)

1. ✅ **Withdrawn 2026-10-18 (user): the library is generic, not built for one deployment.** There is no fleet survey to answer.
   Protection and post-quantum use are decided **per host at run time**: `CryptographyCapabilities` reports what the OS supports, the
   root-key ladder takes the best available level (`Hardware` → `OperatingSystem` → `File`/`Memory`), and a sender seals with `0x11` only
   when the recipient's published capability says ML-KEM. An app that runs many hosts (e.g. through YFex.Installations) may record the
   reported capabilities at enrollment, but that is the app's choice, not a library input. (Original question: Windows versions and TPM
   presence on POS checkouts.)
   **User 2026-10-18: "or manually configurable too".** Every automatic choice has an explicit override in the composition root:
   root key `UseCngRootKey(preferHardware: false)` / `UseFileRootKey` / `UseEnvironmentRootKey` / `UseRootKey(custom)`; signing keys
   `StoreSigningKeysInCng(preferHardware: …)` / `StoreSigningKeysInFiles` / `StoreSigningKeysIn(factory)`; secrets `StoreSecretsInFiles` /
   `StoreSecretsIn(factory)`. When hybrid sealing `0x11` is built (Phase 5), the sender's algorithm gets the same treatment: automatic from
   the recipient's capability by default, forced classic or hybrid by configuration.
2. ✅ **Answered 2026-10-05: nothing uses storage encryption yet.** So there is no F1 history to report, and `yfex-storage report`
   is dropped. (Original question:) **F1 history:** has any store already used `AddYFexStorageEncryption(OsKeyStore)` in production or pilots? If so, Phase 0's
   `yfex-storage report` should run there before upgrading, and the deploy rule (09 §2) should be communicated.
3. ✅ **Decided 2026-10-18 (user: "go with the best approach … configurable, a good interface API that doesn't need to know its
   implementation or how Automation is working so external can use it").** The library owns a generic, Automation-agnostic
   **`ISecretStore`** (`GetAsync`/`SetAsync`/`DeleteAsync` + `GetTextAsync`/`SetTextAsync`) with `FileSecretStore` (sealed files under
   the configured root key: TPM/CNG on Windows, 0600 file or environment key elsewhere) and `InMemorySecretStore` (.Testing); the app
   picks the store with `StoreSecretsInFiles(…)` or `StoreSecretsIn(factory)` (a vault client later). Automation then implements its own
   `ISecretStore` contract over it (or switches to it) and migrates `DpapiSecretStore`/plain-text `*.secret` files once (read old →
   `SetAsync` → delete old). That step is Automation's, not done here. (Original question: keep `DpapiSecretStore` or move to the CNG root?)
4. **Web.Data tenants with their own key lifecycle (10 §6):** needed at launch, or is the derived per-tenant key enough for now?
   *(2026-10-05: user "maybe"; benefits explained in chat. Nothing to build: `ScopedKeyRings.CreateDedicatedRingAsync` (Phase 1)
   already gives one tenant its own ring on demand, and `ApiEndpointSecretMigration` re-seals into it.)*
5. **Fiscal:** when NF-e signing is built, approve `System.Security.Cryptography.Xml` then (⚑K14), or look for a trimming-safe XMLDSig
   implementation (own canonicalization code = a large review surface)?
   *(2026-10-05: user asked whether a dedicated signature library or a variant of this one is better. Recommendation in 12 §5 "Phase 3"
   below: a satellite package **`YFex.Cryptography.Xml`** in this repo. It must be AOT-safe because NFC-e is signed at the checkout,
   and the net11 `SignedXml` package still declares IL2026/IL3050. ✅ **User 2026-10-05: create it.** Built, see 13 and §5.)*
6. **Tenant root CA curve:** offline 03 §3 says the tenant root is ECDsa **P-384**. The library's CA signs with P-256 only (⚑K8 keys,
   TPM support). Recommendation: a P-256 root (128-bit security for a 10-year root; TPM 2.0 guarantees P-256 but not P-384). Otherwise
   P-384 must be added to `SignatureAlgorithm` (core) and to the signing stores. ✅ **User 2026-10-05: P-256.** Offline 03 §3 is
   amended by the Offline session (handoff in §5).
7. ✅ **Approved 2026-10-18 (user: "agree"):** abstraction + PKCS#12 and Windows-store sources first; `YFex.Cryptography.IcpBrasil` when
   Fiscal needs it; PKCS#11 when a real token/HSM case appears (13 §6). **Abstraction + PKCS#12 + Windows-store sources BUILT 2026-10-18**
   (§5). (Original question:) **Certificate sources (13 §6):**
   approve the `ISigningCertificate` / `ISigningCertificateSource` abstraction and a
   `YFex.Cryptography.IcpBrasil` satellite (CNPJ/CPF from OtherName, ICP-Brasil roots, LCR)?
   *(2026-10-07: a generic PKCS#11 signing store (14 §6 route B) is the natural shape for A3 tokens and HSMs on every OS. Consider it
   with this question.)*
8. **Linux TPM 2.0 (14 §6):** approve a native dependency for a `.Linux` TPM store: distro packages `tpm2-openssl` + `tpm2-tss`, access to
   `/dev/tpmrm0` (the `tss` group), keys through the BCL `SafeEvpPKeyHandle.OpenKeyFromProvider` (route A, recommended; it also gives TLS
   with the TPM key)? Until then Linux keys are `FileSigningKeyStore` (`KeyProtection.File`).
   ✅ **Approved 2026-10-18 (user): route A.** The dependency is optional at run time: the `.Linux` store reports `Hardware` only when the
   `tpm2` provider loads and `/dev/tpmrm0` is accessible, otherwise the app falls back to `FileSigningKeyStore`. Spike per 14 §6
   (swtpm in a container or a Linux PC with a TPM); not started — no Linux environment on the development PC yet.
   **2026-10-18 (user: create it, keep the testing pending):** `YFex.Cryptography.Linux` written and compiled; run pending (14 §8).

## 5. Implementation log

### 2026-10-27 — Named recipient keys (`IRecipientKeyStore`, installation key-agreement key) BUILT

User: "yes" (build the CNG key-agreement key before Installations I5). Design and findings: 14 §10. Backup:
`C:\Temp\cryptography-backup\recipient-keys-2026-10-27`.

- Main library (additive): `IRecipientKeyStore`, `SealedRecipientKeyStore` (any `ISecretStore`, hybrid when ML-KEM), builder
  `StoreRecipientKeysInFiles` / `StoreRecipientKeysIn` / `HasRecipientKeyStore`, `IRecipientKey.Protection` (default member, `Memory`).
  IVT for `.Windows` (on-curve check).
- `.Windows`: `CngRecipientKeyStore` + `Interop.NCrypt` (LibraryImport: import flags 0, secret agreement, raw `TRUNCATE`) and
  `StoreRecipientKeysInCng()`. The BCL `ECDiffieHellmanCng` cannot agree with a TPM key (probe in 14 §10).
- Tests: cryptography 241, Windows 20 (TPM case ran here), AOT smoke 0 IL warnings.
- Seen again: the `KeyRingTests.Two_processes_creating_the_ring_at_once…` flake (`store.key` sharing violation) failed once in a
  full run and passed on rerun. The `StoreKeyFile.UnwrapAsync` retry follow-up still stands.

### 2026-10-27 — `YFex.Cryptography.Android` (⚑K19) BUILT

User: "yes you can start it". Backup: `C:\Temp\cryptography-backup\android-2026-10-27` (docs, `RootKeyProviders.cs`,
`cryptography.slnx`, README, installations 09/11).

- **Design gaps closed first** (14 §9.4): Auto Backup → key folders under `NoBackupFilesDir` (`AndroidKeyPaths`); lost keys →
  `AndroidKeystoreKeyLostException`, recovery = re-enroll; runtime → no assumption, trim-clean, NativeAOT evaluated with the app.
- **Package** `src/YFex.Cryptography.Android` (`net11.0-android`, API 26+, `IsTrimmable`, analyzers on): `AndroidKeystoreSigningKeyStore`
  (`ICertificateKeyStore`; digests NONE + SHA-256; `GetOrCreateAsync(name, attestationChallenge)`, `ReadAttestationChain`; TLS through an
  in-memory `PrivateKeyEntry`, so the stored attestation chain stays), `AndroidKeystoreRootKeyProvider` (Keystore AES-256-GCM key wraps
  each blob; StrongBox first), `AndroidKeyPaths`, builder `StoreSigningKeysInAndroidKeystore()` / `UseAndroidKeystoreRootKey()`.
- **Main library (additive):** `RootKeyFormat.AndroidKeystore = 8`; 05 §7 updated.
- **Deviation from the 9.2 sketch:** no static `AndroidKeyAttestation`; attestation is on the store (callers never see aliases). The
  root key wraps blobs itself instead of wrapping a root-key file (the key never leaves the Keystore).
- **Solution:** own `cryptography.android.slnx` (needs the Android workload); `cryptography.slnx` unchanged, so it still builds on
  Linux/macOS.
- **Tests:** `tests/YFex.Cryptography.Android.DeviceTests` (11 instrumentation tests, `androidKit test`): **11/11 on Android 16 ATD and
  Android 15 emulators**; Release build with trimming 0 warnings; Windows suite 235/235.
- **Pending:** a real handheld (StrongBox/TEE protection, timing, Google attestation root, StrongBox with `DIGEST_NONE`), then
  installations I6 adopts it (`YFex.Installations.Android`).

### 2026-10-27 — Wycheproof and NIST ACVP test vectors (⚑K14 data, approved by the user)

The user asked what the pending vectors were and approved the download from trustworthy sources only. Sources: Project Wycheproof
at `github.com/C2SP/wycheproof` (its community home; `google/wycheproof` points there), commit `d17cc89f…` (2026-10-23),
Apache-2.0; NIST `github.com/usnistgov/ACVP-Server`, commit `975de31e…` (2026-08-12), NIST software notice. Files were downloaded
into an empty scratch folder, inspected, then copied to `tests/YFex.Cryptography.Tests/Vectors/` (4.1 MB; commit, licence and SHA-256
of every original file in `Vectors/README.md`). Wycheproof files are unchanged; the ACVP files are trimmed to ML-KEM-768 (noted, as
the NIST notice asks). Backup: `C:\Temp\cryptography-backup\test-vectors-2026-10-27`.

**Rule kept:** a vector file is copied only when its inputs reach a library code path. HKDF, HMAC and PBKDF2 vectors would test only
the BCL behind our own labels (the RFC KATs already cover the byte order), so they were not copied.

| Test | Vectors | Library path |
|---|---|---|
| `WycheproofTests` signatures (7 files) | ECDSA P-256 SHA-256 P1363; RSA PKCS#1 v1.5 and PSS (MGF1-SHA-256, salt 32) at 2048/3072/4096 | `ExternalPublicKeys.TryCreate` (DER) and `TryCreateEcdsaP256` (same id) → `SignatureVerifier.VerifyHash` |
| ECDH | `ecdh_secp256r1_ecpoint` (355: edge cases, invalid-curve points, compressed and twist points) | ring `IRecipientKey.TryDeriveSharedSecret` (the on-curve check) |
| AES-GCM | the 256-bit key / 96-bit nonce / 128-bit tag groups (66, incl. modified tags) | `LegacyFormats.OpenPersistenceVersion2` |
| ML-KEM-768 | `mlkem_768_test` (201: decapsulation from the seed, implicit rejection, wrong key/ciphertext lengths); `mlkem_768_encaps_test` (265, incl. modulus overflow) | hybrid 96-byte ring key (`PublicKey`, `TryDecapsulate`); `RecipientSealing.Seal` refuses a bad encapsulation key |
| `AcvpMLKemTests` | keyGen (25), encapsulation-key check (10) | ring key publishes the FIPS 203 `ek` from d ‖ z; `Seal` accepts/refuses as ACVP says |
| `AcvpMLKemTests` (platform) | keyGen `dk`, decapsulation (10), decapsulation-key check (10) | BCL `MLKem` only: the library never imports an expanded key. ACVP encapsulation with a fixed m is not testable (the BCL takes no m) |

**Result:** all 15 new tests green on Windows 11 (ML-KEM available); no library change was needed. Cryptography tests **235**
(before 220). The ML-KEM tests return early on a host without ML-KEM, like the hybrid tests. No product code or AOT surface changed.

**Found (not caused by this work, not fixed):** `ArchitectureTests.Only_YFex_Cryptography_uses_the_cryptographic_primitives_it_owns`
fails because it scans the whole `C:\Dev\YFex` tree and finds `ECDsa.Create()` in the R7 probe scratch app
`_ignore/android-probe/Probe.cs` (2026-10-21). **Fixed the same day (user: skip it):** `_ignore` added to
`ArchitectureTests.SkippedDirectories` (scratch spikes and probes, not shipped code). Cryptography tests 235/235 green.

### 2026-10-19 — Authentication request C2 (`OneTimePassword`, TOTP/HOTP) BUILT

Requested by authentication 14 §2.2 (⚑AN12) for AN-P5; built by the AN-P5 session (no cryptography session was running; the user allows
changing other libraries with their docs aligned). Backup: `C:\Temp\authentication-backup\P5\other\YFex.Cryptography.tar`.

- `Authentication/OneTimePassword.cs`: `OneTimePassword` (HOTP RFC 4226 §5.3 dynamic truncation, TOTP RFC 6238 with T0 = epoch,
  floor steps before the epoch), `OneTimePasswordParameters` (default struct = 6 digits / 30 s / SHA-1 / window 1; validated `init`
  properties), `OneTimePasswordCheck`, `OneTimePasswordAlgorithm`, RFC 4648 base32 `EncodeSecret`/`TryDecodeSecret` (case-insensitive,
  spaces/hyphens/padding ignored, non-zero trailing bits refused). Shape in 04 §13.
- Architecture test: `HMACSHA1.HashData` added to the banned APIs (regex `HMACSHA(1|256|384|512)\.HashData`); no allow-list entry.

**Tests:** cryptography **220** (before 190; +30 `OneTimePasswordTests`): RFC 4226 appendix D (10 HOTP values), RFC 6238 appendix B (all
18 SHA-1/SHA-256/SHA-512 vectors at 8 digits, plus the 6-digit default), window ±1 accepted and ±2 refused unless configured, step
replay (`Replayed` for the same step and for an older step after a newer one was accepted), malformed codes (length, letters, spaces,
non-ASCII digits), base32 RFC 4648 §10 vectors and round trips, parameter bounds. AotSmoke **37/37** (new: RFC 6238 vector + replay),
0 IL warnings, exit 0.

### 2026-10-18 — Authentication requests C1 (verify-only RSA for external keys) and C5 (fixed P-256 key from PKCS#8) BUILT

Requested by authentication 14 §2.2 (⚑AN11) for AN-P3 (OpenID Connect). The user asked the Authentication session to build both here
("add C1 … and C5 … to YFex.Cryptography with tests and the AOT check, then start AN-P3"); the cryptography sessions were idle.
Backup: `C:\Temp\cryptography-backup\c1-c5-2026-10-18`. Golden generator: `C:\Temp\yfex-edits\rsa_golden.py`.

**C1 — verify-only RSA:**
- Core `SignatureAlgorithm.RsaPkcs1Sha256 = 2` (JWA RS256) and `RsaPssSha256 = 3` (PS256: MGF1-SHA-256, 32-byte salt). Documented as
  verify-only; every signer (`SigningKeyRing`, file/CNG/TPM/Apple/in-memory stores) still throws `NotSupportedException` for them.
- `SignatureVerifier` pools ECDsa **or** RSA per key id (the cache entry also matches the record's algorithm). RSA signature length
  = modulus length (else `Malformed`); a value above the modulus → `Invalid`. Default allowed set unchanged (ES256 only), so no existing
  verifier (Offline bundles, Installations enrollment, Messaging.Rpc central time) accepts RSA — those readers cast the algorithm byte
  from the wire, and an RSA record now fails as `AlgorithmNotAllowed`.
- New `ExternalPublicKeys`: `TryCreateRsa(n, e, algorithm, out record, validity?)`, `TryCreateEcdsaP256(x, y, …)`,
  `TryCreate(algorithm, spki, …)`. Untrusted input → false, never throws. The SubjectPublicKeyInfo is re-encoded by the platform and
  the id is `PublicKeyRecord.KeyIdOf` (a JWK `kid` is the caller's to map). Limits (internal `RsaKeys`): 2 048–8 192 bits, odd modulus,
  odd exponent ≥ 3 and ≤ 32 bits; leading zero bytes of `n` ignored; off-curve P-256 points refused (Windows CNG throws
  `PlatformNotSupportedException`, caught).
- `CryptographyCapabilities.IsSupported` reports both RSA algorithms (not in the browser).

**C5 — fixed imported signing key** (deviation from the request's "into a signing ring": rings rotate and a ring key id is ours,
while an issuer's key must never rotate here, so it is a standalone key):
- `ImportedSigningKey : ISigningKey` — `FromPkcs8(der)`, `FromPem(text)` (first `-----BEGIN PRIVATE KEY-----` block; Apple `.p8`),
  unencrypted PKCS#8 P-256 only (P-384, RSA, encrypted PKCS#8, SEC1 `EC PRIVATE KEY`, trailing bytes → `ArgumentException`). Holds the
  32-byte scalar, one pooled ECDsa; `Dispose` zeroes it and later signing throws `ObjectDisposedException`. `Id` = `KeyIdOf(SPKI)`,
  `ToRecord()` for a verifier directory.
- `ImportedSigningKeyExtensions` on `ISecretStore`: `ImportSigningKeyAsync(name, pem)` validates before storing the DER;
  `GetSigningKeyAsync(name)` → key or null (`Protection` = the `FileSecretStore`'s; a corrupt secret → `CryptographicException`).

**Tests:** cryptography **190** (before 175; +9 `ExternalKeyTests`, +6 `ImportedSigningKeyTests`), Windows 15, Xml 21 green;
architecture test green with no new allow-list entry.
- Independent RS256 and PS256 golden: a 2 048-bit key and signatures made in pure Python (EMSA-PKCS1-v1_5 / EMSA-PSS by hand, no
  crypto library) over a JWS signing input. The RFC 7515 A.3 ES256 vector is read through `TryCreateEcdsaP256` from the JWK `x`/`y`.
- Also covered: RSA refused by a default verifier and by a PS256-only verifier; padding confusion (a PS256 signature against an RS256
  record); an RSA key mislabelled ES256; wrong signature lengths; a value above the modulus; 1 024-bit keys (through `ExternalPublicKeys`
  and as a raw directory record); exponents 1, even and empty; a modulus over 8 192 bits; 2 048/3 072/4 096-bit platform signatures;
  brainpool and off-curve EC points.
- Imported keys: a PEM bundle with other blocks; a file secret store across a restart (the private scalar is absent from every file);
  64 parallel signatures; double dispose.

AotSmoke **36/36**, 0 IL warnings, exit 0 (new checks: an external RSA key verifies RS256 only when allowed; a PKCS#8 key is imported,
sealed, reloaded and signs). Consumers still build: Authentication (112 tests green), Installations, Offline.

| Benchmark (ShortRun, in-process, this PC) | Mean | Allocated |
|---|---|---|
| raw `RSA.VerifyHash` PKCS#1 2 048 | 11.83 µs | 0 B |
| `SignatureVerifier` RS256 2 048, key cached | 11.87 µs (ratio 1.00) | 0 B |
| `SignatureVerifier` PS256 2 048, key cached | 12.41 µs | 0 B |
| `ExternalPublicKeys.TryCreateRsa` 2 048 (per key on a JWKS refresh) | 11.06 µs | 3.8 KB |
| `ImportedSigningKey.SignHash` P-256 | 116 µs | 200 B |

Not done: Linux/macOS runs (as for the rest of the library); RS384/RS512/ES384 are not requested (no provider in authentication 06
needs them).

### 2026-10-18 — Phase 5: hybrid recipient sealing `0x11` BUILT; `.Linux` and `.Apple` WRITTEN (run pending on those OSes)

User: "continue next phase", then "for Linux/Mac … just create it and keep the testing pending on those platforms, so when possible I
will test it", then the order "0x11 first (fully tested here), then .Linux, then .Apple, both opt-in with pending verify scripts and
checklists". Backups: `C:\Temp\cryptography-backup\phase5-hybrid` (code, `docs\`, memory). Edit scripts `C:\Temp\yfex-edits\p5_*.py`.

**Hybrid `0x11` (05 §3 as built, 08 §2 note):**
- `RecipientSealing`:
  - Writes and opens `0x11`. Header 1 171 bytes, overhead 1 187 (`HybridHeaderLength`, `HybridOverhead`).
  - IKM = ECDH secret ‖ ML-KEM secret. Salt = header after the key id ‖ key id (the classic rule generalised; classic bytes are unchanged
    and the 2026-10-05 golden still rebuilds).
  - `ChooseAlgorithm(recipient)`: hybrid when the recipient published it and this host runs ML-KEM, else classic to the P-256 half.
  - The forced overloads `Seal(…, algorithm)` and `Seal(…, destination, algorithm)` are the manual override (12 §4 question 1).
    Forcing the hybrid needs a hybrid key and ML-KEM here.
  - `GetSealedLength(recipient, n)` and an exact `GetPlaintextLength(sealedValue)`. The `int` overload is now an upper bound.
  - `TryReadHeader` reads both algorithms.
  - The sender's `[ThreadStatic]` cache also keeps the imported ML-KEM encapsulation key.
- `RecipientPublicKey`:
  - `MLKem768Length = 1184` and `HybridLength = 1249`.
  - A hybrid key publishes P-256 point ‖ ML-KEM encapsulation key.
- `IRecipientKey.TryDecapsulate` is a default interface member (false), so existing implementers are unaffected.
- `RecipientKeyRing`:
  - A 96-byte key is hybrid: one pooled ECDH and one pooled ML-KEM instance. It opens both `0x01` and `0x11`.
  - On a host without ML-KEM it publishes only the P-256 half, so sealers never pick a format the host cannot open.
- `SecretKey`:
  - `KeyAgreement` accepts 32 or 96 bytes (`HybridKeyAgreementLength`).
  - `Generate(KeyAgreement, 96)` = platform P-256 scalar ‖ **independent** random ML-KEM seed. Deriving the seed from the scalar would let
    a quantum attacker who recovers the scalar break both halves.
- `KeyRingOptions.PostQuantum(PostQuantumKeys)`:
  - New enum, Automatic (the default) / Always / Never.
  - `GeneratedKeyLength()` decides the size of new keys.
  - Existing keys keep their kind; a ring turns hybrid at its next rotation.
  - `Always` on a host without ML-KEM throws `PlatformNotSupportedException`.
- `CryptographyCapabilities.IsSupported(Hybrid)` = AES-GCM + ML-KEM, and not the browser.
- `AlgorithmUnavailable.RecipientAlgorithm`: an init property stored in the union's unused number field. It reports "this host lacks
  ML-KEM" without a new union case, so existing exhaustive switches still compile.
- **Behaviour change:** a `KeyAgreement` ring created on an ML-KEM host is now hybrid by default. No consumer uses recipient sealing yet
  (grep 2026-10-18). Mixed server farms (some nodes without ML-KEM) should set `PostQuantum(Never)` or make every node ML-KEM capable.

Tests: cryptography **174** (+9 `HybridRecipientSealingTests`; 1 updated expectation in `RecipientSealingTests`). The new tests cover:
- a frozen value (2026-10-18) that must keep opening;
- the layout re-derived with BCL primitives (ECDH, `MLKem.Decapsulate`, `HKDF`, `AesGcm`);
- the automatic choice and the forced classic;
- forced hybrid to a classic key, an invalid ML-KEM key, and wrong lengths;
- tampering of the ephemeral key, the ML-KEM ciphertext (first/middle/last byte), the ciphertext and the tag; a wrong context;
  truncation; a relabelled algorithm byte; a classic twin key with the same id;
- ring policy classic → hybrid at the next rotation, with the old value reported `NeedsReseal`;
- a hybrid key that survives a file-store restart;
- key sizes and the unavailable-outcome round trip;
- 16 threads × 2 recipients.

The existing ring tests (rotation, destroy, file restart) now run with hybrid keys on this host and are green. AotSmoke **34/34**, 0 IL
warnings (+3 hybrid checks; the existing classic check now pins `PostQuantum(Never)`). Measured (Windows 11, Release, Stopwatch over
2 000 runs): seal 0.8–1.1 ms and open 0.6–0.8 ms for both algorithms. ML-KEM (≈ 11 µs encapsulation, 18 µs decapsulation, 15 µs key
import) is lost in the ECDH noise; a sealed CPF is 1 198 bytes instead of 110.

**`YFex.Cryptography.Linux` (14 §6 route A, 12 §4 question 8): WRITTEN, NOT RUN ON LINUX.**
- `LinuxTpmSigningKeyStore : ICertificateKeyStore`:
  - Creates P-256 keys in the TPM with non-variadic OpenSSL 3 calls in a private library context with `tpm2` + `default` loaded.
  - Encodes them to a `TSS2 PRIVATE KEY` PEM (the TPM-wrapped blob) and stores the blob 0600 under `ProtectedFiles.NamedPath`; first
    writer wins.
  - Opens the blob with `SafeEvpPKeyHandle.OpenKeyFromProvider("tpm2", "object:<path>")` → `ECDsaOpenSsl`. That gives signing and
    `CopyWithPrivateKeyAsync` for TLS. Protection `Hardware`.
  - `IsAvailable()` checks Linux, `/dev/tpmrm0` read/write **or** `TPM2OPENSSL_TCTI` (swtpm), and that the tpm2 provider loads.
- `SecretServiceRootKeyProvider : ProvidedRootKeyProvider` (id `0x06`, `OperatingSystem`):
  - The 32-byte key is stored as base64 in the default collection through `secret_password_storev/lookupv/clearv_sync`.
  - It uses a native `SecretSchema` (LP64 layout written by hand) and a `GHashTable` of attributes.
  - The password buffers are wiped.
- Builder: `StoreSigningKeysInLinuxTpm(dir)`, `UseSecretServiceRootKey(name)`.

**`YFex.Cryptography.Apple` (14 §5): WRITTEN, NOT RUN ON macOS.**
- `AppleSigningKeyStore : ISigningKeyStore`:
  - Secure Enclave first; otherwise, when allowed, a non-extractable Keychain key.
  - Keys are found by application tag. Uses `SecKeyCreateRandomKey`, `SecItemCopyMatching`, `SecKeyCreateSignature` (DER → P1363),
    `SecKeyCopyExternalRepresentation` (→ SPKI) and `SecItemDelete`.
  - Protection comes from the key's `kSecAttrTokenID`.
  - `IsAvailable()` and `IsSecureEnclaveAvailable()` (the latter makes a non-permanent probe key).
  - Signing only, not TLS (14 §5.3).
- `KeychainRootKeyProvider` (id `0x07`): a generic password, login keychain by default or the data-protection keychain with an
  entitlement; `Delete`.
- Builder: `StoreSigningKeysInAppleKeychain(…)`, `UseKeychainRootKey(…)`.
- CoreFoundation and Security go through `LibraryImport`. The `kSec…` constants are read from the frameworks' exports. A `CFHandle`
  SafeHandle releases each object once.

**Shared:**
- `RootKeyFormat.SecretService = 6` and `AppleKeychain = 7`.
- The `ProvidedRootKeyProvider(key, protection, id)` constructor changed from `private protected` to `protected`, so OS-keyring
  providers in satellite packages can derive from it.
- Core `InternalsVisibleTo` `.Linux`/`.Apple` for `ProtectedFiles`.
- `cryptography.slnx` lists both packages and their test projects.
- AotSmoke references both. Its platform checks run when the feature is present; `YFEX_REQUIRE_*=1` turns a missing feature into
  `[FAIL]`.
- Scripts `scripts/verify-linux.sh` and `scripts/verify-macos.sh`, checklist in 14 §8.

Tests:
- Linux.Tests 6 and Apple.Tests 7 pass on Windows. Only the guards, the builder registration, and the Apple DER → P1363 and SPKI
  conversions (checked against the BCL) really ran. The TPM, keyring and Keychain tests return early here.
- On their OS those tests fail instead of skipping when the matching `YFEX_REQUIRE_*` variable is set.
- Both packages compile with `IsAotCompatible` (analyzers on), and the Windows AOT publish that references them has 0 IL warnings.
- Windows 15 and Xml 21 are unchanged and green.
- Consumers still build: Persistence.Encryption, Installations, Offline, Authentication, Authorization, Time.Cryptography, Web.Data.

Found on the way (not fixed, not reproduced in 12 more runs): one run of `KeyRingTests.Two_processes_creating_the_ring_at_once…` failed
with "store.key is being used by another process". The likely cause is a Windows sharing violation on a reader while another writer
renames `store.key` into place (antivirus or `MoveFileEx`). A bounded retry on sharing violations in `StoreKeyFile.UnwrapAsync` would
harden it. This is a follow-up.

Not done / pending:
- ML-KEM KAT: there are no NIST ACVP vectors offline. Adding them needs a download of the vector files; ask the user.
  **✅ Done 2026-10-27** (user approved the download): see "2026-10-27 — Wycheproof and NIST ACVP test vectors" below.
- The Linux and macOS runs (14 §8).
- Android Keystore: needs a .NET-for-Android project, so it does not fit a BCL-only package.
- WASM design.
- `CompositeMLDsa` (still `[Experimental]`).

### 2026-10-18 — signing-certificate sources (open question 7) BUILT

Approved shape (13 §6, user 2026-10-18), built as the caller-facing syntax shown to the user before coding. Backups:
`C:\Temp\cryptography-backup\certificate-sources` (same relative paths). Edit scripts: `C:\Temp\yfex-edits\certsrc_*.py`.

Built:
- `YFex.Cryptography/Certificates/SigningCertificates.cs`:
  - `ISigningCertificate : IDisposable`: `Certificate` (private key attached when local), `PrivateKey` (`AsymmetricAlgorithm`, i.e. RSA
    or ECDsa, owned by the handle), `Protection`.
  - `ISigningCertificateSource.GetAsync(purpose, ct)` returns **null when nothing is configured** for the purpose (the same rule as
    `ISecretStore.GetAsync`). A configured certificate that cannot be used throws `SigningCertificateException` (a `CryptographicException`).
    Expired certificates are returned: validity is the caller's policy.
  - `SigningCertificate`: the ready handle. `(certificate, protection)` takes the attached key. `(certificate, privateKey, protection)` is
    for remote signers and checks that the key matches the certificate by its public parameters.
  - `Pkcs12CertificateSource(ISecretStore, secretNamePrefix = "certificates/", storage = null)`: `ImportAsync(purpose, pkcs12, password)`
    validates the file before storing it, plus `RemoveAsync` and `GetAsync`.
  - Secret format (05 §7 family): version `0x01` · password (UTF-8 **bytes**, not `WriteText`, whose NFC would change a non-NFC password) ·
    the original PKCS#12 bytes. Password and file are one secret, so a renewal replaces both in one write.
  - Loads with `Pkcs12LoaderLimits.Defaults` and the password as a pooled `char[]` (zeroed). Keys report `KeyProtection.File`.
  - `storage` overrides the key storage flags (default `CertificateFiles.DefaultStorage`, now `internal`). Use `DefaultKeySet` when the
    certificate must be a Windows TLS credential (Schannel refuses ephemeral keys, 14 §2.5).
- `CryptographyBuilder`: `SigningCertificatesFromSecrets(prefix, storage)` uses the configured `ISecretStore`, and gives a clear error
  when no secret store was chosen. `SigningCertificatesFrom<TSource>(factory)` also registers `TSource` as itself, so callers reach
  `Import`/`Add`. `HasSigningCertificateSource`. The last choice wins: the earlier source's concrete registration is removed too.
  Nothing is registered by default.
- `.Windows/WindowsCertificateStoreSource.cs` (`[SupportedOSPlatform("windows")]`):
  - `Add(purpose, thumbprint, location = CurrentUser, storeName = My)` (adds or replaces) and `Remove(purpose)`, in a concurrent map, so
    mappings can change at run time.
  - Static `Open(thumbprint, …)` for apps that keep the mapping themselves.
  - Thumbprints may be SHA-1 (40 hex digits, as certmgr shows them) or SHA-256 (64). Spaces, `:`, `-` and the invisible U+200E/U+200F/U+FEFF
    copied from the certificate dialog are ignored.
  - Errors: missing certificate (removed, or the middleware unpublished it with the token unplugged), certificate without a key, and a
    key that cannot be opened all throw `SigningCertificateException`.
  - Protection: the CNG `Impl Type` property (hardware or removable flag), else the provider name (Platform Crypto / Smart Card KSP),
    gives `Hardware`; a CSP key uses `CspKeyContainerInfo.HardwareDevice || Removable`. Everything else is `OperatingSystem`.
  - Builder: `SigningCertificatesFromWindowsStore(store => store.Add(…))`.
- `.Xml`: `XmlSignature.SignEnveloped(referenced, ISigningCertificate, algorithm, parent)` overload (RSA only, `ArgumentException` otherwise).

Tests:
- **Cryptography 165** (+10, `SigningCertificateTests`):
  - import/get/sign; restart over `FileSecretStore` (the bytes are sealed on disk); null and remove; renewal; wrong password, non-PKCS#12
    and key-less files refused with nothing stored; non-NFC password and no-password files; ECDSA;
  - tampered version, trailing or truncated record;
  - handle key mismatch and remote handle;
  - DI: last wins, the earlier concrete registration removed, nothing by default, clear error without a secret store.
- **Windows 15** (+5, `WindowsCertificateStoreSourceTests`, unique names, certificates and keys removed afterwards):
  - software CNG key by SHA-1 thumbprint as pasted from the dialog, and by SHA-256 thumbprint → `OperatingSystem`;
  - **TPM** CNG key → `Hardware` (stands in for a token's hardware KSP);
  - legacy **CSP** key (`PROV_RSA_AES`, the shape many A3 middlewares register) signs SHA-256;
  - errors and run-time remapping; DI.
- **Xml 21** (+5): a PKCS#12-source certificate signs byte-identically to the SignedXml goldens (4 cases); remote-signer handle; ECDSA refused.
- `ArchitectureTests` green, no allow-list change.
- AotSmoke **31/31**, 0 IL warnings. New checks: a PKCS#12 certificate from sealed file secrets through DI (reopened by a second provider)
  signs and verifies an NF-e; an unconfigured purpose returns null; a Windows-store certificate by thumbprint through DI signs an NF-e.

Measured (Windows 11, Release, Stopwatch over 200 runs, probe `C:\Temp\certprobe`): `Pkcs12CertificateSource.GetAsync` **3.6 ms**, of which
`LoadPkcs12` (PBES2, 2 048 iterations) is 3.0 ms; plus an RSA-2048 signature, 4.0 ms. That is fine per document (NFC-e at the sale), so
there is no cache. A caller signing a batch keeps one handle for the whole batch.

Not verified / gotchas:
- No real A3 token or smart card was available. The token path is covered by a TPM KSP key (hardware `Impl Type`) and a software CSP
  key; a vendor middleware's PIN dialog and "token unplugged" behaviour were not exercised.
- An old smart-card CSP of type `PROV_RSA_FULL` cannot sign SHA-256 (eSocial). NF-e's SHA-1 works. A minidriver (Smart Card KSP) does both.
- `RSACng.Key` / `ECDsaCng.Key` return the instance the algorithm object itself uses. **Never dispose it** (a first draft did, and every
  later operation threw `ObjectDisposedException`).
- Python heredocs in bash turn `\u0301` into real characters (known gotcha); the test's non-NFC password is now written with escapes.

Not in scope (unchanged): `YFex.Cryptography.IcpBrasil` (with Fiscal), PKCS#11 (on demand), cloud-provider adapters (each its own package,
over `SigningCertificate(certificate, remoteRsa, protection)`). Possible follow-ups: an unattended PIN for A3 (CNG `SmartCardPin` /
`CspParameters.KeyPassword` from an `ISecretStore`), and a per-purpose composite source (A1 for one emitter, A3 for another).

### 2026-10-18 — `FileSigningKeyStore`, F-P1, generic `ISecretStore` (open questions 1, 3, 7, 8 answered)

User answers: question 1 withdrawn (generic library, manual overrides everywhere), 3 → generic secret store, 7 → approved, 8 → approved
(§4). User go: "YOU can start". Backups: `C:\Temp\cryptography-backup\file-stores-2026-10-18` (code) and `…\questions-2026-10-18` (docs).

Built (all in `YFex.Cryptography` unless noted):
- `Keys/ProtectedFiles.cs` (internal): owner-only directories (0700) and atomic 0600 writes (temp + write-through + flush + rename,
  first-writer-wins variant), `TightenFile`, `NamedPath` (the shared `{readable}-{hash8}.ext` rule).
- `Keys/StoreKeyFile.cs` (internal): the `store.key` create/unwrap shared by every file store (moved out of `FileKeyRingStore`;
  bytes unchanged). **F-P1 fixed:** `FileKeyRingStore` writes 0600 files in a 0700 directory and narrows an older `store.key` when it opens
  it; `FileRootKeyProvider` creates a missing directory 0700 (an existing one is the app's and is left alone) and narrows its key file.
- `Signing/FileSigningKeyStore.cs`: `FileSigningKeyStore : ICertificateKeyStore, IDisposable` + `SigningKeyStoreException`, format 05 §7
  (the algorithm enters the seal context as its byte in invariant decimal, so an enum rename cannot orphan files). Load checks in the
  order of 14 §2.3. **Deviation:** each store instance caches the decrypted key and reuses it while the file's bytes are unchanged (the file
  is still read and compared on every call); returned `ISigningKey` handles are cheap, disposing one only disables it, and they stop
  working when the store is disposed. Reason: Windows CNG imports an ephemeral P-256 key in 400–550 µs, so an uncached open was 1.7 ms
  against the 200 µs budget.
- `Secrets/SecretStores.cs`: `ISecretStore`, `SecretStoreExtensions` (UTF-8 text, buffers zeroed), `FileSecretStore` (`*.sealedsecret`,
  05 §7), `SecretStoreException`. `.Testing`: `InMemorySecretStore`.
- `CryptographyBuilder`: `StoreSigningKeysInFiles(dir)`, `StoreSigningKeysIn(factory)`, `StoreSecretsInFiles(dir)`, `StoreSecretsIn(factory)`,
  `HasSigningKeyStore`, `HasSecretStore`; the last choice wins; nothing is registered unless the app chooses. `.Windows`:
  `StoreSigningKeysInCng(prefix, machineScope, preferHardware)`. The builder's constructor is now internal (only `BuilderOf` creates it;
  no caller used it).

Tests: cryptography **155** (+16: `FileStoreTests` — sign/verify, restart, delete/new id, `a/b` ≠ `a_b`, tamper version/ciphertext/tag/key
id, copied to another name/store, other root, 8 parallel creates on two instances → one key, ring + signing store sharing one `store.key`,
mutual TLS 1.3 with file keys, cache notices a changed file, secret round trip/restart/rename/tamper/delete, DI last-wins + nothing by
default, Unix modes (runs only on Unix)), Windows 10, Xml 16; Installations 125 + Windows 6 and Persistence.Encryption 15 still green.
AotSmoke **28/28**, 0 IL warnings (new: file signing key + file secret through DI, reopened by a second provider). Benchmarks (Windows 11,
in-process, ShortRun): open an existing file signing key **145 µs** / 2.1 KB; read a 48-byte secret **134 µs** / 2.0 KB (both dominated by the
file read).

Found and fixed on the way:
- `CertificateTests.Expiry_not_yet_valid_revocation_and_foreign_roots` failed (`InvalidExtension` instead of `PartialChain`):
  `CurrentUser\CA` held 102 stale "CN=farmaviva installations" CAs (2026-10-13, from before Installations stopped passing intermediates,
  §5 2026-10-14). Windows completed the chain through one of them. With the user's OK they were removed, and the test's issuing CA now has
  a unique name per run.

Pending: the Linux run (14 §4); Automation's adoption of `ISecretStore` (Automation repo); certificate sources (question 7, built the
same day, entry above); Linux TPM
spike (question 8).

*(append per phase: date, what was done, deviations from this design, gotchas, test counts, benchmark numbers with the machine)*

### 2026-10-04 — decisions confirmed, core `Hash256`, YFex.Audit.Public aligned

- The user confirmed ⚑K1–K18. **K2 refined:** core holds types only, no implementations ("so logic does not bleed into core").
  Consequences: `Hash256` has value semantics only (no `Sha256` factory). `CanonicalWriter`/`CanonicalReader` and `Hashing` move to
  the library. Core keeps the `ICanonicalValue<TSelf>` contract.
- **Deviation (found while aligning):** offline Phase 0 (2026-10-03) had already fixed F1/F2/C3/C10 inside Persistence.Encryption and
  writes layout `0x02`. The converged format therefore takes version byte `0x03` ("sealed value v3"). Persistence `0x02` becomes
  legacy L1b, and the Web.Data text prefix is `y3.`.
- **Core:** `YFex/src/YFex/Cryptography/Hash256.cs` (InlineArray storage, read/write, equality + `FixedTimeEquals`, lower-case hex
  `ISpanFormattable`/`IUtf8SpanFormattable`/`ISpanParsable`, `"S"` short form, `ILogValue`). Tests in
  `YFex.Tests/Cryptography/Hash256Tests.cs` (5, including the FIPS 180-4 "abc" vector). YFex.Tests: 155 green.
- **YFex.Audit.Public:** `AuditDigest(long Length, Hash256 Sha256)` (was a hex `string`). `AuditValueWriter.WriteDigest` now applies
  the same rule as every other non-text writer: above Internal the value is **erased**. Before, it wrote an unkeyed SHA-256 of
  Personal/SensitivePersonal/Financial/Secret blobs (01 A-1). BCL `SHA256.HashData` stays: the package is a leaf (core + BCL),
  and unkeyed hashing is not reserved to the library. **Generator:** blob members above Internal show `Erased` in the schema
  snapshot (`CapturedBlob`). Tests: Audit.Tests 26 (+1 `Blob_digest_is_a_core_hash_and_only_below_personal`), Generator.Tests 6.
  Backups in `C:\Temp\cryptography-backup\2026-10-04-audit-public`.
- **Follow-ups (not changed):** the generator's secret-looking-name rule covers strings only, so a `byte[] CertificadoPfx` at
  Internal floor is still digested. `AuditIdentity` keeps `StableHash64` for action ids (non-adversarial identity, allowed by ⚑K3).
  Library Phase 0 is not started.

### 2026-10-04 — Phase 0 DONE

**Done (by task):**

- **0.0 goldens** with the code as it was: `YFex.Persistence/tests/YFex.Persistence.Encryption.Tests/Golden/persistence-v2.tsv` (4 values of
  the interim `0x02` layout, key `0xA0…0xBF`, sealed by offline Phase 0's `AesGcmValueProtector`). `0x01` values are rebuilt in tests
  with the BCL (that code no longer existed). Backups of every file touched: `C:\Temp\cryptography-backup\phase0`.
- **0.1 core** (`YFex/src/YFex`): `Cryptography/KeyId.cs`, `Cryptography/Signatures.cs` (`SignatureAlgorithm`, `DetachedSignature`),
  `Canonical/ICanonicalValue.cs` (+ `Hash256` from 2026-10-04 morning). Types only. Tests `YFex.Tests/Cryptography/KeyIdTests.cs`.
  YFex.Tests 158.
- **0.2 repo** `C:\Dev\YFex\YFex.Cryptography` (`cryptography.slnx`, `Directory.Build.props` like YFex.Audit's, TreatWarningsAsErrors).
- **0.3 library** `src/YFex.Cryptography`: `Canonical/CanonicalWriter.cs` + `CanonicalReader.cs` (namespace `YFex.Canonical`,
  `CanonicalFormatException`), `Hashing`, `MessageDomain`, `Keys/` (`KeyPurpose`, `KeyProtection`, `KeyStatus`, `SecretKey` + `KeyLease`
  with pinned zeroed memory and a one-slot `AesGcm` pool, `KeyRingName`, `KeyRingEntry`, `KeyRing`, `KeyRingOptions`,
  `KeyRingProvider` with creation, cache, `ReloadAsync`, rotation by age/seal count, two-phase propagation, `DestroyAsync`,
  `IRootKeyProvider` + `RootKeyFormat` + `ProvidedRootKeyProvider` / `EnvironmentRootKeyProvider` / `FileRootKeyProvider` (0600),
  `IKeyRingStore` + `KeyRingRecord` + `InMemoryKeyRingStore` + `FileKeyRingStore` (store.key + HMAC-tagged `.keyring` files, lock file,
  optimistic revisions) + `KeyRingStoreException`), `Sealing/` (`SealingAlgorithm`, `SealContext`, `SealedValue` v3, `OpenResult` +
  cases + `OpenOutcome`, `LegacyFormats`), `CryptographyCapabilities`, DI `AddYFexCryptography` + `CryptographyBuilder`.
- **0.4** `src/YFex.Cryptography.Windows`: `CngRootKeyProvider` (TPM → software KSP, OAEP-SHA256 round-trip check at creation,
  `DeleteKey`), `UseCngRootKey()`. `src/YFex.Cryptography.Testing`: `KnownAnswerVectors` (GCM test cases 13–16, FIPS 180-4).
- **0.5 Persistence.Encryption** moved onto the library (09 §1 I1–I6, offline 11 §5 addendum): deleted `AesGcmValueProtector`,
  `IValueProtector`/`UnprotectStatus`, `IKeyProvider`, `ProvidedKeyProvider`, `IKeyWrapper`/`KeyProtection`, `CngKeyWrapper`,
  `DataProtectionKeyWrapper`, `WrappedKeyProvider`/`StorageKeyUnavailableException`; rewrote `EncryptedKeyValueStore` (v3 + legacy
  `0x01`/`0x02` with the imported key, `TryGetAsync` → `StoredValueResult`, `GetAsync` throws `StorageIntegrityException`, reload on
  unknown key), `EncryptionServiceExtensions` (options, one-time import of the interim `.storage-key`, keyed inner registration, old
  overload `[Obsolete]`), new `StorageIntegrity.cs`. csproj: DataProtection package removed, Logging.Abstractions added,
  `IsAotCompatible`, project references to the library. Consumers updated: `Messaging.Tests/Encryption/EncryptionTests.cs`,
  `Persistence.Smoke/Program.cs`.
- **0.6 tests:** YFex.Cryptography.Tests 52 (KAT, layout rebuilt with the BCL, every tampered byte, truncation, rotation/destruction,
  16-thread concurrency, canonical encoding, rings in files incl. restart/tamper/wrong root/concurrent creation, DI, architecture test),
  YFex.Cryptography.Windows.Tests 5 (software KSP, real TPM on this machine, interim blob compatibility, ring store over CNG),
  Persistence.Encryption.Tests 15, Messaging.Tests 167 (+2 skipped, pre-existing), YFex.Tests 158, Persistence.Smoke encryption
  scenario 12/12 (the smoke's 3 other failures are pre-existing, in FusionCache/CacheResult/SQLite queue scenarios). **AOT smoke**:
  `dotnet publish -r win-x64` 0 IL warnings, 8/8 checks, exit 0.

**Benchmarks** (BenchmarkDotNet 0.15.8, in-process toolchain, ShortRun; Windows 11 26200, .NET 11 preview 4):

| Operation | Mean | Budget | Allocated |
|---|---:|---:|---:|
| raw `AesGcm.Encrypt` 64 B (baseline) | 204 ns | — | 0 B |
| `SealedValue.Seal` 64 B | 334 ns | ≤ 600 ns ✅ | 0 B |
| `SealedValue.Open` 64 B | 313 ns | ≤ 600 ns ✅ | 0 B |
| raw `SHA256.HashData` 1 KiB (baseline) | 489 ns | — | 0 B |
| `Hashing.Sha256` 1 KiB | 505 ns | ≤ 450 ns ❌ gap | 0 B |
| `KeyRingProvider.TryGetCached` | 18 ns | ≤ 20 ns ✅ (46 ns before caching options and throttling clock reads) | 0 B |

Gap **B-1**: the 450 ns hashing budget came from a `Stopwatch` probe (414 ns); under BenchmarkDotNet the BCL call alone takes 489 ns and
the wrapper adds 16 ns (3 %). Proposed budget: "raw SHA-256 + 5 %".

**Deviations from the design (decide or accept):**

1. Root-provider ids follow offline Phase 0's bytes (1 TPM, 2 software, 3 DataProtection) so its wrapped files stay readable; provided =
   4, file = 5 (05 §7 updated).
2. `IRootKeyProvider.WrapAsync` returns `ValueTask<byte[]>` (no `MaxWrappedLength`); 06 §3 updated.
3. `OpenResult` has a 7th case `Malformed(Length)`; `KeyStatus` has `Unknown = 0`. No `[Union]` polyfill: the net11 BCL ships it.
4. The offline Phase 0 storage key is imported as the ring's first key with a **fixed id** (`EncryptedKeyValueStore.LegacyStorageKeyId`),
   because layouts `0x01`/`0x02` carry no key id; the old `.storage-key` file is left in place. No `LegacyKey(…)` option: the import is
   automatic (`ImportLegacyStorageKey`), an app key is `UseKey(bytes)`.
5. DataProtection-wrapped `.storage-key` files (interim, Linux/macOS only, created 2026-10-03/04) are **not read**: the import fails with
   a clear message. No such deployment is known; say so if one exists and a small adapter package is needed.
6. Default root key names: `YFex.RootKey.<app>` (new); the interim CNG key `YFex.Persistence.StorageKey.<app>` is only used for the
   one-time import. Rings live in `<KeyDirectory>\<app>\`.
7. Not built yet (later phases): `KeepOpenOnlyFor` (automatic retirement; Phase 1 with MAC rings), persisted seal counts (in-memory
   per process now; the 2³¹ bound is far away on devices), core-probe events (07 §8; the Persistence adapter logs through `ILogger`),
   `[Sensitive(Secret)]` on `SecretKey` (needs the Privacy generator reference), `yfex-storage report`, `InMemoryKeyRingStore` lives in
   the library (not `.Testing`) because `UseKey` and tests need it.
8. `CanonicalWriter` refuses non-ASCII text under `InvariantGlobalization=true` (normalization is a no-op there; measured).
9. `KeyRingProvider.TryGetCached` reads the system clock at most once per second per ring; a custom `TimeProvider` is read on every call.

**Found while implementing:** the rotation code first gave a ring entry an id different from its key's own id (caught by the
file-store tests: unwrap failed). Fixed, plus a guard in `FileKeyRingStore.Encode`.

**Next:** Phase 1 (MACs, tokens, derivation, passwords; EventChannel C4, Web.Data C2, Automation C7, Payment C9). The architecture test's
allow-list names the 4 remaining direct uses and their phases.

### 2026-10-04 — Phase 1 library + Phase 2 signing slice (what Authorization needs)

**Why this order:** the user asked to continue Phase 1 and to deliver first what unblocks the Authorization session: Auth Phase 3b
(server-signed permission snapshots with key rotation, authorization 09 Phase 3) and Auth Phase 4 (approval tokens v2 with ⚑Z12, v1
verified byte-identically). User decisions of the day: per-tenant keys are built **generically** (any service, not only Web.Data);
Automation's stores (open question 3) are **not** implemented now; Payment (I18) waits for an explanation (below). Backups:
`C:\Temp\cryptography-backup\phase1` (repo, docs, CLAUDE.md, the golden generator in `goldengen\`).

**Done (library `YFex.Cryptography`):**

- `MessageDomain.Raw` (no separation; legacy/protocol formats only) and `IsRaw`; `default(MessageDomain)` is refused everywhere.
- `Authentication/MessageAuthentication`: HMAC-SHA256 over a per-domain subkey `HKDF-Expand(key, "yfex.mac|" ‖ domain)` cached on the
  `SecretKey` (copy-on-write, ≤ 32 domains, zeroed with the key); tags 16–32 bytes; fixed-time verify; ring verify by key id.
- `Authentication/MessageToken` (05 §6) + `TokenCheck`; `TryReadHeader` (key id + expiry without authenticating, for routing).
- `Keys/KeyDerivation` + `KeyDerivationLabel`: `DeriveKeyMaterial`, `DeriveKey` (deterministic key id, 05 §11), `ExtractAndExpand`,
  `ContextLength`/`WriteContext` (length-prefixed text parts).
- `Keys/ScopedKeyRings` + `ScopedKeyRingOptions` + `CryptographyBuilder.ScopedKeyRings(master, …)`: **generic per-scope keys** (tenant,
  store, customer…). Default = keys derived from a `Derivation` master ring (nothing stored per scope, every node derives the same
  ids); `DedicatedRings("tenant/{scope}/secrets")` + `CreateDedicatedRingAsync` give one scope its own lifecycle without touching
  the others. The returned `KeyRing` merges both, so old values open (`NeedsReseal`) and new ones seal with the scope's current key.
  Registered in DI by `AddYFexCryptography`.
- `KeyRingOptions.KeepOpenOnlyFor` (automatic retirement = destroy keys superseded longer than the period; runs in the background
  with rotation, or explicitly through `KeyRingProvider.RetireExpiredAsync`) and `KeyRingProvider.TryGetExistingAsync` (never creates).
- `Passwords/PasswordHashing` + `PasswordCheck` (05 §4): NFC via the span normalization APIs when the runtime can normalize,
  `MinimumIterations` 10 000 for creation, `MaximumIterations` 10 000 000 for verification (a forged verifier cannot stall a login).
- Phase 2 slice: `KeyPurpose.Signing` (a ring key = ECDsa P-256 private scalar, generated by the platform so it is always < n),
  `Signing/SigningContracts` (`ISigningKey`, `ISigningKeyStore`, `IPublicKeyDirectory`, `PublicKeyRecord` + `KeyIdOf(spki)`,
  `SignatureCheck`), `SignedPayload` (05 §5; `Raw` = `SHA-256(payload)` for v1 device tokens), `SignatureVerifier` (pooled imported
  keys, re-import when a record's key bytes change, curve pinned to P-256, revocation and validity as of `signedAt`),
  `InMemoryPublicKeyDirectory`, **`SigningKeyRing`** (+ `GetSigningKeysAsync`/`TryGetCachedSigningKeys`): rotating server signing
  keys that are a key ring and their own public-key directory. `CanonicalWriter.WriteSignature`/`WriteValidity` and the reader
  counterparts.
- `.Windows`: `CngSigningKeyStore` (persisted ECDsa P-256, `ExportPolicy None`, TPM → software KSP, sign/verify round trip at
  creation, key id = public-key fingerprint). `.Testing`: `InMemorySigningKey`, `InMemorySigningKeyStore`, RFC 4231 / RFC 5869 /
  RFC 7914 vectors.
- **Step 1.0 goldens:** `YFex.Rules/tests/YFex.Rules.Tests/Golden/approval-token-v1.tsv` (L3) generated with the unchanged
  `OverrideTokens.cs` of 2026-09-30: 3 server + 2 device tokens, fixed HMAC key and fixed device scalar. `ApprovalTokenGoldenTests`
  (Rules) proves the current code reproduces it; `ApprovalTokenVersion1GoldenTests` (crypto) proves the library verifies it with
  `MessageDomain.Raw`. The L2 Web.Data golden is **not** generated yet (it belongs to the Web.Data consumer step).
- Architecture test fix: `FindEcosystemRoot` also starts from the source file path, so the scan runs when tests are built with
  `--artifacts-path` outside the repo (before, it could silently skip). It scans and passes.

**Tests:** YFex.Cryptography.Tests 52 → **103** (MAC + spec reconstruction + zero-allocation check, tokens incl. two nodes sharing a
store and rotation, derivation, scoped rings incl. two tenants (C2), dedicated ring, peer pick-up after the check interval, DI,
retirement, passwords, signing incl. revocation/validity/rotation/restart through the file store/16 threads/canonical form, v1
golden), YFex.Cryptography.Windows.Tests 5 → **8** (CNG signing: software KSP non-exportable, delete, **TPM: 22 ms per signature on
this machine**), YFex.Rules.Tests 64 → **65**, Persistence.Encryption.Tests 15 (unchanged, green). **AOT smoke:** 0 IL warnings,
18/18 checks (Phase 0's 8 + MAC, RFC 4231, tokens, scoped rings, NFC verifier, signing ring, rotation, CNG signing).

**Benchmarks** (BenchmarkDotNet 0.15.8, in-process, ShortRun; Windows 11 26200, .NET 11 preview):

| Operation | Mean | Budget (11 §1) | Allocated |
|---|---:|---:|---:|
| raw `HMACSHA256.HashData` 64 B (baseline) | 257 ns | — | 0 B |
| `MessageAuthentication.ComputeTag` 64 B | 252 ns | ≤ 400 ns ✅ | 0 B |
| `MessageAuthentication.VerifyTag` 64 B | 356 ns | — | 0 B |
| `MessageToken.Issue` (19 B payload) | 291 ns | — | 368 B (the string) |
| `MessageToken.Read` (19 B payload) | 369 ns | ≤ 1.5 µs ✅ | 0 B |
| raw `HKDF.Expand` 32 B (baseline) | 380 ns | — | 32 B |
| `KeyDerivation.DeriveKeyMaterial` 32 B | 472 ns | ≤ 900 ns ✅ | 32 B (gap B-2: the BCL call allocates) |
| raw `ECDsa.SignHash` P-256 (baseline) | 111 µs | — | 289 B |
| `SignedPayload.Sign` 256 B, ring key | 117 µs | ≤ 140 µs ✅ | 201 B |
| `SignedPayload.Verify` 256 B, key cached | 100 µs | ≤ 125 µs ✅ | 201 B |

**Deviations from the design (decide or accept):**

1. **Software signing keys are key rings** (`KeyPurpose.Signing`, `SigningKeyRing`) instead of a `FileSigningKeyStore`: a P-256
   private scalar is 32 bytes, so rotation, two-phase propagation to every node, root wrapping, tamper-evident files and retirement
   come from the ring machinery. `ISigningKeyStore` stays for named, non-exportable keys (CNG/TPM, Offline's installation keys).
   ML-DSA will need an algorithm byte per ring entry (ring file version 0x02) when ⚑K10 is revisited.
2. `SignedPayload.Sign` returns `DetachedSignature` with a new array, plus an `int Sign(…, Span<byte>)` overload: a `Span` buffer
   cannot become the `ReadOnlyMemory` inside `DetachedSignature` (04 §5 sketch).
3. Public-key records of a signing ring have **no end of validity** (rotation is not revocation); revocation is the directory's
   `RevokedAt`. Destroying/retiring a signing ring key removes its public key from the ring: export `PublicKeys` to a durable
   directory when old signatures must verify for years.
4. Per-scope keys need **no key class**: the merged ring finds the right key by id (10 §6 proposed key class 1/2 to route).
   A destroyed master key drops its derived keys, so such values open as `KeyUnknown` (not `KeyDestroyed`).
5. `DeriveKey` binds the purpose into `info` (`… ‖ 0x00 ‖ purpose`): one label/context can never yield the same bytes for sealing and
   MACs. `DeriveKeyMaterial` keeps 04 §2's `label ‖ 0x00 ‖ context`. Signing keys are never derived.
6. Passwords: when the runtime cannot normalize (`InvariantGlobalization`), the characters are used as typed instead of throwing
   (a refused login is worse than the rare decomposed-accent mismatch); the canonical writer keeps throwing.
7. No DI registration for `ISigningKeyStore` (owners create the store they need: Offline the CNG one).
8. Not done in this step: `ICanonicalValue<T>` on `Cpf`/`Cnpj`/`Money`/`Quantity`/`ValidityPeriod` (core; the Authorization session is
   editing core files right now — `CanonicalWriter.WriteValidity` covers what Auth needs); `[Sensitive(Secret)]` on `SecretKey`.

**Gaps:** B-2 (`DeriveKeyMaterial` allocates 32 B inside `HKDF.Expand` of this .NET preview; the budget says 0 B). Linux runs (OpenSSL
private-scalar-only import, concurrency) are not executed on this machine (recorded, not measured).

**Handoff to Authorization (Phase 3b and Phase 4):**

```csharp
// server composition root (every node shares the ring store; SQL or file)
services.AddYFexCryptography(cryptography => cryptography
    .UseEnvironmentRootKey()                                      // or UseCngRootKey() on Windows servers
    .StoreKeyRingsInFiles(keysDirectory)
    .KeyRing("authorization/snapshots", ring => ring.For(KeyPurpose.Signing)
        .RotateEvery(TimeSpan.FromDays(365)).PropagateFor(TimeSpan.FromMinutes(10)))
    .KeyRing("approvals/server", ring => ring.For(KeyPurpose.MessageAuthentication)
        .RotateEvery(TimeSpan.FromDays(90)).KeepOpenOnlyFor(TimeSpan.FromDays(1))));

// Phase 3b — sign a snapshot (payload = canonical bytes, never JSON; ValidityPeriod via CanonicalWriter.WriteValidity)
SigningKeyRing keys = await rings.GetSigningKeysAsync(new KeyRingName("authorization/snapshots"), ct);
DetachedSignature signature = SignedPayload.Sign(keys.Current, AuthorizationDomains.Snapshot, payload);   // e.g. "yfex.authorization-snapshot.v1" (Auth owns it)
// client: pin keys.PublicKeys (shipped by the server; trusted through Offline's bundles/CA when that exists) in an InMemoryPublicKeyDirectory
SignatureCheck check = new SignatureVerifier(pinnedServerKeys).Verify(AuthorizationDomains.Snapshot, payload, signature, signedAt: snapshot.IssuedAt);

// Phase 4 — approval tokens v2 (HMAC, online)
KeyRing approvals = await rings.GetAsync(new KeyRingName("approvals/server"), ct);
MessageAuthentication.ComputeTag(approvals.Current, ApprovalDomains.Version2, body, tag);                  // "yfex.approval.v2"
MessageAuthentication.VerifyTag(approvals, token.Key, ApprovalDomains.Version2, body, token.Tag);
// device tokens (offline): SignedPayload with an ISigningKey (CngSigningKeyStore "installation/evidence" until Offline wraps it)
// v1 codec: MessageAuthentication.VerifyTag(v1Key, MessageDomain.Raw, payloadV1, tag) and SignedPayload.Verify(verifier, MessageDomain.Raw, …);
//           golden = YFex.Rules.Tests/Golden/approval-token-v1.tsv (keep ApprovalTokenGoldenTests green after the move)
```

**Payment I18 (explained to the user, not implemented):** today `CreatePaymentValidator` hashes `'|'`-joined text that contains the
payer's CPF with unkeyed SHA-256. Fix: build the fingerprint with `CanonicalWriter` (no field shifting) and, because it is persisted,
compute it with `MessageAuthentication` over a `payments/idempotency` ring (a confirmable CPF hash otherwise). Fingerprints change
once at upgrade.

**Next:** Phase 1 consumers — EventChannel audience tokens (C4, `MessageToken`), Web.Data `SealedSecretProtector` on `ScopedKeyRings`
+ L2 golden (C2, I10/I11), Payment fingerprint (C9, with the Payment owner), Automation (C7) when the user decides; then the rest of
Phase 2 (recipient sealing; approval tokens v2 inside Authorization's Phase 4).

### 2026-10-04 — Phase 1 consumer: Web.Data secrets (C2, I10/I11) DONE

**Findings before changing anything:** no production code in the ecosystem called `ISecretProtector` (only its tests), and
`SqlApiEndpointStore` / `ApiRow` read `Secret`, `Token`, `ApiKey` **as stored** — the documented pattern was "call `Unprotect` in
your synchronous row mapper". So C2 was latent (one key for everyone, `TenantSalt` never applied) and any app that followed the docs
had no location binding. Backups: `C:\Temp\cryptography-backup\phase1-webdata` (+ the L2 generator in `phase1\goldengen`).

**Done:**

- **Step 0 golden L2:** `YFex.Web/src/Web/YFex.Web.Data.Tests/Golden/web-secret-v1.tsv` — 8 values (AES-256 and AES-128 keys
  `0xA0…`) produced by the unchanged `AesSecretProtector` (code of 2026-06-19).
- Library: `LegacyFormats.OpenWebSecretVersion1` + `GetWebSecretPlaintextLength` (L2 = `nonce ‖ ct ‖ tag`, no AAD, 16/24/32-byte keys);
  KAT theory over the GCM vectors without AAD.
- `YFex.Web` contract (`Credentials/ISecretProtector.cs`): `SecretLocation(Tenant?, Table, Column, Row)`, `UnprotectedSecret(Value,
  NeedsReprotect)`, `SecretIntegrityException`; `ProtectAsync`/`UnprotectAsync` as **default interface members** (an old
  implementation throws `NotSupportedException` on protect and reads through its legacy `Unprotect`), so no implementation breaks;
  `Protect(string)`/`Unprotect(string)` `[Obsolete]` (I11).
- `YFex.Web.Data`: `Security/SealedSecretProtector` (+ `SealedSecretProtectionOptions`): `ScopedKeyRings` family `web/secrets` (master
  rotated yearly) with dedicated rings `tenant/{tenant}/secrets`, scope `__global__` for rows without tenant, context
  `yfex.web.secret.v1 [tenant, table, column, row]`, text `"y3." + base64url(sealed value v3)`, one reload on `KeyUnknown` (peer
  rotated), `LegacyKey` reads L2 with `NeedsReprotect`. `AddYFexSecretProtection(…)`. `AesSecretProtector`, `SecretProtectionOptions`
  (+ `TenantSalt`), `AddAesSecretProtection` `[Obsolete]`. Project reference to YFex.Cryptography.
- Store wiring (additive): `SqlApiEndpointStore` constructor with an **async** mapper; `AddApiEndpoints<TRow>(table, (sp, row, ct) =>
  …)`; `ApiRow.ToEndpointAsync(row, protector, table)` / `ApiRow.ProtectAsync` / `ApiRow.LocationOf`; `AddProtectedApiEndpoints(table)`
  (opt-in: refuses plain-text columns). The existing overloads behave exactly as before.
- Docs: YFex.Web 01, 05 §5.5, 09, 10, 11 rewritten for the new API. Architecture allow-list reason for `AesSecretProtector` updated
  (obsolete, kept one release).

**Acceptance (Phase 1, Web.Data part):** two tenants' secrets use different derived keys and do not open across tenants ✅; an L2
secret opens and is re-protected with `y3.` ✅ (all 8 goldens); a value moved to another row/column/table/tenant fails ✅; master
rotation and a dedicated tenant ring keep old values readable with `NeedsReprotect` ✅; a second node sharing the store reads ✅.

**Tests:** YFex.Web.Data.Tests 30 → **46** (`SealedSecretProtectorTests` 11, `ProtectedApiRowTests` 5), YFex.Web.Tests 32, Refit 14,
Generator 1 (unchanged, green), YFex.Cryptography.Tests 103 → **107** (L2 reader KAT ×4).

**Decision (user, 2026-10-04): (a) a one-off migration command**, never a transitional "accept plain text" mode (it would keep a
database writer able to plant credentials). No database uses the endpoint table yet. Built the same day:
`Security/ApiEndpointSecretMigration` (registered by `AddYFexSecretProtection`): `ProtectAsync(table, dryRun, …)` reads `Secret`,
`Token`, `ApiKey`; `y3.` under the current key → untouched, under an older key → re-sealed, not opening → `Invalid` (never
overwritten); no prefix → legacy `AesSecretProtector` value when it authenticates with `LegacyKey`, else plain text → sealed.
Updates are conditional on the cell still holding the value read (`ChangedConcurrently` otherwise); idempotent; the report never
holds secret values. Also the re-seal sweep after a dedicated ring or a master rotation (10 §6 `ResealAsync`). Tests on a real SQLite
table (`ApiEndpointSecretMigrationTests`, 5, incl. the end-to-end read through `AddProtectedApiEndpoints`): Web.Data.Tests **51**.

### 2026-10-04 — Phase 1 consumer: EventChannel audience tokens (C4, I7) DONE

**Findings:** `EventChannelHost` is not registered anywhere yet (`EnableServerPushedEvents()` sets a flag nothing reads; the Jobs
push-channel interfaces have no implementation; `FusionEventStream` is never constructed). Only `EventChannelAudienceTests` used it,
so the change carries no deployment risk. Coordination: the Authorization session was in Phase 4a (Rules typed `Permission`) at
the same time and edits `RpcResultFormatters`, `RulesStage`, Rules* and `Messaging.Tests/Fixtures`; this change touches only
`EventChannel.cs`, `YFex.Messaging.Rpc.csproj` and `EventChannelAudienceTests.cs` (agreed by message). Backups:
`C:\Temp\cryptography-backup\phase1-eventchannel`.

**Done:**

- Tokens are `MessageToken` v1 (05 §6), domain `yfex.event-audience.v1`, payload = UTF-8 scope text, expiry
  `EventChannelOptions.AudienceTokenLifetime` (default 12 h). An expired, foreign, old-format or forged token reads as "no
  audience" (global events only). On `UnknownKey` the host reloads the shared ring once (peer rotated) and retries.
- Keys: ring `EventChannelOptions.KeyRing` (default `messaging/event-audience`) from the app's `KeyRingProvider` **when configured**
  (`KeyRingProvider.IsConfigured`, new) — shared by every node, rotating; otherwise a key generated per process (the old
  zero-configuration behaviour). `CryptographyBuilder.EventAudienceTokens()` configures the ring: MAC keys, rotate every 30 days,
  propagate 10 min, keep one token lifetime + 10 min (`KeepOpenOnlyFor`).
- `EventChannelOptions.SigningKey` `[Obsolete]`, default **null** (was a random array); when set it still works as a fixed shared key
  with a deterministic key id (`MessageAuthentication` over domain `yfex.event-audience.key-id.v1`).
- Client `FusionEventStream`: re-fetches the token when 90 % of its remaining lifetime has passed (`MessageToken.TryReadHeader`),
  re-subscribes with the new filter, retries after 1 min when the server is unreachable. Not covered by a test: the class is
  internal and never constructed yet.
- Architecture allow-list: `EventChannel.cs` entry removed (no direct HMAC left; the scan passes).

**Tests:** EventChannelAudienceTests 4 → **8** (expiry, two nodes sharing a ring incl. rotation on one node, old-format token
refused, obsolete fixed key across nodes); run green in an isolated project referencing Messaging.Rpc, because the full
Messaging.Tests project did not compile at that moment (Authorization Phase 4a in progress: `RulesStageTests`, `CheckoutAggregate`).
After Phase 4a compiled: **YFex.Messaging.Tests 173 passed (+2 skipped, pre-existing)**. **YFex.Messaging.Rpc.Tests 60 passed** (after that session fixed its `WireFormatTests`). YFex.Cryptography.Tests 107.

**Acceptance (Phase 1, C4 part):** an audience token expires and is refused after expiry ✅; tokens verify on a second node sharing
the ring ✅ (also after a rotation on the issuing node).

### 2026-10-05 — Phase 1 consumer: Payment idempotency fingerprint (C9, I18) — written, not wired

**User direction (same day, after it was written):** YFex.Payment is out of scope. It will be rewritten completely, so nothing else is
changed there, and its build errors are not ours to fix. The code below stays as written.

**Findings:** `CreatePaymentValidator.Fingerprint` had **no callers**. `PaymentRecord` is never constructed. The repo does **not
compile** (since 2026-09-28): `AddYFexPayments` registers `CreatePaymentHandler`, `PaymentService`, `PaymentOperationsHandler`,
`WebhookHandler`, `PaymentQueriesHandler` and `PaymentReconciler`, and none of them exist. `tests/YFex.Payment.Tests` was empty. So no
unkeyed CPF hash was ever persisted.

**Written** (backups: `C:\Temp\cryptography-backup\phase1-payment`):
- `Internal/PaymentRequestFingerprints.cs`: the same fields as before, encoded with `CanonicalWriter`.
  - Free text is UTF-8 as typed, not NFC, so an `InvariantGlobalization` server does not refuse a payment because of an accent.
  - Keyed by `MessageAuthentication`, ring `payments/idempotency`, domain `yfex.payment-request.v1`.
  - Text form `pf1.` + base64url(key id ‖ tag), 68 characters.
  - `CompareAsync` returns `Same`/`Different`/`Unverifiable`. Unverifiable (destroyed key or foreign format) means a conflict. It
    reloads once on an unknown key.
- The unkeyed `Fingerprint` is deleted.
- `AddYFexPayments` adds the ring (MAC, yearly rotation, old keys kept) to the shared crypto builder.
- Project reference to the library added, plus a test project `tests/YFex.Payment.Tests` (csproj + 10 tests).
- **Tests: 10/10**, run in an isolated project (`C:\Temp\paytest`) that compiles Payment.Public + the two files. The golden
  fingerprint was computed independently in Python (`hmac`/`hashlib`).
- **Gotcha:** on this .NET 11 preview, `Base64Url.TryDecodeFromChars` **throws** `FormatException` on characters outside the alphabet
  (it does not return false). The library's own parsers already use the `OperationStatus` overloads, so they are not affected.

### 2026-10-05 — Phase 2: recipient sealing (ECIES) DONE

Backups: `C:\Temp\cryptography-backup\phase2-recipient` (repo src/tests/benchmarks, README, docs).

**Done:**
- `KeyPurpose.KeyAgreement = 6`: a 32-byte ECDH P-256 private scalar, generated by the platform like signing keys. It is never derived:
  `KeyDerivation.DeriveKey` refuses it, and so does `SecretKey.IsCurveScalar`.
- `Sealing/RecipientSealing.cs`:
  - Types: `RecipientSealingAlgorithm` (`0x01`; `0x11` reserved), `RecipientPublicKey`, `IRecipientKey`, `RecipientSealedHeader`.
  - `RecipientSealing`: `GetSealedLength`, `GetPlaintextLength`, `Seal` (span + array), `TryReadHeader`, `Open` (key or ring).
  - Format, HKDF salt/info and AAD are exactly 05 §3.
  - Ephemeral points are checked against the curve equation before import, the same on every platform.
  - Test seam `SealWithFixedEphemeralForTests` (internal, a parameter, no static state).
- `Sealing/RecipientKeyRing.cs`: `RecipientKeyRing.For(ring)`, `Current`, `PublicKeys`, `TryGetRecipientKey`, one pooled ECDH
  instance per key, `GetRecipientKeysAsync` / `TryGetCachedRecipientKeys`.
- `CryptographyCapabilities.IsSupported(RecipientSealingAlgorithm)` (false in the browser and for the reserved hybrid).
- `.Testing`: `KnownAnswerVectors.EcdhP256` (NIST CAVS ECC CDH P-256 COUNT 0).
- AotSmoke: 2 new checks. Benchmarks: `RecipientSealingBenchmarks`.

**Tests:** YFex.Cryptography.Tests 107 → **120** (`RecipientSealingTests`, 13):
- NIST CDH KAT.
- Independent golden.
- Round trip; empty and 70 KB values; context above the stack limit.
- Every flipped byte has its exact outcome (0 → `UnsupportedFormat`, 1 → `Malformed`, key id → `KeyUnknown`, anything else →
  `AuthenticationFailed`) and every truncation fails, with a zeroed plaintext buffer.
- An off-curve ephemeral point gives `AuthenticationFailed`.
- Moved column, tenant or recipient.
- `SealedValue` and recipient values are never confused.
- Rotation (`NeedsReseal`) and destruction (`KeyDestroyed`); a destroyed key held directly.
- File store across a restart.
- 16 threads.
- Programmer errors.

Windows.Tests 8 (unchanged, green). **AOT smoke:** 0 IL warnings, **20/20**, exit 0.

**Benchmarks** (BenchmarkDotNet 0.15.8, in-process, ShortRun; Windows 11 26200, .NET 11 preview):

| Operation | Mean | Budget (11 §1) | Allocated |
|---|---:|---:|---:|
| raw ephemeral ECDH P-256 + `DeriveRawSecretAgreement` (baseline) | 1 128 µs | — | 1.82 KB |
| `RecipientSealing.Seal` 11 B | 1 169 µs | ≤ 1.4 ms ✅ | 1.91 KB (≤ 2 KB ✅) |
| `RecipientSealing.Open` 11 B (ring key) | 929 µs | informational (server) | 2.64 KB |

The first version measured 1 658 µs / 3.76 KB. Re-importing the recipient public key on every seal cost about 0.5 ms (CNG). The sealer
now keeps the last imported recipient key per thread (`[ThreadStatic]`, keyed by key id + point bytes).

**Deviations from the design (decide or accept):**
1. `IRecipientKey.TryDeriveSharedSecret` returns `bool` (the sketch had `void DeriveSharedSecret`), and `IRecipientKey` gained `PublicKey`.
2. Software recipient keys are **rings** (`KeyPurpose.KeyAgreement`, `RecipientKeyRing`), like signing keys (Phase 1 deviation 1).
   There is no separate "software `IRecipientKey`" type. A CNG/TPM-held recipient key is not built: nothing needs it yet, because the
   intake key lives on the server's ring store.
3. The reserved algorithm `0x11` opens as `Malformed`, not `AlgorithmUnavailable`. That case carries a `SealingAlgorithm`, and adding
   a recipient variant to `OpenResult` waits until Phase 5 implements it.
4. `RecipientPublicKey` has no canonical writer or text form yet. Offline's manifest/bundle format (Phase 3 + Offline) decides how it
   travels: `CanonicalWriter.WriteKeyId` + a tag + `WriteBytes` is enough.

**Gotcha:** Windows CNG rejects an off-curve point with `PlatformNotSupportedException("…curve 'nistP256' or its parameters are not
valid…")`, not `CryptographicException`. The library checks the equation itself first and catches both.

**Answer to Authorization (their 09 "Deferred until YFex.Cryptography"):** the FNV-1a claims hash in `AuthorizationContextCache` is a
**cache key**. It is not a value that is signed, MACed or checked to grant access (⚑K3 is about those), and subject, scope and
version are separate parts of the key. It is out of the architecture test's scope and may stay. If it is ever persisted or shared
across users, switch to `Hashing.Sha256` over canonical claims.

**Phase 2 acceptance status:**
- ✅ v1 golden payloads byte-identical and v1 signatures verify (2026-10-04).
- ✅ Device-token verify ≤ 125 µs with a cached key (100 µs).
- ✅ Recipient-seal golden and negative cases.
- ✅ CNG signing key non-exportable; TPM test ran.
- ✅ AotSmoke covers signing and recipient sealing.
- ⏳ "v2 tokens fail on a changed decision digest, wrong domain or revoked key" belongs to **Authorization Phase 4b** (not started).

**Next:** Phase 3 (certificates, hash chains, Merkle trees), which Offline (CA, bundles) and Audit (chains, anchors) need. In parallel:
- Authorization Phase 4b (approval tokens v2).
- Privacy Tier 2 / Offline adoption of recipient sealing.
- Core `ICanonicalValue<T>` on Cpf/Cnpj/Money/Quantity/ValidityPeriod (pending since Phase 1).

### 2026-10-05 — Phase 3: certificates, hash chains, Merkle trees DONE

Backups: `C:\Temp\cryptography-backup\phase2-recipient` (taken before the session's changes; Phase 3 only **added** files, except
`CngSigningKeyStore.cs`, `KnownAnswerVectors.cs`, the AotSmoke and benchmark `Program.cs`, which are also in that backup).

**Done:**
- `Evidence/HashChain.cs`: `Genesis`, `Next`, `Replay`. Formulas in 05 §12. Stack buffer up to 4 KiB, pooled above. `Raw`/default
  domains are refused.
- `Evidence/MerkleTree.cs`: `LeafHash`, `NodeHash`, `ComputeRoot` (empty tree = `SHA-256("")`), `GetInclusionProofLength`,
  `WriteInclusionProof` (RFC 6962 PATH), `VerifyInclusion` (RFC 9162 §2.1.3.2, never throws), `MaximumProofLength`.
- `Certificates/CertificateAuthority.cs`: `CertificateUsage`, `CertificateIssueRequest` (+ `FromSigningRequest`),
  `CertificateAuthority.CreateRoot` / `Issue`, and an internal `X509SignatureGenerator` over `ISigningKey`.
- `Certificates/CertificateTrust.cs`: `CertificateTrust.Validate` (custom root trust, no downloads, the caller's time and revocation
  list, required TLS usage). Native `union CertificateCheck(CertificateTrusted, CertificateUntrusted, CertificateExpired,
  CertificateRevoked)`. `CertificateFiles` (`Load`, `LoadPkcs12`, `ExportPkcs12`).
- `.Windows`: `CngSigningKeyStore.CopyWithPrivateKey(certificate, keyName)`.
- `.Testing`: `KnownAnswerVectors.MerkleTreeRfc6962`.

**Independent vectors:** stdlib Python (`C:\Temp\yfex-edits\merkle.py`) reproduces all 8 RFC 6962 reference roots. It produced the
6 inclusion proofs and the 3 chain goldens that the tests pin.

**Tests:** YFex.Cryptography.Tests 120 → **138**.
- `EvidenceTests` (7): chain goldens incl. the pooled path; every input of a chain hash matters; RFC 6962 roots; 6 independent
  proofs; proofs for **every tree of 1…1 025 leaves** (all indices up to 64 leaves, 5 per size above), each refused for another
  index, another leaf and the next size's real root; tampered, short, long, wrong-root and out-of-range proofs.
- `CertificateTests` (7):
  - Offline's 4-level hierarchy (root → installation issuing CA → site CA → terminal from a CSR) with URN SANs, EKU, AKI = issuer
    SKI, a 16-byte serial, backdating and clamping.
  - Expired, not yet valid, revoked (revoking the site CA revokes its terminals), foreign root with the same name, missing
    intermediate, wrong TLS role.
  - Path length and usage rules; a tampered CSR is refused.
  - A signing **ring** key as CA, with its DER signature verified by the BCL.
  - PKCS#12 round trip: PBES2, no 3DES/RC2 OIDs, wrong password refused, DER and PEM load.

YFex.Cryptography.Windows.Tests 8 → **10**:
- A **TPM-held root CA key** (protection `Hardware` on this machine) issues a hierarchy that validates.
- **Mutual TLS 1.3** over `SslStream` between a "site broker" and a "terminal". Both keys are non-exportable CNG keys attached with
  `CopyWithPrivateKey`. Each side validates the other only through `CertificateTrust` with the required role.

**AOT smoke:** 0 IL warnings, **24/24** (chain replay, Merkle proof, CA + trust, PKCS#12).

**Benchmarks** (BenchmarkDotNet 0.15.8, in-process, ShortRun; Windows 11 26200, .NET 11 preview):

| Operation | Mean | Budget | Allocated |
|---|---:|---:|---:|
| raw `SHA256.HashData` over the same 1 084 bytes (baseline) | 517 ns | — | 0 B |
| `HashChain.Next` 1 KiB entry | 578 ns | ≤ 600 ns ✅ | 0 B |
| `MerkleTree.LeafHash` 1 KiB entry | 556 ns | — | 0 B |
| `MerkleTree.ComputeRoot` 100 000 leaves (a busy day seal) | 19.7 ms | informational | 0 B |
| `MerkleTree.WriteInclusionProof`, 100 000 leaves | 19.8 ms | informational | 0 B |
| `MerkleTree.VerifyInclusion`, 100 000 leaves (17 hashes) | 3.4 µs | informational | 0 B |

`WriteInclusionProof` recomputes the subtrees it needs (about one root's worth). That is fine for one export. If Audit exports many
proofs of the same day at once, add a builder that keeps the tree levels (about 6 MB for 100 000 leaves). Not built until Audit asks.

**Deviations from the design (decide or accept):**
1. `CertificateTrusted` holds `ImmutableArray<X509Certificate2>` copies instead of `X509ChainElementCollection`, which is disposed
   with the chain. `CertificateExpired` and `CertificateRevoked` name the certificate (subject; serial for revoked).
2. Issuers are ECDsa P-256 only. Offline 03 §3 wants a P-384 root: open question 6.
3. `Validate` has `requiredUsage` (TLS role). Revocation is the caller's callback, called for every certificate of a built chain.
4. Added `CertificateFiles`, `CertificateIssueRequest.FromSigningRequest`, `CngSigningKeyStore.CopyWithPrivateKey`,
   `HashChain.Replay`, `MerkleTree.GetInclusionProofLength`/`MaximumProofLength`.
5. No consistency proofs and no tree-level cache (no consumer yet).

**Gotchas:**
- Under FluentAssertions, a native union value is the union wrapper: assert on `.Value`.
- `AsnReader.ReadSequence(tag)` refuses universal tags other than SEQUENCE: use `ReadSetOf` for SET.
- `CertificateRequest.LoadSigningRequest` takes the hash algorithm **before** `out bytesConsumed`.
- In the AOT smoke, `leaf.CopyWithPrivateKey(ecdsa)` resolved to the `CompositeMLDsa` overload. Call
  `ECDsaCertificateExtensions.CopyWithPrivateKey` explicitly.

**Package check for open question 5:** the cached `System.Security.Cryptography.Xml` 11.0.0-preview.5 still carries
`RequiresUnreferencedCode`/`RequiresDynamicCode`. Its messages say "CreateDigest is not trim compatible…", "CreateFormatter is not
trim compatible…", "XmlDsigXsltTransform … requires dynamic code".

**Phase 3 acceptance:**
- ✅ root → issuing CA → leaf with URN SANs, built with a TPM-held CA key and validated with custom root trust.
- ✅ The OS store is ignored (custom root trust, downloads disabled; a same-name foreign root is refused).
- ✅ Expired and revoked cases.
- ✅ Merkle inclusion proofs for 1…1 025 leaves.
- ✅ Chain genesis/next goldens.

**What is left of this design:**
- **Phase 4 (adoption, owned by the other designs):**
  - Offline 1–3: `CngSigningKeyStore` installation keys, tenant CA + mTLS, bundles, envelopes, installation sequence.
  - Audit: chains, anchors, day seals with `MerkleTree` (sign root + count).
  - Privacy Tier 2: intake sealing on `RecipientSealing`, pseudonyms on `MessageAuthentication`/`KeyDerivation`.
  - Authorization 4b: approval tokens v2, then `OverrideTokens` leaves the allow-list.
- **Phase 5:** hybrid ML-KEM `0x11`, the ChaCha20 path, Android/libsecret providers, WASM.
- **Small items:**
  - Core `ICanonicalValue` on Cpf/Cnpj/Money/Quantity/ValidityPeriod.
  - `[Sensitive(Secret)]` on `SecretKey`.
  - Linux runs.
  - Open questions 1, 3, 5, 6; Wycheproof (⚑K14).

### 2026-10-05 — `YFex.Cryptography.Xml` (open question 5) and the P-256 root (open question 6)

**Decisions (user):**
- The tenant root CA is P-256, so the library CA stays P-256 only.
- Create `YFex.Cryptography.Xml`.
- The user also asked where certificate handling lives and whether certificates can be "online". Answer: 13 §5–§6. The
  certificate-source layer is open question 7.

**Done:**
- `src/YFex.Cryptography.Xml` (BCL only, `IsAotCompatible`):
  - `XmlCanonicalization`: inclusive C14N 1.0 without comments, over `XmlDocument` (keeps prefixes); element subtree in context, or
    the whole document.
  - `XmlSignature`: `Load` (DTD/XXE refused), `SignEnveloped`, `Verify` (strict profile, unique-Id resolution, native union
    `XmlSignatureCheck`).
  - `XmlSignatureAlgorithm` (`RsaSha1` / `RsaSha256`) and `XmlSignatureFailure`.
- Added to `cryptography.slnx`.
- Architecture test: `new SignedXml(` banned.
- AotSmoke references the package: **26/26**, 0 IL warnings.

**Tests:** `tests/YFex.Cryptography.Xml.Tests`, **16**.
- Inputs and goldens are in `Golden/`: 4 documents, a test RSA key, a self-signed test certificate, and `signedxml-oracle.tsv`.
- Byte-identical DigestValue + SignatureValue against SignedXml (4 cases); SignedXml signatures verify here (4).
- NF-e round trip through text, with the `Signature` placed after `infNFe`.
- Explicit canonical forms: apex namespaces and `xml:lang` inherited; prefix rules; every escape.
- The spec-vs-SignedXml tab/CR case.
- Tampering with the right reason (digest, signature, digest method, C14N with comments, XPath transform, `Object`, unknown URI,
  missing KeyInfo, algorithm not allowed, another certificate).
- A signature-wrapping attack (duplicate Id).
- A remote signer (`RSA` subclass) gives the identical signature with one call.
- Re-indentation breaks the signature.
- Programmer errors and XXE.

YFex.Cryptography.Tests 138 (architecture test incl. the new ban).

**Oracle method:** `C:\Temp\xmloracle` (signer) and `C:\Temp\xmlbisect` (feature bisection). Both use the cached
`System.Security.Cryptography.Xml` 11.0.0-preview.5. Bisection showed every construct matches except the tab/CR normalization in
SignedXml (13 §4).

**Gotchas:**
- `ExportSubjectPublicKeyInfo()` on an `RSA` subclass without `KeySize` hangs (13 §4): remote signers must set `KeySizeValue`.
- `SignedXml` writes `<Transform … />` with a space, so string-based tamper tests must edit the DOM.
- Long bash heredocs break quoting and turn escapes into real characters: write scripts with the Write tool.

**Not built (proposal 13 §6, open question 7):**
- signing-certificate sources (PKCS#12 from sealed storage, Windows store/A3, remote providers);
- `YFex.Cryptography.IcpBrasil` (CNPJ/CPF OIDs, root set, LCR);
- a CNG-held `IRecipientKey` for delivering an A1 to one installation.

### 2026-10-05 — Handoff to Offline (what the library gives Offline Phases 1–4)

The user asked for instructions the Offline session can act on. The library side of everything Offline needs from cryptography is done:

| Offline need (offline 11 §2) | Use | Notes |
|---|---|---|
| installation keys (transport, evidence) | `YFex.Cryptography.Windows.CngSigningKeyStore` — `GetOrCreateAsync("installation/transport")` / `("installation/evidence")`, TPM first, `ExportPolicy None` | `IInstallationKeyStore` stays a thin typed layer in YFex.Offline. `KeyId` = SHA-256(SPKI)[0..16) |
| TLS certificate with the non-exportable key | `CngSigningKeyStore.CopyWithPrivateKey(certificate, "installation/transport")` | tested: mutual TLS 1.3 over `SslStream` |
| tenant CA (central) | `CertificateAuthority.CreateRoot(rootKey, subject, 10 years, pathLength: 2)`, then `Issue(...)` for the installation issuing CA (path 1), the site CA (path 0) and installation certificates via `CertificateIssueRequest.FromSigningRequest(csr, subject, [urn:yfex:installation:…, urn:yfex:site:…, urn:yfex:role:…], usage, lifetime)` | **P-256 root** (user 2026-10-05). The root key = a `SigningKeyRing` key on central servers (`ring.For(KeyPurpose.Signing)`) or a CNG/TPM key. Lifetimes clamp to the issuer |
| peer validation (broker ↔ terminal) | `new CertificateTrust([tenantRoot]).Validate(peer, intermediates, now, isRevoked: serial in the revocation bundle, requiredUsage: Client/Server)` → `CertificateTrusted` / `Untrusted` / `Expired` / `Revoked` | OS store ignored, no downloads. Read the URN SANs from the trusted leaf |
| bundles (manifest, revocations, snapshots, rule data) | `SigningKeyRing` (central, rotating) + `SignedPayload.Sign` / `SignatureVerifier(IPublicKeyDirectory)`; payload = `CanonicalWriter` bytes; domains `yfex.bundle.v1`, … (05 §9) | verify as of `signedAt` |
| envelopes, receipts, installation assertion | `SignedPayload` with the evidence `ISigningKey` (the TPM signs **batches/heads**, about 22–32 ms per signature) | — |
| installation sequence | `HashChain.Genesis/Next` (05 §12) | — |
| personal data typed offline (customer CPF) | `RecipientSealing.Seal(in intakePublicKey, …)` on the terminal, `RecipientKeyRing` per tenant at central (Privacy owns the meaning) | the intake public key travels in the signed manifest |
| offline login verifiers | `PasswordHashing` (PBKDF2, 05 §4) | — |
| storage at rest | `AddYFexStorageEncryption` (on the library since Phase 0) | — |
| trusted time | `YFex.Time` (separate design); the library takes any `TimeProvider` | — |
| NFC-e signed offline at the checkout (Fiscal) | `YFex.Cryptography.Xml` with a **local** key (13 §5) | delivering an A1 to one installation needs a CNG key-agreement key (13 §6, not built) |

**Offline docs to amend (by the Offline session):**
- 03 §3: the root is ECDsa **P-256** (was P-384).
- 03 §2: the CNG key names above.
- 11 §2: mark the cryptography rows done (Phases 0–3 ✅).
- 11 §3: Phase 1 uses `CngSigningKeyStore` directly (no own CNG code); Phase 2 uses `CertificateAuthority`/`CertificateTrust`/`SigningKeyRing`.
- README: status line.

### 2026-10-14 — `ICertificateKeyStore` (additive; by the YFex.Installations I3 session, no cryptography session was running)

Installations' peer endpoint must attach an installation's transport key to its certificate for TLS **through the key store** on every
OS, and nothing outside this library may create ECDsa keys (architecture test). Added, additive only:
- `ICertificateKeyStore : ISigningKeyStore` (`Signing/SigningContracts.cs`): `ValueTask<X509Certificate2> CopyWithPrivateKeyAsync(certificate,
  keyName, cancellationToken)` — the async shape crypto 14 §2.1 designed for `FileSigningKeyStore`, which should implement it when built.
- `CngSigningKeyStore` implements it (wraps the existing synchronous `CopyWithPrivateKey`).
- `InMemorySigningKey.CopyWithPrivateKey(certificate)` and `InMemorySigningKeyStore : ICertificateKeyStore` (.Testing): on Windows the
  PKCS#12 round trip of 14 §2.5 (Schannel refuses ephemeral keys); elsewhere the direct `CopyWithPrivateKey`.
- Test `CertificateKeyStoreTests` (mutual TLS over loopback with two in-memory stores; wrong certificate → `ArgumentException`; missing key
  → `InvalidOperationException`). Cryptography tests 139 (+1) / Windows 10 / Xml 16 green; `ArchitectureTests` unchanged and green, no
  allow-list entry. Backups: `C:\Temp\installations-backup\I3\YFex.Cryptography`.
- **Finding for 14 §2.5 / §6 (measured 2026-10-14, Windows 11, TPM 2.0):** a user-scope **TPM** key (Platform Crypto Provider) works as a TLS
  **client** credential but Schannel refuses it as a **server** credential (`SEC_E_UNKNOWN_CREDENTIALS`, TLS 1.3 and 1.2). The earlier
  "mutual TLS with CNG keys" test used software KSP keys (`preferHardware: false`). Machine-scope TPM keys are not verified (needs an
  elevated run). Details: installations 09 §4 R6, ⚑I25.
- **Finding (Windows):** `SslStreamCertificateContext.Create(certificate, additionalCertificates)` adds the intermediates to the user's CA
  store. Tests that mint a new same-named CA per run filled `CurrentUser\CA` until `X509Chain.Build` threw "unknown chain building error" for
  every certificate with that issuer name. Installations no longer passes intermediates; test CAs here and in Installations should carry a
  unique name.
- **Measured cost:** `CertificateTrust.Validate` of one installation certificate ≈ 20 ms on Windows (`X509Chain` with a custom root,
  BenchmarkDotNet in-process; unchanged after removing same-named stale CAs from the lookup), the largest part of the peer handshake
  (≈ 31 ms for both sides + a request). A validated-chain cache keyed by the leaf is a
  possible optimization for this library (not done: security-sensitive, not needed for the I3 budget).

### 2026-10-07 — Platform signing-key stores: evaluation and design (14) — nothing coded

Requested by installations 09 §5: YFex.Installations must run on Windows, Linux and macOS (user requirement 2026-10-07). Its named keys
`"installation/transport"` and `"installation/evidence"` have a persisted store only on Windows (`CngSigningKeyStore`).

**User decisions 2026-10-07:**
- `FileSigningKeyStore` as proposed, plus the `FileKeyRingStore` permission fix.
- The Apple store is a later spike in `YFex.Cryptography.Apple`.
- Tests on Windows now, Linux later.
- **"For all this plan, I just want it designed, do not change anything in code yet"**, so this entry is design only. Backup of the
  docs and memory before the edits: `C:\Temp\cryptography-backup\platform-key-stores-design`.

**Design (14):**
- §2 `FileSigningKeyStore`: file format `yfex.signing-key-wrap.v1` (05 §7, §9), Unix 0600/0700, first writer wins, always
  `KeyProtection.File`, `CopyWithPrivateKeyAsync`, `StoreSigningKeysInFiles`.
- §3 F-P1 fix, through a shared internal `ProtectedFiles` helper.
- §5 Apple evaluation.
- §6 Linux TPM note.
- §7 answers to installations 09 §5 and R1–R3.

**Measured / found:**
- **Windows probe** (`C:\Temp\fsksprobe`, .NET 11 preview 4, Windows 11, Schannel), mutual TLS 1.3 over loopback `SslStream`:
  - certificate + `CopyWithPrivateKey(software ECDsa from PKCS#8)` → handshake **fails** (unexpected EOF: Schannel refuses ephemeral keys);
  - the same certificate after a PKCS#12 round trip (`X509CertificateLoader.LoadPkcs12`, `DefaultKeySet`) → **`Tls13`**, client
    certificate received.
  - So `CopyWithPrivateKeyAsync` does the round trip on Windows only.
- **F-P1:** `FileKeyRingStore` writes `store.key`, `.keyring` files and its directory without Unix modes (0644/0755 under the default
  umask). Contents are sealed, but the files break the 0600 rule and expose ring names, revisions and statuses. `FileRootKeyProvider`
  already uses 0600.
- **05 §7 doc drift corrected:** the store key is one `store.key` file per directory, not a field of every ring file.
- **macOS TLS (desk research):**
  - `SslStream` uses SecureTransport by default (TLS 1.2).
  - Client TLS 1.3 is opt-in since .NET 10 (`System.Net.Security.UseNetworkFramework`, dotnet/runtime#117428).
  - Server TLS 1.3 is not in .NET 11: dotnet/runtime#134260 is open, milestone 12.0.
  - So a macOS **listener** falls under installations ⚑I22's TLS 1.2 fallback.
  - Whether a Secure Enclave key works in `SslStream` is unverified (spike 14 §5.4).
- **Linux TPM (desk research):** the BCL `SafeEvpPKeyHandle.OpenKeyFromProvider` (since .NET 9) with `tpm2-openssl` gives an
  `ECDsaOpenSsl` usable for signing **and** TLS. The runtime has TLS tests with TPM keys (dotnet/runtime#113229). The .NET 9 handshake
  failures (#111251) came from loading `tpm2` without the `default` provider, which the multi-provider overload (#113118) fixes. Open
  question 8 asks to approve the native dependency.
- **No Linux environment** on the development PC (no Docker, no WSL): the Unix-mode and direct-`CopyWithPrivateKey` tests are recorded
  as pending a Linux run.

**Docs touched:**
- 14 (new)
- 05 §7, §9
- 06 §4, §5
- 12 §3 Phase 5, §4 questions 7–8
- README (status, files)
- installations 04 §1, 08, 09 §2, §4, §5

**Next (when the user says go):** build 14 §2–§3 with the acceptance list in 14 §2.6; then the Linux run; then the `.Apple` spike on a Mac.
