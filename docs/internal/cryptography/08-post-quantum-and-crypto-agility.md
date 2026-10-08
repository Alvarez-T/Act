# 08 — Post-quantum readiness and crypto agility (⚑K10)

## 1. What .NET actually ships (verified 2026-10-03)

Probed by reflection and by calling `IsSupported` on .NET 11.0.0-preview.4, Windows 11 build 26200 (01 §4):

| Algorithm | BCL type | `[Experimental]`? | Supported here | Sizes | Measured |
|---|---|---|---|---|---|
| ML-KEM-512/768/1024 (FIPS 203) | `MLKem`, `MLKemCng`, `MLKemOpenSsl` | **no** | yes | ML-KEM-768: encapsulation key 1 184 B, ciphertext 1 088 B, shared secret 32 B | encapsulate 11 µs, decapsulate 18 µs |
| ML-DSA-44/65/87 (FIPS 204) | `MLDsa`, `MLDsaCng`, `MLDsaOpenSsl` | **no** (only `SignPreHash`/`VerifyPreHash`/`SignMu`/`VerifyMu` are) | yes | ML-DSA-65: public key 1 952 B, signature 3 309 B | sign 549 µs, verify 44 µs |
| SLH-DSA (FIPS 205) | `SlhDsa`, `SlhDsaCng`, `SlhDsaOpenSsl` | **yes** (`SYSLIB5006`) | **no** on this Windows build | signatures 7–50 KB | — |
| Composite ML-DSA (IETF draft) | `CompositeMLDsa`, `CompositeMLDsaCng` | **yes** (`SYSLIB5006`) | yes (`MLDsa65WithECDsaP256` among 18 combinations) | ≈ ML-DSA + 64 B | — |
| KMAC, SHA-3, SHAKE | `Kmac128/256`, `SHA3_*`, `Shake128/256` | no | yes | — | SHA3-256 1 KiB: 1.85 µs |
| Ed25519 / X25519 | — | — | **not in the BCL** | — | — |

Availability is an **operating system** property, not a .NET one. CNG gained ML-KEM/ML-DSA in recent Windows 11 and Server 2025 builds,
and Linux needs OpenSSL ≥ 3.5. Windows 10 (still common on pharmacy checkouts) cannot be assumed to support them. Phase 5 re-probes the
real fleet before any switch.

## 2. Recommendation: agility now, switch later

**No post-quantum algorithm is made default in this design.** What happens now costs nothing and makes the later switch a configuration
change:

1. **Algorithm id in every format** (05 §8): sealed values, recipient sealing, detached signatures and password verifiers. Ids
   `0x10`/`0x11` are reserved for ML-DSA-65, composite ML-DSA-65+P-256, and hybrid ML-KEM-768+ECDH sealing.
2. **Key rings and public-key records carry the algorithm.** A ring can hold P-256 and ML-DSA keys side by side. Verifiers dispatch per
   record.
3. **Sizes are never hard-coded by consumers.** They call `GetSealedLength`/`SignatureLength`. A 3 309-byte ML-DSA signature breaks no
   `byte[64]`. An architecture test looks for literal `64` next to `Signature` in consumer code.
4. **Capability reporting** (04 §12) tells central which installations could use PQ algorithms. The installation registry stores it
   at enrollment.

## 3. Where post-quantum matters first: sealing, not signatures

| Threat | Affects | Urgency |
|---|---|---|
| "harvest now, decrypt later": a recorded ciphertext decrypted by a future quantum computer | **recipient sealing** (CPF and customer data sealed for central travel over networks and sit in outboxes and backups); TLS sessions | real for data with long confidentiality needs (personal data: LGPD retention, health data in pharmacies) |
| forging a signature **in the future** | audit anchors and bundles verified years later | lower: audit chains plus external witnesses (audit 06 §9) detect rewrites even if a signature is later forgeable; bundles live hours or days |
| forging a signature **today** | everything signed | none (no quantum computer exists that breaks P-256) |

Symmetric cryptography (AES-256, HMAC-SHA256, HKDF-SHA256) is already fine against quantum attacks (Grover halves the security level,
leaving 128 bits). **Only the asymmetric parts change.**

Hence the order of the switch (Phase 5, after a fleet probe):

1. **Hybrid recipient sealing** `0x11`: ML-KEM-768 encapsulation **and** ephemeral ECDH P-256. Both shared secrets go into HKDF, so the
   result is safe if **either** algorithm holds. The intake public key becomes a pair (P-256 + ML-KEM-768). Costs: +1 088 bytes per
   sealed value (a sealed CPF grows from 110 to ~1.2 KB) and +11 µs. That is negligible against the 1.2 ms ECDH key generation.
   Installations that do not support ML-KEM keep sealing with `0x01`, and central accepts both.
2. **TLS**: follows the platform (`SslStream` negotiates hybrid key exchange when both OS stacks support it). There is no YFex code to
   change.
3. **Signatures** (composite ML-DSA-65 + ECDsa P-256) only when (a) .NET removes `[Experimental]` from `CompositeMLDsa`, (b) the TPMs in
   the fleet can hold the key or a software key is acceptable for that role, and (c) the size cost is acceptable: +3.3 KB per
   envelope and bundle, ~0.5 ms signing. Long-lived **central** keys (tenant root CA, bundle signing, audit anchors) go first. Device
   keys go last because TPM 2.0 chips do not implement ML-DSA.

**SLH-DSA** is not planned: signatures are 7–50 KB, it is unsupported here, and its only advantage (hash-based conservatism) matters for
roots of trust that rotate every 10 years. That can be revisited for the tenant root CA.

## 4. What crypto agility does **not** mean

- No runtime negotiation between peers: the writer picks per configuration, and the reader accepts every id it has an implementation and
  a policy for.
- No "accept anything": central keeps an **allowed algorithms** policy per object kind (e.g. bundles must be `EcdsaP256Sha256` or better).
  A downgraded signature is `SignatureCheck.AlgorithmNotAllowed`, never silently accepted.
- No algorithm choice in application code: apps choose rings and purposes. Algorithms are library configuration.
