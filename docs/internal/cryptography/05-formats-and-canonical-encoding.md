# 05 — Byte formats, algorithm ids, domains and canonical encoding

Every format here is **normative**. Each one has a golden file in `YFex.Cryptography.Tests/Golden/` produced with fixed test keys and
an internal fixed-nonce seam (never public). A change to a format is a new version byte, never an edit (11 §3).

Rules shared by all formats:

1. **The first byte is a format version.** Readers dispatch on it and keep every old reader that may still have data at rest.
2. **The algorithm is on the wire** (one byte from a library-owned enum). Changing the default algorithm is a configuration change, and
   old values stay readable (crypto agility, [08](08-post-quantum-and-crypto-agility.md)).
3. **The key is on the wire** as a `KeyId` (16 bytes). Rotation needs no format change.
4. **Everything in the header is authenticated** (AAD or signed bytes). Nobody can swap the algorithm or key id without detection.
5. **Where a value belongs is authenticated, not stored**: the caller passes the same `SealContext`/`MessageDomain` to seal and open.

## 1. Sealed value v3 (⚑K4) — replaces privacy 05 §6.1 and both live layouts

```
offset  size  field
0       1     format version   0x03
1       1     algorithm        SealingAlgorithm: 0x01 AES-256-GCM, 0x02 ChaCha20-Poly1305
2       1     key class        opaque to YFex.Cryptography, assigned by the key's owner (Privacy KeyKind; 0 = none)
3       16    key id           KeyId of the exact key version (Guid v7, big endian)
19      12    nonce            random per value (RandomNumberGenerator)
31      n     ciphertext       |plaintext|
31+n    16    tag
AAD  =  bytes[0..19)  ‖  SealContext.Bytes
```

- **Overhead: 47 bytes**, the same as Privacy's draft. Privacy had a separate key-version byte. Here a version **is** a key id, and the
  freed byte carries the algorithm.
- **Version byte `0x03`:** `0x02` was drafted by Privacy 05, but on 2026-10-03 offline Phase 0 wrote a **different** `0x02` layout to
  disk in Persistence.Encryption (L1b, §2). Readers dispatch on the first byte, so the converged format takes the next free value.
- **SealContext bytes** = `varint(len) ‖ UTF-8(domain)` followed by `varint(len) ‖ UTF-8(part)` for each part. Example for the device
  store: domain `yfex.store.v1`, parts `[storeName, key]` → `0D "yfex.store.v1" 05 "farma" 0A "outbox:042"`. The parts are unambiguous,
  unlike `'|'`-joined text (privacy A-2).

## 2. Legacy layouts (read, never written)

| Id | Layout | Key | Reader | Then |
|---|---|---|---|---|
| **L1** Persistence `0x01` | `0x01 ‖ nonce(12) ‖ ct ‖ tag(16)`, **no AAD** (`AesGcmValueProtector.cs:8-14`) | the device storage key, which F1 made unstable | `LegacyFormats.OpenPersistenceVersion1(SecretKey, …)` → `Opened(NeedsReseal: true)` | re-sealed as v2 on the next write. Values written under F1 by an earlier process fail as `AuthenticationFailed`. They are counted, logged **once** as `StorageKeyMismatch` (offline 05 §3) and left in place for support, never silently returned as missing |
| **L1b** Persistence `0x02` (offline Phase 0, 2026-10-03) | `0x02 ‖ nonce(12) ‖ ct ‖ tag(16)`, AAD = UTF-8 `"yfex.store|" + store + "|" + key` | the wrapped storage key (`WrappedKeyProvider` + `CngKeyWrapper`/`DataProtectionKeyWrapper`) | `LegacyFormats.OpenPersistenceVersion2` (rebuilds the `'|'` AAD) → `Opened(NeedsReseal: true)` | re-sealed as `0x03` on the next write. The CNG key name and the stored wrapped key are kept, so terminals keep their storage key |
| **L2** Web.Data | `base64(nonce(12) ‖ ct ‖ tag(16))`, no version byte (`AesSecretProtector.cs:46-52`) | the one configured key (C2) | `LegacyFormats.OpenWebSecretVersion1(SecretKey, …)` | new values are text `"y3." + base64url(sealed value v3)`. The `y3.` prefix tells the formats apart **without a schema change** (privacy 05 proposed a `secret_format` column instead) |
| **L3** Approval token v1 | `kind(1) ‖ i32LE len ‖ approver ‖ i32LE len ‖ permission ‖ u64LE fingerprint ‖ i64LE issuedAt UTC ticks ‖ i64LE expiresAt ticks ‖ i32LE len ‖ keyId` (UTF-8 strings, `OverrideTokens.cs:29-44`); server = raw HMAC-SHA256 32 bytes with a string-named key; device = ECDsa P-256 SHA-256 P1363 64 bytes | Rules keys | `ApprovalTokenVersion1Codec` (Authorization, built on `MessageDomain.Raw` and `SignatureVerifier`). Golden: `YFex.Rules.Tests/Golden/approval-token-v1.tsv` (2026-10-04, generated before the move) | accepted only within one `MaxAge` after v2 ships (⚑K12), then refused |

