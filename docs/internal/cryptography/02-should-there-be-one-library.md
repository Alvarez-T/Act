# 02 — Should the ecosystem have one cryptography library?

**Answer: yes, with a firm boundary.** One library, `YFex.Cryptography`, owns key material, keyed operations and every byte format
that is encrypted, authenticated or signed. A handful of pure value types live in YFex core. Platform key storage (CNG/TPM) goes in a
platform package. The **meaning** stays in the libraries that own it: Privacy decides what a pseudonym domain is, Offline decides what
a bundle is, Audit decides what a chain is, and Authorization decides what an approval is. Protocol-mandated algorithms and
non-adversarial hashing stay where they are.

So the honest answer to "only for that" is **yes for primitives, formats and keys; no for the features built on them**.

## 1. Why the question comes up now

Four designs, written in a week, each needed "a little cryptography" and each defined its own ([01](01-current-state-and-inventory.md)
§2): three key-id shapes, three encryption layouts, four key rings, three root-key contracts, four device-key stores, two canonical
encodings, three domain-separation spellings. The live code adds two more encryption layouts and two more key rings. Every defect in
01 §3 is a **glue** defect, not an algorithm defect:

| Defect | Glue that failed | Would one library have prevented it? |
|---|---|---|
| F1 | key material derived from randomized output | yes: the only way to get a storage key is `KeyRing` (random once, wrapped), there is no "derive from a seed" API |
| F2 | AAD forgotten | yes: `SealedValue.Seal` has no overload without a `SealContext` |
| C1 | `AesGcm` shared across threads | yes: instances are pooled inside the library, callers never see `AesGcm` |
| C2 | a documented option not implemented | partly: per-tenant keys are a ring feature with a test suite, not an option bag |
| C4 | token without expiry, key id, domain | yes: `MessageAuthentication` tokens always carry a domain and the ring's key id; expiry is in the shared token shape |
| C5 | non-cryptographic hash used as a security binding | yes, with a rule (§4) and an architecture test |
| C7 | secrets stored without protection | yes: one root-key ladder with an honest "unprotected" capability flag |
| C10 | failure = absence | yes: `OpenResult` distinguishes every outcome |

## 2. Options and their cost

| Option | What it means | Cost | Verdict |
|---|---|---|---|
| **A. Status quo** | each library keeps its own crypto (Privacy, Offline, Audit, Authorization implement their parts of their designs) | lowest effort per feature, highest total: ≥ 4 envelope/token formats, 4 key rings, 4 device-key stores (F11), 4 sets of tests and reviews. Every new library re-learns nonce limits, AAD, domain separation, zeroing. Each security review covers N codebases. The defects in §1 are what this option produces | rejected |
| **B. Everything in YFex core** | a `YFex.Cryptography` namespace in `YFex/src/YFex` with primitives **and** key storage | core gets platform code (CNG/TPM is Windows-only: CA1416 everywhere), async key stores, DI, file I/O and certificate handling. Every app, including Blazor WASM, ships it. Core's "base types" purpose (yfex-core-base-types) is stretched to infrastructure | rejected for implementations; **accepted for ~5 pure value types** (§3) |
| **C. One sibling library + tiny core contracts + platform package** | `C:\Dev\YFex\YFex.Cryptography` (BCL only) + `.Windows`; core holds `Hash256`, `KeyId`, `SignatureAlgorithm`, `DetachedSignature` (+ the `ICanonicalValue` contract; types only, 03 §2) | one more repo and package; a migration of 11 S-class call sites (09); Privacy/Offline/Audit/Authorization/Persistence/Web reference it (they need cryptography anyway, so the edge exists in every option except A). Design + Phase 0–3 ≈ the effort the four designs would spend separately, once | **recommended** (⚑K1) |
| **D. Inside YFex.Privacy** | Privacy owns all cryptography (it already designed most of it) | Offline, Audit, Authorization, Messaging, Web depend on the privacy **runtime** to sign a bundle or an approval. Approval tokens and mTLS are not personal-data concerns. Privacy's tiering (Tier 2 built "with the feature") would block Offline Phase 0 | rejected |
| **E. A third-party library** | NSec (libsodium), BouncyCastle, or similar as the engine | NuGet package(s) needing approval; libsodium is a native dependency per RID (AOT and Android packaging work); BouncyCastle is large and managed (slower than CNG/OpenSSL for AES/ECDsa, and its own key storage cannot use the TPM). Gains Ed25519/X25519/Argon2id/XChaCha20 | rejected as the engine; **individual packages remain ⚑ options** for gaps (Argon2id, 04 §8) |

### Cost of option C in numbers

| Item | Size |
|---|---|
| Core additions | 4 value types + 1 contract, no logic (⚑K2) |
| Library public surface | ~25 types ([04](04-primitives-catalog.md)) |
| Windows package | 3 types (`CngRootKeyProvider`, `CngSigningKeyStore`, `WindowsKeyProtection`) |
| Call sites migrated | 11 S-class sites in 7 projects (09 §1) |
| Formats with legacy readers | 3 (Persistence `0x01`, Web.Data base64, approval token v1) |
| New dependencies | none (BCL + `Microsoft.Extensions.DependencyInjection.Abstractions`, already used) |

