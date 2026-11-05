# 11 — Performance budgets, benchmarks and test strategy

## 1. Budgets (benchmarks gate Phases 0–3)

The baselines were measured on the development machine (01 §4). The budgets leave room for the library's own work (header, context,
ring lookup, pooling) over the raw BCL call. Budgets are for the Release build on x64 with AES-NI. A budget that is missed is logged as a
gap with the measured number (the YFex.Logging practice), not silently relaxed.

| Operation | Raw BCL baseline | Budget | Allocation |
|---|---:|---:|---:|
| `SealedValue.Seal` 64 B, ring cached, pooled `AesGcm` | 258 ns + 72 ns nonce | **≤ 600 ns** | 0 B (caller buffer) |
| `SealedValue.Open` 64 B | ≈ 260 ns | **≤ 600 ns** | 0 B |
| `EncryptedKeyValueStore.SetAsync` overhead vs. the inner store | — | ≤ 1 µs + one output array | output array only |
| `MessageAuthentication.ComputeTag` 64 B, subkey cached | 264 ns | **≤ 400 ns** | 0 B |
| `MessageToken.Read` (event audience) | — | ≤ 1.5 µs | 0 B (payload into caller span) |
| `Hashing.Sha256` 1 KiB | 414 ns | ≤ 450 ns | 0 B |
| `HashChain.Next` 1 KiB entry | ≈ 420 ns | ≤ 600 ns (2026-10-05: 578 ns ✅; raw SHA-256 of the same bytes 517 ns) | 0 B ✅ |
| `KeyDerivation.DeriveKeyMaterial` 32 B | 690 ns (HKDF one-shot) | ≤ 900 ns | 0 B |
| `SignedPayload.Sign`, software P-256 | 125 µs | ≤ 140 µs | ≤ 300 B |
| `SignedPayload.Sign`, TPM | 31.8 ms | informational (hardware-bound) | — |
| `SignedPayload.Verify`, key cached (fixes C6) | 111 µs | ≤ 125 µs | ≤ 150 B |
| `RecipientSealing.Seal` 11 B | 1.19 ms | ≤ 1.4 ms (2026-10-05: 1.17 ms ✅) | ≤ 2 KB (1.91 KB ✅) |
| `PasswordHashing.Verify` (600 000) | 55 ms | informational (deliberately slow) | — |
| `KeyRingProvider.TryGetCached` | — | ≤ 20 ns | 0 B |
| first `GetAsync` of a ring with a TPM root | 75 ms unwrap | ≤ 100 ms, once per process | — |

Privacy 05 §9 budgets (`Pseudonymize` ≤ 1.2 µs, envelope seal ≤ 1.5 µs) remain valid and are met by these primitives.

## 2. Benchmark plan (`YFex.Cryptography.Benchmarks`, BenchmarkDotNet)

- BenchmarkDotNet 0.15.8 (MIT) is already approved for benchmark projects (⚑L21, used by `YFex.Logging.Benchmarks`). The same version
  is reused, in the benchmark project only.
- One class per row of §1, `[MemoryDiagnoser]`, payload sizes 16 B / 64 B / 1 KiB / 64 KiB for symmetric operations.
- **Concurrency benchmark:** 8 threads sealing through one ring. This measures the `AeadInstancePool` under contention (07 §6) against a
  per-call `new AesGcm`. Target: ≤ 2× the uncontended time per operation.
- **Platform matrix:** Windows x64 (dev machine and a real POS: Celeron/Pentium class with AES-NI), Linux x64 (OpenSSL 3), and an ARM64
  run when an Android handheld enters scope. The TPM rows run only on machines with a TPM and are reported, not gated.
- Results are recorded in the implementation log (12) with the machine description, as YFex.Logging does.

## 3. Test strategy

| Layer | What | How |
|---|---|---|
| **Known-answer tests (KAT)** | AES-256-GCM (NIST SP 800-38D / CAVP vectors), HKDF-SHA256 (RFC 5869 A.1–A.3), HMAC-SHA256 (RFC 4231), PBKDF2-HMAC-SHA256 (published RFC 7914 §11 vectors), SHA-256 (FIPS 180-4 examples), ECDsa P-256 verify (NIST CAVP SigVer), ML-KEM/ML-DSA (FIPS 203/204 vectors when Phase 5 starts), RFC 6962 Merkle vectors | vectors typed into `YFex.Cryptography.Testing/KnownAnswerVectors.cs` (public standards, no third-party package). They prove the wrappers pass bytes in the right order (AAD vs plaintext, salt vs info), which is where wrappers break |
| **Golden formats** | every format of 05 and the legacy layouts (09 §3) | committed hex files. Byte-identical output for deterministic formats, open-ability for randomized ones |
| **Negative tests** | every `OpenResult`/`SignatureCheck`/`TokenCheck`/`PasswordCheck` case | flip one bit in each header field, nonce, ciphertext, tag. Wrong context part. Swapped key id. Destroyed key. Truncated input of every length 0..header+tag. Downgraded algorithm id. Expired token. Revoked key before/after `signedAt` |
| **Property tests** | seal/open round trips, canonical encoding (equal values ⇔ equal bytes, NFC, decimal normalization) | xunit theories with seeded random inputs (no FsCheck package) |
| **Concurrency** | C1: 16 threads × 100 000 seal/open on one ring, then every value opens | runs on Windows and in a Linux container (OpenSSL) |
| **Persistence restart** (F1) | write, dispose the host, start a new host, read | `CngRootKeyProvider` with the software KSP and a test key name (always runs); TPM variant skips without a TPM |
| **AAD binding** (F2) | value copied to another key, store, scope, tenant, column | `AuthenticationFailed` |
| **Architecture tests** | banned APIs outside the library and P-class allow-list (02 §4.3); no `MessageDomain.Raw` outside legacy codecs; library references only core | source scan + `System.Reflection.Metadata` over the built assembly (no reflection at run time in the product) |
| **AOT smoke** | `YFex.Cryptography.AotSmoke`: every public entry point, published with native AOT | 0 IL2xxx/IL3xxx warnings; runs and exits 0 |
| **Fuzzing of parsers** | `TryReadHeader`, legacy readers, `MessageToken.Read`, key-ring file reader, `CanonicalReader` | seeded random mutations of golden inputs in a long-running test category (no package); must never throw anything but documented results |
| **Consumer contract suites** | `IKeyRingStore`, `IRootKeyProvider`, `ISigningKeyStore`, `IPublicKeyDirectory` | abstract test classes in `.Testing`. Privacy.Data (SQL vault), Offline (registry) and Persistence (key-value ring store) inherit them |

**Wycheproof and NIST ACVP** (approved and added 2026-10-27, 12 §5): adversarial Wycheproof vectors (Apache-2.0) for ECDSA P-256,
RSA PKCS#1/PSS verify, ECDH P-256 points, AES-256-GCM and ML-KEM-768, and NIST ACVP ML-KEM-768 vectors, copied as data into
`tests/YFex.Cryptography.Tests/Vectors/` (provenance and hashes in its README). They run through the library's entry points
(`WycheproofTests`, `AcvpMLKemTests`); HKDF/HMAC/PBKDF2 vectors are not copied because they would reach only the BCL.

## 4. AOT smoke contents

```
seal/open (ring + legacy L1) · MAC + token · HKDF · PBKDF2 · Hash256 + HashChain + Merkle proof · software ECDsa sign/verify ·
recipient seal/open · certificate root + leaf + CustomRootTrust validation · capabilities report · key-ring file round trip
(+ on Windows: CngRootKeyProvider software KSP, CngSigningKeyStore software KSP, deleting the test keys at exit)
```