## 3. Recipient-sealed value v1 (ECIES; privacy offline intake)

```
offset  size  field
0       1     format version   0x81   (high bit: recipient family, so it can never be read as a sealed value)
1       1     algorithm        RecipientSealingAlgorithm: 0x01 ECDH P-256 + HKDF-SHA256 + AES-256-GCM
2       16    recipient key id
18      65    ephemeral public key (SEC1 uncompressed 0x04 ‖ X ‖ Y)
83      n     ciphertext
83+n    16    tag
key ‖ nonce = HKDF-SHA256(IKM = ECDH shared secret, salt = bytes[18..83) ‖ bytes[2..18),
                          info = "yfex.recipient-seal.v1" ‖ 0x00 ‖ SealContext.Bytes) → 44 bytes
AAD = bytes[0..83) ‖ SealContext.Bytes
```

Overhead 99 bytes (a CPF of 11 bytes becomes 110).

**Hybrid `0x11` (Phase 5, built 2026-10-18; 08 §3):**

```
offset  size  field
0       1     format version   0x81
1       1     algorithm        0x11 ML-KEM-768 + ECDH P-256 + HKDF-SHA256 + AES-256-GCM
2       16    recipient key id
18      65    ephemeral P-256 public key (SEC1 uncompressed)
83      1088  ML-KEM-768 ciphertext (FIPS 203), encapsulated to the recipient's encapsulation key
1171    n     ciphertext
1171+n  16    tag
key ‖ nonce = HKDF-SHA256(IKM = ECDH shared secret (32) ‖ ML-KEM shared secret (32),
                          salt = bytes[18..1171) ‖ bytes[2..18),
                          info = "yfex.recipient-seal.v1" ‖ 0x00 ‖ SealContext.Bytes) → 44 bytes
AAD = bytes[0..1171) ‖ SealContext.Bytes
```

Overhead 1 187 bytes (a CPF becomes 1 198). The salt rule is the classic one generalised ("the header after the key id ‖ key id"), so
both ciphertexts and the key id are bound; the algorithm byte is in the AAD. A hybrid recipient key publishes `P-256 point (65) ‖ ML-KEM-768
encapsulation key (1 184)` = 1 249 bytes with algorithm `0x11`; its ring material is 96 bytes = P-256 scalar ‖ ML-KEM seed `d ‖ z`, both
random and independent (a recovered scalar reveals nothing about the ML-KEM key). The same key also opens classic `0x01` values sealed to
its P-256 half (a sealer without ML-KEM falls back to `0x01`). **Golden:** ML-KEM encapsulation randomness cannot be fixed through the
BCL, so (1) a value sealed 2026-10-18 to the key `scalar 0x01…0x20 ‖ seed 0x41…0x80` is frozen in `HybridRecipientSealingTests` and must
open forever, and (2) a test re-derives the layout above with BCL primitives only (ECDH, `MLKem.Decapsulate`, `HKDF`, `AesGcm`).

**As built 2026-10-05:** exactly this layout. Golden: `RecipientSealingTests.Golden_value_is_rebuilt_byte_for_byte…` (recipient scalar
0x01…0x20, ephemeral scalar 0x21…0x40); points, ECDH secret, key and nonce were computed independently with a pure-Python P-256 + HKDF
(which reproduces NIST CAVS CDH P-256 COUNT 0, also in `KnownAnswerVectors.EcdhP256`), and the value is rebuilt with the BCL `AesGcm`.
The ephemeral point is validated against the curve equation before ECDH (P-256 has cofactor 1, so this is the whole check).

## 4. Password verifier v1

```
0  1   version     0x01
1  1   algorithm   0x01 PBKDF2-HMAC-SHA256 · 0x02 PBKDF2-HMAC-SHA512 · 0x10 Argon2id (reserved, ⚑K14)
2  4   iterations  uint32 LE
6  16  salt        random
22 32  derived     PBKDF2(NFC UTF-8 password, salt, iterations)
```