## 3. What lives where (summary; details in [03](03-home-layering-and-dependencies.md))

```
YFex core  (namespace YFex.Cryptography + YFex.Canonical; TYPES ONLY: no logic, no keys, no I/O, no platform code — ⚑K2)
   Hash256 · KeyId · SignatureAlgorithm · DetachedSignature · ICanonicalValue<TSelf>
        ▲
YFex.Cryptography  (sibling repo; BCL + core)
   CanonicalWriter/CanonicalReader · Hashing · SecretKey · KeyRing · KeyRingProvider · IRootKeyProvider · IKeyRingStore · SealedValue · SealContext · OpenResult
   MessageAuthentication · MessageDomain · KeyDerivation · PasswordHashing · ISigningKey · SignatureVerifier · PublicKeyCache
   RecipientSealing · HashChain · MerkleTree · CertificateAuthority · CertificateTrust · CryptographyCapabilities
        ▲                                   ▲
YFex.Cryptography.Windows             consumers: Privacy · Offline · Audit · Authorization · Persistence.Encryption.Windows ·
   CngRootKeyProvider · CngSigningKeyStore      Messaging.Rpc · Web.Data · Automation · (Fiscal: certificates only)
```

## 4. The boundary: what does **not** move

### 4.1 Three classes of use

| Class | Test | Rule | Examples |
|---|---|---|---|
| **S — security** | Would an adversary gain anything by forging, reading or replaying it? | Through `YFex.Cryptography` (or a core value type). No direct `AesGcm`/`HMACSHA256`/`ECDsa`/`RandomNumberGenerator`-for-keys outside the library | storage encryption, secrets at rest, approval tokens, audience tokens, pseudonyms, bundles, envelopes, chains, offline login |
| **P — protocol** | Does an external specification fix the algorithm and encoding? | Stays next to the protocol. The BCL is used directly, documented with the spec reference | JA3 (MD5), ESP flash check (MD5), payment-provider webhook HMAC hex, Windows driver signing, NF-e XMLDSig (future), TLS |
| **N — non-adversarial** | Is it only identity, caching, equality or change detection, where nobody benefits from a collision? | `StableHash64` (fast, non-crypto) or a plain SHA-256 where a content identity is published. Never used to **authorize** anything | cache keys, situation hashes, rule-row ids, schema signatures, file identity, CRC frames, trace ids |

### 4.2 The line between StableHash64 and cryptography

`StableHash64` is correct for what it says: deterministic, fast, persisted, **not cryptographic**. The line is crossed when a non-crypto
hash ends up **inside** something a key signs or a check trusts (C5). The rule:

> If a value is an input to a signature, a MAC or an authorization decision, it is either the canonical bytes themselves or a
> `Hash256` of them. A `StableHash64` may travel next to it for display and equality, never instead of it.

```csharp
// Before — the token signs a 64-bit XXH64 of the outcome (OverrideTokens.cs:40, Evaluator.cs:741)
var payload = OverrideToken.Payload(kind, approver, permission, decision.Fingerprint /* ulong XXH64 */, now, expires, keyId);

// After (⚑K12) — the token signs the SHA-256 digest of the canonical outcome; the ulong stays for UI equality
Hash256 digest = decision.ComputeDigest();                    // Rules: BCL SHA256.HashData(canonical outcome) into the core Hash256 type, no library reference
var token = approvals.Issue(approver, permission, digest, now); // Authorization (⚑Z12) signs through YFex.Cryptography
```

### 4.3 Enforcement

An **architecture test** in `YFex.Cryptography.Tests` scans the source trees listed in 09 §1 for banned APIs outside allowed folders
(`AesGcm`, `ChaCha20Poly1305`, `HMACSHA256`, `ECDsa.Create`, `IDataProtector`, `ProtectedData`) and fails with the file and line. P-class
files carry an allow-list entry with the spec reference. The Microsoft `BannedApiAnalyzers` package would give compile-time errors
instead, but it is a NuGet package, so it is listed as ⚑K14 and not assumed.

## 5. What "generic" means here (domain-agnostic, yfex-libs-domain-agnostic)

- No `Tenant`, `Store`, `Customer`, `Cpf` or `Sale` in library types. A per-tenant key is a `KeyRingName` chosen by the caller
  (`"tenant/0=farmaviva/secrets"`). Separation comes from ring names and `SealContext`, not from a tenancy dependency.
- Closed enums only for sets the library owns completely: `KeyPurpose`, `SealingAlgorithm`, `SignatureAlgorithm`, `KeyStatus`. Open
  sets are record structs with string keys, like `PermissionKey`: `KeyRingName`, `MessageDomain`, `KeyDerivationLabel`.
- The library never decides **policy**: how long a token lives, which data is encrypted, who may rotate. Callers pass `TimeSpan`s,
  rings and contexts.
