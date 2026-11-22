# YFex.Cryptography — one cryptography library for the ecosystem — Analysis and design

> **Status:** APPROVED 2026-10-04: the user confirmed ⚑K1–K18, with K2 refined to **types only in core, no implementations**
> ("so logic does not bleed into core"). Done: core `Hash256` and the YFex.Audit.Public alignment (log in 12 §5). Offline Phase 0
> (2026-10-03) had already fixed F1, F2, C3 and C10 inside Persistence.Encryption and writes a `0x02` layout, so the converged
> format is **sealed value v3** (version byte `0x03`, 05 §1). **Phase 0 DONE 2026-10-04** (repo `C:\Dev\YFex\YFex.Cryptography`,
> Persistence.Encryption moved onto it; log in 12 §5). **Phase 1 library DONE 2026-10-04** (MACs, message tokens, key derivation,
> generic per-scope/tenant key rings, key retirement, password verifiers) **+ the Phase 2 signing slice Authorization needs** (signing
> key rings, `SignedPayload`, `SignatureVerifier`, CNG/TPM signing store, approval token v1 golden). Phase 1 consumers: Web.Data (C2)
> and EventChannel (C4) done; Payment (C9) written, not wired (Payment will be rewritten); Automation (C7) deferred by the user.
> **Phase 2 library DONE 2026-10-05**: recipient sealing (`RecipientSealing`, `RecipientKeyRing`, `KeyPurpose.KeyAgreement`); approval
> tokens v2 are Authorization's Phase 4b (the library side is ready). **Phase 3 DONE 2026-10-05**: `CertificateAuthority` (TPM-held CA
> keys), `CertificateTrust` (custom roots only), `CertificateFiles` (PKCS#12 with PBES2), `HashChain`, `MerkleTree` (RFC 6962). The
> library's own phases are complete apart from Phase 5. **2026-10-05 also:** tenant root CA = **P-256** (user), and
> **`YFex.Cryptography.Xml`** built (13). Phase 4 is adoption by Offline, Audit, Privacy and Authorization (12 §3). No NuGet package is added. Everything uses the BCL of .NET 11 (preview 4 probed on this machine).
> **2026-10-18:** `FileSigningKeyStore` (every OS, 14), the F-P1 file-permission fix and a generic `ISecretStore` + `FileSecretStore`
> (12 §4 question 3) are **built**. Linux TPM 2.0 route A approved (question 8, spike pending); **certificate sources built** the same
> day (question 7: `ISigningCertificateSource`, PKCS#12 from sealed secrets, Windows store / A3 token; 13 §6); `YFex.Cryptography.Apple` (Secure Enclave) is a spike for later. Open questions 1, 3, 7, 8 are answered (12 §4).
> **Phase 5 (2026-10-18):** hybrid recipient sealing `0x11` (ML-KEM-768 + P-256) **built and tested**, automatic per host with manual
> overrides. `YFex.Cryptography.Linux` (TPM via tpm2-openssl, Secret Service root key) and `YFex.Cryptography.Apple` (Secure Enclave /
> Keychain) **written, opt-in, not yet run on their OS**: checklist and scripts in 14 §8.
> **2026-10-27:** `YFex.Cryptography.Android` (⚑K19) **built**: Keystore signing keys that work for TLS, a Keystore root key, and
> key folders outside Auto Backup. 11/11 device tests pass on the Android 15/16 emulators; the real-handheld run is pending (14 §9.4–9.5).
> Wycheproof + NIST ACVP vectors were added the same day (11 §3). Named recipient (key-agreement) keys `IRecipientKeyStore` too:
> CNG/TPM in `.Windows`, sealed secrets on every OS (14 §10; unblocks installations I5).
> **Audience:** the user first, then the sessions that implement the phases. Read this file, then the file your phase needs
> ([12-phases-and-acceptance.md](12-phases-and-acceptance.md)).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`.
> **Converges (does not compete with):** privacy 05 (keys, envelope, pseudonym mechanics, intake sealing), offline 03/05 (installation
> keys, CNG root, CA, bundles, verifiers), audit 06 (Hash256, chains, anchors, ⚑A31), authorization-consolidation 06 (approval tokens, ⚑Z12).

## The question and the answer

**Question (user, 2026-10-03):** while designing YFex.Offline we found encryption, signing and key code duplicated and inconsistent
across projects. Should there be one dedicated, generic, reusable library "only for that", built with modern .NET?

**Answer: yes, with a firm boundary** ([02](02-should-there-be-one-library.md)).

- **`YFex.Cryptography`** (sibling repo, BCL only, AOT) owns key material, keyed operations and every encrypted, authenticated or signed
  **byte format**.
- **YFex core** gets only the pure value types other contracts must name: `Hash256`, `KeyId`, `SignatureAlgorithm`,
  `DetachedSignature`, and the canonical writer.
- **`YFex.Cryptography.Windows`** holds CNG/TPM key storage.
- The **meaning** stays with its owner: Privacy (pseudonyms, vault, shredding), Offline (installation identity, bundles, envelopes),
  Audit (chains, anchors) and Authorization (approvals) build on the library.
- **Not moved:** protocol-mandated algorithms (JA3 MD5, ESP MD5, webhook HMAC formats, driver signing, future NF-e XMLDSig) and
  non-adversarial hashing (`StableHash64`, cache keys, file identity).

Cost of the alternatives, in short: leaving crypto in each library means ≥ 4 formats, rings and device keys, and the bugs below. Putting
it all in core drags platform code and stores into every app. Putting it in Privacy couples approvals and mTLS to personal-data
handling. A third-party engine needs packages and loses the TPM.

## Why now: what the inventory found ([01](01-current-state-and-inventory.md))

The four designs agree on the algorithms and disagree on everything around them: 3 key-id shapes, 3 encryption layouts, 4 key rings,
3 root-key contracts, 4 device-key stores, 2 canonical encodings, 3 domain-separation spellings. The live code has these defects:

| # | Defect | Evidence |
|---|---|---|
| **F1** | the storage key changes on every start, so queued offline sales disappear after a restart | `DataProtectionKeyProvider.cs:26-33` |
| **F2** | no associated data, so ciphertexts can be swapped between keys and scopes | `AesGcmValueProtector.cs:56,75` |
| **C1** | one `AesGcm` shared by concurrent calls (not thread-safe on the OpenSSL backend; *plausible*, to confirm on Linux) | `AesGcmValueProtector.cs:26,31-40` |
| **C2** | `TenantSalt` is documented as per-tenant isolation and **never used**: all tenants' secrets share one key | `SecretProtectionOptions.cs:12-16`, `AesSecretProtector.cs:31` |
| **C3** | `rpc.Encryption = EncryptionMode.AesGcm` is **never read**: the outbox stays plain text | `YFexMessagingRpcClientOptions.cs:18-19` |
| **C4** | event-audience tokens never expire, have no key id and no domain | `EventChannel.cs:37-40,154-175` |
| **C5** | approval tokens bind a non-cryptographic XXH64 fingerprint, payload without domain separation | `Evaluator.cs:738-774`, `OverrideTokens.cs:29-44` |
| **C6** | device-token verify re-imports the key per call (513 µs vs 111 µs) and pins no curve | `OverrideTokens.cs:191-195` |
| **C7** | Linux/portable secret stores write plain text, world-readable with the usual umask | `LinuxSecretStore.cs:39`, `PortableSecretStore.cs:43` |
| **C8** | reflection (`ActivatorUtilities`) + the DataProtection package on POS clients | `EncryptionServiceExtensions.cs:61` |
| **C9** | payment idempotency fingerprint: ambiguous `'|'` join + unkeyed hash containing the CPF | `CreatePaymentValidator.cs:59-66` |
| **C10** | authentication failure = "missing" (`null`), which is what makes F1 silent | `EncryptedKeyValueStore.cs:22-27` |
| **C11** | AES-GCM unavailable in Blazor WASM, no capability check (*plausible*) | `AesGcmValueProtector.cs:39` |

**Status 2026-10-04:** F1, F2, C3 and C10 were fixed on 2026-10-03 by offline Phase 0, directly in Persistence.Encryption (random
storage key wrapped by `CngKeyWrapper`/`DataProtectionKeyWrapper`, layout `0x02` with AAD, `UnprotectStatus`, dead RPC options
removed). C1 (shared `AesGcm`) and C8 (`ActivatorUtilities`, DataProtection package) are still open there. Library Phase 0 moves
that interim code onto the library (09 §1 update).

Measured on this machine (.NET 11 preview 4, Windows 11 26200): AES-GCM 64 B 258 ns, HMAC 264 ns, ECDsa P-256 verify 111 µs,
**TPM ECDsa sign 32 ms**, **TPM RSA-OAEP unwrap 75 ms**, ML-KEM-768 11/18 µs, ML-DSA-65 sign/verify 549/44 µs, PBKDF2 600k 55 ms.
ML-KEM and ML-DSA are supported and **not** experimental. Composite ML-DSA and SLH-DSA are experimental. Ed25519 and Argon2 are not in the BCL.

## The mental model

```
 YFex core            Hash256 · KeyId · SignatureAlgorithm · DetachedSignature · ICanonicalValue<T>   (types only, no logic: ⚑K2)
     ▲                (CanonicalWriter/Reader, Hashing and every other computation live in YFex.Cryptography)
 YFex.Cryptography    keys ─ SecretKey (pinned, zeroed) · KeyRing (Pending/Active/OpenOnly/Destroyed) · KeyRingProvider · IRootKeyProvider
     ▲                seal ─ SealedValue v3 (algorithm + key id + AAD context) · RecipientSealing (ECDH→HKDF→AES-GCM)
     │                prove ─ MessageAuthentication · MessageToken · SignedPayload (domain ‖ payload) · SignatureVerifier
     │                derive ─ KeyDerivation (HKDF) · PasswordHashing (PBKDF2)   evidence ─ HashChain · MerkleTree
     │                trust ─ CertificateAuthority · CertificateTrust            report ─ CryptographyCapabilities · OpenResult unions
 YFex.Cryptography.Windows   CngRootKeyProvider (TPM → software KSP) · CngSigningKeyStore (non-exportable ECDsa)
     ▲
 Privacy · Offline · Audit · Authorization · Persistence.Encryption · Messaging.Rpc · Web.Data · Automation   (meaning + policy)
```

Five rules make it hard to misuse:

1. **No nonce, no AAD-less, no raw key in public APIs.** `SealedValue.Seal` generates the nonce and requires a `SealContext`. Keys come
   only from rings (random once, wrapped at rest) ([04](04-primitives-catalog.md), [06](06-keys-rings-rotation-and-storage.md)).
2. **Algorithm and key id on the wire, header authenticated.** Rotation and post-quantum switches are configuration, not format
   changes ([05](05-formats-and-canonical-encoding.md), [08](08-post-quantum-and-crypto-agility.md)).
3. **Domain-separated, canonical bytes.** Everything signed or MACed is `domain ‖ canonical payload`, never JSON (05 §9–§10).
4. **Expected failures are values.** `OpenResult` (`[Union]`), `SignatureCheck`, `TokenCheck`, `PasswordCheck`. Exceptions only for
   programmer errors ([07](07-modern-dotnet-and-api-style.md)).
5. **One device identity.** The TPM-held evidence key signs envelopes, approvals, audit heads and consent. It signs batches, never per
   entry (32 ms each).

## One example, end to end

```csharp
// ✍ Terminal composition root of YFexFarma.Desktop
services.AddYFexCryptography(cryptography => cryptography.UseCngRootKey().StoreKeyRingsInFiles(AppPaths.Data("keys")));
services.AddYFexSqliteStorage(AppPaths.Data("farma.db"));
services.AddYFexStorageEncryption(storage => storage.StoreName = "farma");   // ring "device/storage" by default
services.AddYFexOffline(offline => offline.Terminal().UseWindowsKeyStore());   // installation keys from CngSigningKeyStore
```

**Result:** at first start a random storage key is created and wrapped by the TPM. Every later start unwraps the same key (F1 fixed).
Each stored value is bound to its store and key (F2 fixed). During an outage Ana's offline sale is sealed in the outbox and signed with the
checkout's evidence key. Maria's CPF, typed at the counter, is sealed for central with the tenant's public intake key, so the checkout
cannot read it back. Carla's approval for a 12 % discount binds the SHA-256 digest of that exact outcome. At sync, central verifies each
signature **as of when it was made**, opens Maria's CPF and re-seals it with her subject key, ready for crypto-shredding. App code does
not touch `AesGcm`, `ECDsa` or `HMACSHA256` once ([10](10-usage-examples.md)).

## Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | 28 uses classified S/P/N with file:line, the same concept defined 3–4 times, defects F1/F2 + C1–C11, alignment items, measured baseline and platform probe |
| 02 | [02-should-there-be-one-library.md](02-should-there-be-one-library.md) | **the answer**, options A–E with costs, the boundary (security vs protocol vs non-adversarial), the StableHash64 line, enforcement, domain-agnostic rules |
| 03 | [03-home-layering-and-dependencies.md](03-home-layering-and-dependencies.md) | core contracts (normative), repo layout, dependency graph, no cycles (Rules/Logging/Authorization.Public stay off), what each consumer keeps, CLAUDE.md changes |
| 04 | [04-primitives-catalog.md](04-primitives-catalog.md) | one API per primitive: sealing, derivation, MAC + tokens, hashing, signatures, recipient sealing, passwords, random, constant time, certificates, chains/Merkle, capabilities |
| 05 | [05-formats-and-canonical-encoding.md](05-formats-and-canonical-encoding.md) | normative byte layouts (sealed value v3, legacy L1–L3, recipient seal, verifier, signatures, token, key-ring file), algorithm and domain registries, canonical rules |
| 06 | [06-keys-rings-rotation-and-storage.md](06-keys-rings-rotation-and-storage.md) | `SecretKey`, rings and statuses, root-key ladder (CNG/TPM, environment, file, DataProtection adapter, no DPAPI package), stores, signing keys and the 32 ms TPM budget, rotation, shredding |
| 07 | [07-modern-dotnet-and-api-style.md](07-modern-dotnet-and-api-style.md) | spans and one-shots, modern features used, union results, AOT/trim bans, platform matrix, thread safety (C1), naming, observability |
| 08 | [08-post-quantum-and-crypto-agility.md](08-post-quantum-and-crypto-agility.md) | what net11 ships (probed), why sealing goes first, hybrid ML-KEM plan, signatures later, what agility is not |
| 09 | [09-migration-map.md](09-migration-map.md) | every implementation → its replacement, compatibility, how F1/F2 are fixed, golden tests, doc amendments |
| 10 | [10-usage-examples.md](10-usage-examples.md) | before/after: terminal storage, offline sale envelope, supervisor approval token, CPF sealed for central, audit chain, tenant key rotation, offline login |
| 11 | [11-performance-and-testing.md](11-performance-and-testing.md) | budgets vs measured baseline, benchmark plan, KATs, goldens, negative/concurrency/restart/AAD/architecture/AOT/fuzz tests |
| 13 | [13-document-signatures-and-certificates.md](13-document-signatures-and-certificates.md) | `YFex.Cryptography.Xml` (NF-e/eSocial XML-DSig, AOT-safe, byte-identical to SignedXml), where signing certificates live (A1, A3, cloud, central; NFC-e offline needs a local key), who owns certificate code |
| 14 | [14-platform-signing-key-stores.md](14-platform-signing-key-stores.md) | persisted named signing keys on Linux/macOS: `FileSigningKeyStore` (format, permissions, TLS attach per OS), `FileKeyRingStore` permission fix, macOS Keychain/Secure Enclave evaluation (`.Apple`, TLS 1.3 status on macOS), Linux TPM 2.0 options (§2–§3 built 2026-10-18; §5 Apple and §6 Linux TPM pending) |
| 12 | [12-phases-and-acceptance.md](12-phases-and-acceptance.md) | working rules, cross-design dependencies, phases 0–5 with acceptance, open questions, implementation log |

## Decisions (⚑K) — confirmed by the user on 2026-10-04

All rows were confirmed as recommended. K2 was refined (core = types only), and K4's version byte moved to `0x03` (see the row).

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **K1** | One library? Where? | ✅ **Yes**: sibling repo **`C:\Dev\YFex\YFex.Cryptography`** (`YFex.Cryptography`, `.Windows`, `.Testing`, `.Tests`, `.Windows.Tests`, `.AotSmoke`, `.Benchmarks`), BCL + core only | status quo (≥ 4 formats/rings/device keys, the C-defects); all in core (platform code and stores in every app); inside Privacy (approvals/mTLS depend on privacy runtime); third-party engine (packages, native deps, no TPM) | 02 §2, 03 §1 |
| **K2** | What goes to YFex core? | ✅ **Types only, no implementations** (user refinement): `Hash256` (value semantics, no hashing), `KeyId`, `SignatureAlgorithm`, `DetachedSignature` (namespace `YFex.Cryptography`) and the contract `ICanonicalValue<TSelf>` (`YFex.Canonical`). `CanonicalWriter`/`CanonicalReader` and all computation live in `YFex.Cryptography` (amends ⚑A31). Leaf contracts that may not reference the library (Rules, Audit.Public, Authorization.Public) hash with the BCL one-shot `SHA256.HashData` into `Hash256` | contracts like `ISigningKey` in core (pulls async/key concepts into core); canonical writer under `YFex.Auditing` (Offline/Privacy/Rules import "Auditing") | 03 §2 |
| **K3** | The boundary | ✅ Library owns **S**-class (security) uses. **P**-class (protocol-mandated) and **N**-class (non-adversarial) stay. Rule: a value that feeds a signature/MAC/authorization is canonical bytes or a `Hash256`, never a `StableHash64`. Enforced by an architecture test | everything with "hash" or "crypto" moves (JA3, ESP, webhooks forced through abstractions they don't fit) | 02 §4 |
| **K4** | Encrypted value format | ✅ **Sealed value v3**, version byte **`0x03`** (`0x02` was taken on 2026-10-03 by offline Phase 0's interim layout): `0x03 ‖ algorithm ‖ key class ‖ key id(16) ‖ nonce(12) ‖ ct ‖ tag(16)`, AAD = header + **length-prefixed** context (47 B overhead, same as privacy's draft, which it replaces). Legacy readers L1/L1b (Persistence `0x01`/`0x02`), L2 (Web.Data, new values prefixed `y3.`), re-seal on write | keep privacy's draft (no algorithm byte, `'|'`-joined AAD is ambiguous); a schema column for the format (privacy draft) instead of the `y3.` prefix | 05 §1–§2 |
| **K5** | Default algorithms | ✅ AES-256-GCM · HKDF-SHA256 · HMAC-SHA256 · SHA-256 · ECDsa P-256 P1363 · ECDH P-256 ECIES · PBKDF2-SHA256 600 000. ChaCha20-Poly1305 implemented as fallback only. SHA-3/KMAC/SP 800-108 available, not default | ChaCha20 default (slower here, no gain on AES-NI hardware); KMAC (SHA-3 4–5× slower, not on all targets) | 04 summary |
| **K6** | Key model | ✅ `KeyId` = one id **per key version** (Guid v7); `KeyRing` with `Pending/Active/OpenOnly/Destroyed`; `KeyPurpose` enforced; `SecretKey` pinned + zeroed + leased; rings swapped atomically, two-phase rotation on multi-node servers, reload on unknown key | privacy's `KeyId(Guid, Version)` (a second identifier for no gain); string key ids (Rules today) | 06 §1–§2, §6 |
| **K7** | Root keys | ✅ `IRootKeyProvider` moves to the library. **`CngRootKeyProvider`** (TPM → software KSP, package-free) in `.Windows`; environment/provided/file providers; DataProtection only as an optional server adapter; **no `ProtectedData` package** (DPAPI via `LibraryImport` if ever needed); cloud KMS = seam only | DPAPI through the `System.Security.Cryptography.ProtectedData` package (approval; no TPM binding); keep DataProtection on clients (F1's origin, server-oriented dependency) | 06 §3 |
| **K8** | Signing keys and identity | ✅ `ISigningKey`/`ISigningKeyStore` in the library, **`CngSigningKeyStore`** (TPM first, non-exportable) in `.Windows`; Offline's installation keys = two names in that store; one evidence key signs envelopes, approvals, audit heads and consent; signatures over `SHA-256(domain ‖ 0x00 ‖ canonical payload)`; TPM signs **batches/heads, never per entry** (32 ms) | per-library device keys (offline F11); per-entry TPM signing (unusable at 32 ms) | 06 §5, 05 §5 |
| **K9** | Result shapes | ✅ `[Union]` tagged struct for data-carrying hot-path outcomes (`OpenResult`), `enum` for data-less (`SignatureCheck`, `TokenCheck`, `PasswordCheck`), native `union` on cold paths (`CertificateCheck`); exceptions only for programmer errors | exceptions (`CryptographicException`) for authentication failures (expensive, easy to swallow into `null`, C10) | 07 §3 |
| **K10** | Post-quantum | ✅ **Agility now, switch later**: algorithm ids reserved (`MLDsa65`, composite, hybrid ML-KEM-768+ECDH). First switch = **hybrid recipient sealing** after a fleet probe. Signatures later (composite still experimental; TPMs lack ML-DSA). SLH-DSA not planned | switch now (Windows 10 checkouts unsupported, +3.3 KB per signature, experimental APIs); ignore PQ (sealed personal data exposed to harvest-now-decrypt-later) | 08 |
| **K11** | Chains and Merkle trees | ✅ `HashChain` + `MerkleTree` (RFC 6962) in the **library**; `Hash256` in core (amends ⚑A31, which put `HashChain` in core); the timestamp moves into the canonical entry; audit domains become `yfex.audit.*.v1` | keep A31 as is (core grows with chain logic only Audit/Privacy/Offline use) | 04 §11 |
| **K12** | Approval binding (C5) | ✅ Approval tokens **v2** (with ⚑Z12) bind a SHA-256 **decision digest** + domain `yfex.approval.v2` + `KeyId`; the XXH64 fingerprint stays for equality/UI; **v1** verified byte-identically (golden) for one `MaxAge`, then refused | keep XXH64 binding (crosses the crypto line); break v1 immediately (in-flight offline approvals rejected at sync) | 02 §4.2, 09 §1 |
| **K13** | How F1/F2 get fixed | ✅ **Phase 0 builds the minimal library slice** (sealing, rings, root providers, CNG) and fixes Persistence through it, independently of the other ⚑ | a 20-line stopgap in Persistence.Encryption first (faster by days, then migrated again) | 12 §3, 09 §2 |
| **K14** | Packages | ✅ **None now.** Future candidates, each needing approval when its feature comes: `System.Security.Cryptography.Xml` (NF-e XMLDSig, Fiscal), `System.Security.Cryptography.Pkcs` (RFC 3161 timestamps, CMS) — **approved 2026-10-14 for `YFex.Time.Cryptography` (RFC 3161 time source), version 10.0.13 for the CVE-2026-84008/70162 fixes (time ⚑T16)**, Argon2id (Konscious managed or NSec/libsodium native), NSec/BouncyCastle for Ed25519/X25519, Microsoft `BannedApiAnalyzers`, Wycheproof vectors (third-party data) — **Wycheproof + NIST ACVP ML-KEM vectors approved and added 2026-10-27 as test data (12 §5)**. BenchmarkDotNet only in benchmarks (already approved, ⚑L21) | adopt now (scope creep, native assets, AOT work) | 04, 11 §3 |
| **K15** | Persistence.Encryption and the dead RPC options | ✅ Keep `YFex.Persistence.Encryption` as a **thin adapter** (`EncryptedKeyValueStore` over `SealedValue`; keyed inner registration; integrity failures raise `StorageIntegrityException`, `TryGetAsync` gives the union); `IKeyProvider`/`EncryptionKeySource` `[Obsolete]`; **remove** `YFexMessagingRpcClientOptions.Encryption/EncryptionKeySource` (`[Obsolete(error: true)]`, C3, F12) | wire the RPC options to the decorator (two ways to configure one thing); fold the adapter into the library (library would depend on Persistence) | 09 §1–§2 |
| **K16** | Event-audience tokens (C4) | ✅ `MessageToken` with expiry (default 12 h), key id, domain, ring shared by nodes | keep per-process random key without expiry | 04 §3, 09 I7 |
| **K17** | Name | ✅ **`YFex.Cryptography`** (assembly, repo, core namespace). Type names without abbreviations (`SealedValue`, `MessageAuthentication`, `KeyDerivation`, `RecipientSealing`, `PasswordHashing`) | `YFex.Crypto` (abbreviation); `YFex.Security` (taken by the network-intelligence repo); `YFex.Keys` (too narrow) | 07 §7 |
| **K18** | Browser and mobile | ✅ Library reports capabilities; WASM gets `AlgorithmUnavailable` (no AES-GCM in the browser). Storage encryption on WASM = a later WebCrypto design; Android Keystore/Linux libsecret providers in Phase 5 | make WASM "work" with a managed AES (unreviewed, slow, keys in JS-reachable memory) | 07 §5, 12 Phase 5 |
| **K19** | Android Keystore (added 2026-10-20 for installations ⚑I26) | ✅ **confirmed by the user 2026-10-20** ("a variant of the Crypto"): package **`YFex.Cryptography.Android`** (`net11.0-android`) with a Keystore `ISigningKeyStore` (P-256, StrongBox → TEE → software, real `KeyProtection`), a Keystore-wrapped `IRootKeyProvider` and an attestation reader (record only). Hardware preferred, never required here | inside `YFex.Installations.Android` (key code outside the crypto library, architecture-test exception) | 14 §9 |

### Added 2026-11-18 — pending

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **K20** | Verifying YFex.Inference model files ([inference](../inference/README.md) ⚑IF17) | **no new API:** the built `Hashing.Sha256Async(Stream)` checks each file against the hash in a signed bundle (Installations ⚑I45) before load | a model-signature API here | inference README §5 |