54 bytes. Verifiers at rest are themselves sealed (sealed value v3, context `yfex.credential-verifier.v1`, parts `[user]`). Copying Ana's
verifier onto Bruno's record therefore fails authentication.

## 5. Signed payloads and detached signatures

```
signature = Sign_algorithm( SHA-256( UTF-8(domain) ‖ 0x00 ‖ payload ) )        // ECDsa: SignHash, IEEE P1363, 64 bytes

DetachedSignature on the wire (canonical):
  algorithm (1) ‖ key id (16) ‖ varint length ‖ signature bytes                 // 82 bytes for EcdsaP256Sha256
```

The **payload** is always canonical bytes (§7). The domain is never on the wire: the verifier knows which kind of object it is checking,
so a bundle signature cannot verify as an envelope.

## 6. Message token v1 (server-issued bearer capabilities)

```
0  1   version    0x01
1  16  key id     ring key that computed the tag
17 8   expires    unix seconds, int64 LE
25 n   payload    caller bytes (e.g. canonical scope identity)
25+n 16 tag       first 16 bytes of HMAC-SHA256(subkey(key, domain), bytes[0..25+n))
text  = base64url(all)
```

41 bytes of overhead, about 55 characters. Used for event-channel audience tokens (fixes C4: expiry, key id, domain).

## 7. Wrapped keys and key-ring files

Two levels, as offline 05 §3 already designed for the storage key:

```
root key provider (TPM/CNG RSA-OAEP, environment, file…)  ──wraps once──►  store key (32 random bytes, one per key-ring store)
store key  ──seals──►  every ring key (sealed value v3, context "yfex.key-wrap.v1" [ring name, key id, purpose])
```

The root provider is touched **once per process start** (TPM unwrap ≈ 75 ms, 01 §4). Ring keys are sealed with AAD that binds ring name,
key id and purpose. A key moved to another ring or relabelled from `Sealing` to `MessageAuthentication` fails to open.

`FileKeyRingStore` file (one per ring), canonical encoding:

```
version 0x01 · ring name · purpose · store-key id · wrapped store key (root provider id + bytes)
entries: [ key id · algorithm · status · validity (ValidityPeriod canonical) · sealed key material ]
tag: HMAC-SHA256(subkey(store key, "yfex.key-ring-file.v1"), all bytes above)
```

*(As built in Phase 0, corrected 2026-10-07: the wrapped store key is **not** inside each ring file. It is one `store.key` file per
store directory: `0x01 · root provider id · wrapped bytes`. A ring file is `0x01 · ring name · purpose · revision · entry count ·
entries [key id · status · validity · sealed key material, empty once destroyed] · tag`.)*

`FileSigningKeyStore` file (`*.signingkey`, one per key name, 14 §2.3; **built 2026-10-18**), same `store.key`, canonical encoding:

```
version 0x01 · key name · algorithm (SignatureAlgorithm) · key id (16 bytes) · sealed private key
sealed private key = sealed value v3 under the store key, context "yfex.signing-key-wrap.v1"
                     [key name, key id (KeyId.ToString()), algorithm byte as invariant decimal ("1")];
plaintext = PKCS#8 PrivateKeyInfo. No file tag: every header field is in the seal context.
```

`FileSecretStore` file (`*.sealedsecret`, one per secret name, **built 2026-10-18**, 12 §4 question 3), same `store.key`:

```
version 0x01 · secret name · sealed value
sealed value = sealed value v3 under the store key, context "yfex.secret-store.v1" [secret name]; plaintext = the secret's bytes.
```

File names for all three file kinds: readable part (non `[A-Za-z0-9.-]` → `_`) + `-` + first 8 hex of SHA-256(UTF-8 name) + extension.
On Unix the store directory is 0700 and every file 0600 (14 §3, F-P1).

The file tag stops someone from flipping a `Retired` key back to `Active` or deleting the newest entry to force an old key. Root provider
ids (`RootKeyFormat`, as implemented in Phase 0): `0x01` CNG Platform (TPM) RSA-OAEP-SHA256, `0x02` CNG software KSP RSA-OAEP-SHA256
(both byte-identical to offline Phase 0's `CngKeyWrapper` blobs), `0x03` ASP.NET Core DataProtection (offline Phase 0 only; not read),
`0x04` provided/environment key (AES-GCM wrap), `0x05` file key (AES-GCM wrap, file permissions only), `0x06` Linux Secret Service key
(libsecret, AES-GCM wrap; `.Linux`, 2026-10-18), `0x07` Apple Keychain key (generic password, AES-GCM wrap; `.Apple`, 2026-10-18),
`0x08` Android Keystore AES-256-GCM key (`.Android`, 2026-10-27): the key never leaves the Keystore and wraps each blob itself, as
`0x08 ‖ nonce (12, chosen by the Keystore) ‖ ciphertext ‖ tag (16)`, AAD = UTF-8 `yfex.root-wrap.android.v1` ‖ `0x08` (14 §9.4).

Named recipient keys in an `ISecretStore` (`SealedRecipientKeyStore`, 14 §10): secret `recipient-key/{name}` holds
`0x01 ‖ P-256 scalar (32) [‖ ML-KEM-768 seed (64)]`; the secret store seals it (`FileSecretStore`: under the root key).

## 8. Algorithm id registry

| Enum (owner: YFex.Cryptography) | Value | Meaning | Status |
|---|---|---|---|
| `SealingAlgorithm` | 0x01 | AES-256-GCM, 96-bit random nonce, 128-bit tag | default |
| | 0x02 | ChaCha20-Poly1305 | implemented; used only without AES-GCM |
| `RecipientSealingAlgorithm` | 0x01 | ECDH P-256 + HKDF-SHA256 + AES-256-GCM | default |
| | 0x11 | hybrid ML-KEM-768 + ECDH P-256 → HKDF-SHA256 → AES-256-GCM | **implemented 2026-10-18** (Phase 5): automatic when the recipient publishes it and the sealer runs ML-KEM (§3) |
| `SignatureAlgorithm` (core) | 0x01 | ECDsa P-256 SHA-256 IEEE P1363 | default |
| | 0x10 | ML-DSA-65 (FIPS 204) | reserved |
| | 0x11 | composite ML-DSA-65 + ECDsa P-256 | reserved |
| Password verifier algorithm | 0x01 / 0x02 | PBKDF2-HMAC-SHA256 / -SHA512 | 0x01 default |
| | 0x10 | Argon2id | reserved (package) |

## 9. Domain registry (every `MessageDomain` and sealing context domain in the ecosystem)

Convention: lowercase, dot-separated, `yfex.<object>.v<n>`. Each owner declares its domains as `static readonly MessageDomain` fields
and has a uniqueness test. This table is the cross-library view:

| Domain | Owner | Used for |
|---|---|---|
| `yfex.store.v1` | Persistence.Encryption | sealing context of `IKeyValueStore` values (parts: store, key) |
| `yfex.secret.v1` | Cryptography `SealedSecretProtector` (was `yfex.web.secret.v1` in Web.Data until 2026-11-05, atlas F08) | secrets in app rows (parts: tenant, table, column, row id) |
| `yfex.secret-store.v1` | YFex.Cryptography | §7 `FileSecretStore` (parts: secret name; built 2026-10-18 as the generic store Automation adopts, 12 §4 question 3) |
| `yfex.credential-verifier.v1` | Offline | offline login verifiers at rest |
| `yfex.key-wrap.v1`, `yfex.key-ring-file.v1` | YFex.Cryptography | §7 |
| `yfex.signing-key-wrap.v1` | YFex.Cryptography | §7 `FileSigningKeyStore` (built 2026-10-18, 14 §2.3) |
| `yfex.recipient-seal.v1` | YFex.Cryptography | §3 |
| `yfex.payment-request.v1` | Payment | MAC of the idempotency fingerprint (C9, written 2026-10-05; Payment will be rewritten) |
| `yfex.event-audience.v1` | Messaging.Rpc | §6 tokens |
| `yfex.approval.v2` | Authorization | approval tokens v2 (HMAC and device/site signatures) |
| `yfex.envelope.v1`, `yfex.receipt.v1`, `yfex.session.v1`, `yfex.bundle.v1`, `yfex.installation-assertion.v1`, `yfex.slice.v1` | Offline | offline 03–08 |
| `yfex.audit.node.v1`, `yfex.audit.chain.v1`, `yfex.audit.anchor.v1`, `yfex.audit.tombstone.v1` | Audit | audit 06 (was `"YFEX-AUDIT-NODE-v1"` etc.; nothing written yet, so the spelling aligns now) |
| `yfex.pseudonym.v1`, `yfex.consent-ledger.v1`, `yfex.subject-data.v1` | Privacy | privacy 03–05 |
| `yfex.backup.manifest.v1`, `yfex.backup.part.v1` | YFex.Backup | manifest signature; seal context of each part of a backup set (parts: set id, part name) — databases-and-backups 06 §6 (DB-P1, 2026-11-12) |

## 10. Canonical encoding (what is signed or hashed)

The writer lives in YFex.Cryptography (namespace `YFex.Canonical`); core holds only the `ICanonicalValue<TSelf>` contract (⚑K2, 03 §2). The specification is Audit 02 §9 (⚑A31). Summary of the rules every format above
relies on:

- **Never JSON** for anything signed or hashed: property order, number formatting and escaping vary by serializer version.
- Unsigned integers as LEB128 varints. Signed as zigzag varints. `WriteFixed64` only where a fixed width is part of a published format.
- Text: Unicode NFC, UTF-8, varint length prefix. `"Farmácia"` typed as precomposed or combining characters encodes the same.
  In an app built with `InvariantGlobalization=true`, .NET does **not** normalize (measured 2026-10-04): the writer then throws
  `NotSupportedException` for non-ASCII text instead of producing different bytes. ASCII is always normalized.
- Decimals normalized (no trailing zeros): `10.50m` and `10.5m` encode the same.
- Instants as UTC ticks. `DateTimeOffset` offsets are not encoded (the same instant from Manaus and São Paulo hashes the same).
- `Guid`/`KeyId` 16 bytes big endian (RFC 9562 order), matching their text form.
- Collections: count prefix, then elements in a **defined** order (by key for maps). The caller sorts; the writer does not.
- Optional values: a presence byte (0/1).
- Value objects: `T.TryWriteCanonical` (`ICanonicalValue<TSelf>`), e.g. `Cpf` → its 11 digits as 11 bytes, never the masked or formatted
  text.

**Legacy exception:** approval token v1 (L3) uses `int32` little-endian length prefixes and fixed 64-bit integers. Its codec reproduces
it byte for byte and is never used for new formats.

## 11. Derived keys and MAC subkeys (as built 2026-10-04)

```
MAC subkey     = HKDF-Expand(SHA-256, PRK = key, info = "yfex.mac|" ‖ ASCII(domain), 32)          cached per (key, domain)
derived bytes  = HKDF-Expand(SHA-256, PRK = master, info = ASCII(label) ‖ 0x00 ‖ context)          KeyDerivation.DeriveKeyMaterial
derived key    = HKDF-Expand(SHA-256, PRK = master, info = ASCII(label) ‖ 0x00 ‖ context ‖ 0x00 ‖ purpose byte)
derived key id = HKDF-Expand(SHA-256, PRK = master, info = "yfex.derived-key-id.v1" ‖ 0x00 ‖ ASCII(label) ‖ 0x00 ‖ context ‖ 0x00 ‖ purpose byte, 16)
text context   = for each part: varint(UTF-8 length) ‖ UTF-8                                        KeyDerivation.WriteContext
```

`ScopedKeyRings` uses label `yfex.scoped-key.v1` (configurable per family) and the scope as the only text part. Labels are a registry
like domains (§9): `yfex.scoped-key.v1` (YFex.Cryptography), `yfex.pseudonym.v1` (Privacy, planned).

**Signing ring keys** (`KeyPurpose.Signing`) are stored like any ring key (§7): the 32-byte ECDsa P-256 private scalar, sealed with
the store key. Keys of non-exportable stores are numbered `KeyId = SHA-256(SubjectPublicKeyInfo)[0..16)` (`PublicKeyRecord.KeyIdOf`).

## 12. Hash chains and Merkle trees (as built 2026-10-05, Phase 3)

```
Genesis = SHA-256( ASCII(domain) ‖ 0x01 ‖ previousChainFinal (32) ‖ chainKey )        previousChainFinal = 32 zero bytes for the first chain
Next(i) = SHA-256( ASCII(domain) ‖ 0x00 ‖ previous (32) ‖ i (8 bytes, little endian) ‖ entry )
Merkle  = RFC 6962 §2.1: leaf = SHA-256(0x00 ‖ entry), node = SHA-256(0x01 ‖ left ‖ right), split at the largest power of two < n,
          empty tree = SHA-256("")
```

A domain is printable ASCII, so the separator byte (0x00 or 0x01) cannot occur inside it, and genesis and next hashes can never be
confused. The chain key and the entry come last, so neither needs a length prefix. Time is part of the owner's canonical entry, never a
chain parameter. **Goldens:** `EvidenceTests` (genesis, a short entry, a 10 000-byte entry) and the 8 RFC 6962 roots plus 6 inclusion
proofs, all computed independently with Python `hashlib`.
