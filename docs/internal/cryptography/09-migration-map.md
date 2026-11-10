# 09 — Migration map

## 1. Every implementation → its replacement

> **Update 2026-10-04.** Offline Phase 0 (2026-10-03) already fixed F1, F2, C3 and C10 **inside** Persistence.Encryption, before this
> library exists: `IKeyWrapper` + `CngKeyWrapper` (TPM → software KSP, RSA-OAEP-SHA256) + `DataProtectionKeyWrapper`,
> `WrappedKeyProvider` (random storage key generated once), layout `0x02` with AAD `"yfex.store|" + store + "|" + key`,
> `UnprotectStatus`, RPC options removed. Rows I1–I6 therefore describe the move **from that interim code**: `IKeyWrapper` →
> `IRootKeyProvider` (the CNG key name and the stored wrapped key are kept, so existing terminals keep their storage key),
> `CngKeyWrapper` → `CngRootKeyProvider`, layout `0x02` → legacy reader L1b (05 §2), `UnprotectStatus` → `OpenResult`. Still open
> there: C1 (one shared `AesGcm`) and C8 (`ActivatorUtilities`, DataProtection package).

| # (01) | Today | After | Data / format compatibility | Phase |
|---|---|---|---|---|
| I1 | `AesGcmValueProtector` (`0x01`, no AAD, shared `AesGcm`) | removed. `EncryptedKeyValueStore` calls `SealedValue` with ring `device/storage`, context `yfex.store.v1 [store, key]` | reads L1 through `LegacyFormats.OpenPersistenceVersion1` (05 §2), re-seals on the next write | 0 |
| I2 | `DataProtectionKeyProvider` | removed. Root-key ladder (06 §3): `CngRootKeyProvider` on Windows, `FileRootKeyProvider`/`EnvironmentRootKeyProvider` elsewhere | **F1 values cannot be recovered** (their key no longer exists in any process). §2 explains the deploy rule | 0 |
| I3 | `ProvidedKeyProvider` + `IKeyProvider` | `storage.LegacyKey(bytes)` (read-only key for L1 values) + any root provider for new data. `IKeyProvider` `[Obsolete]` for one release | L1 values written with a provided key **are** readable (that key was stable) | 0 |
| I4 | `EncryptedKeyValueStore` (`null` on any failure) | same type, rebuilt: `null` only for "absent" and for `KeyDestroyed` (erased = absent); `AuthenticationFailed`/`KeyUnknown`/`UnsupportedFormat` raise `StorageIntegrityException` + probe; new `TryGetAsync` → `StoredValueResult` union for callers that want the detail (outbox replay, diagnostics) | — | 0 |
| I5 | `AddYFexStorageEncryption(EncryptionKeySource, byte[]?)`, `ActivatorUtilities` | `AddYFexStorageEncryption(storage => storage.KeyRing("device/storage").LegacyKey(…))` on top of `AddYFexCryptography(...)`. The inner store is re-registered as a **keyed** service (`"yfex.storage.inner"`) instead of `ActivatorUtilities` (C8). `Microsoft.AspNetCore.DataProtection` package removed | `EncryptionKeySource` `[Obsolete]`: `OsKeyStore` → platform default root, `Provided` → `LegacyKey` + `ProvidedRootKeyProvider` | 0 |
| I6 | `YFexMessagingRpcClientOptions.Encryption`, `.EncryptionKeySource`, both enums | **removed** with `[Obsolete(error: true)]` pointing to `AddYFexStorageEncryption` (they never did anything, C3). Fixes the F12 duplicate enum | none (no data was ever encrypted through them) | 0 |
| I7 | `EventChannel` HMAC audience token, random per-process key | `MessageToken` (05 §6), ring `messaging/event-audience`, domain `yfex.event-audience.v1`, `EventChannelOptions.AudienceTokenLifetime` (default 12 h); `SigningKey` `[Obsolete]` | old tokens fail once, and clients re-fetch (same as after a restart today) | 1 |
| I8 | Rules `OverrideTokens` (`HmacOverrideTokens`, `DeviceOverrideTokenIssuer/Verifier`, `IDeviceKeyStore`) | Authorization `ApprovalToken` v2 (⚑Z12 + ⚑K12): HMAC through `MessageAuthentication` + ring `approvals/server`; device and site tokens through the installation evidence `ISigningKey` + `SignatureVerifier`; `IDeviceKeyStore` → Offline `IInstallationKeyRegistry : IPublicKeyDirectory` | `ApprovalTokenVersion1Codec` verifies v1 **byte-identically** (golden, §3) for one `MaxAge` after v2 ships | 2 |
| I9 | `Evaluator.Fingerprint` (XXH64) as the approval binding | unchanged for equality and UI; new `Decision.ComputeDigest()` → `Hash256` over the canonical outcome (core only, no library reference); v2 tokens sign it (C5) | — | 2 |
| I10 | `AesSecretProtector` (+ unused `TenantSalt`) | `SealedSecretProtector : ISecretProtector` in Web.Data: ring `web/secrets` (purpose `Derivation`), per-tenant key **derived** with the tenant as context (fixes C2), context `yfex.web.secret.v1 [tenant, table, column, row]` | L2 values (no `y3.` prefix) open with the configured legacy key and are re-sealed on the next save | 1 |
| I11 | `ISecretProtector.Protect(string)` | adds `Protect(string plaintext, in SecretLocation location)` / `Unprotect(...)`. The location-less overloads become `[Obsolete]` (they cannot bind AAD) | — | 1 |
| F08 | Web `ISecretProtector` + Web.Data `SealedSecretProtector` (2026-11-05, atlas F08) | moved to **YFex.Cryptography** (`ISecretProtector`, `SecretLocation`, `UnprotectedSecret`, `SecretIntegrityException`, `SealedSecretProtector`, `AddYFexSecretProtection`); ring family `secrets`, context `yfex.secret.v1`. Deleted: `AesSecretProtector`, the obsolete location-less members, `LegacyKey`, `LegacyFormats.OpenWebSecretVersion1`, `ApiEndpointSecretMigration` | clean break (nothing in production): L2 and `yfex.web.secret.v1` values no longer open | — |
| I13, I14 | Profiling/System `SHA256(value + salt)`, unsalted MAC hash | Privacy `Pseudonymizer` (privacy ⚑P11) built on `MessageAuthentication`/`KeyDerivation` | old hashes are not joinable with new pseudonyms (intended) | Privacy Tier 2 |
| I15 | Automation `PortableSecretStore`, `LinuxSecretStore` (plain text) | `SealedFileSecretStore` in Automation.Core: root ladder (`CngRootKeyProvider` on Windows, `FileRootKeyProvider` 0600 elsewhere), context `yfex.secret-store.v1 [key]` | first read of a plain-text `*.secret` file re-seals it and deletes the plain file | 1 |
| I16 | Automation `DpapiSecretStore` | unchanged (its `ProtectedData` package is Automation's existing dependency); may switch to `SealedFileSecretStore` + CNG later | — | owner's choice |
| I17 | Payment `WebhookSecurity` | unchanged (P-class) | — | — |
| I18 | Payment idempotency fingerprint | `CanonicalWriter` + `Hash256`; keyed `MessageAuthentication` if it is persisted or logged (C9) | fingerprints change once. In-flight retries across the upgrade are treated as new (acceptable: providers keep their own idempotency) | 1 — **written 2026-10-05** (`PaymentRequestFingerprints`); not wired: Payment does not compile and will be rewritten (12 §5) |
| I20 | `FileScanner` obsolete `X509Certificate2` constructor | `X509CertificateLoader` (or `CertificateFiles.Load`, Phase 3) | — | when touched |
| I27 | Audit `Digest` (SHA-256 in `AuditValueEncoding`) | core `Hash256`; personal content uses the Privacy treatments (A-1) | — | Audit session |
| design | Privacy 05 `KeyId(Guid, byte)`, `KeyLease`, `IRootKeyProvider`, `AesGcmEnvelope`, `EnvelopeOpenStatus`, `KeyRing` | `KeyId`, `SecretKey`/`KeyLease`, `IRootKeyProvider`, `SealedValue`, `OpenResult`, `KeyRing` | nothing written yet | doc amendment |
| design | Offline 03/05: `IInstallationKeyStore` internals, `CngRootKeyProvider`, bundle signing ring, `Hash256 PayloadHash`, string `KeyId` | `ISigningKeyStore` + `CngSigningKeyStore`, `.Windows` `CngRootKeyProvider`, `PublicKeyRecord`s, core `Hash256`, `KeyId`, `DetachedSignature` | nothing written yet | doc amendment |
| design | Audit 06 `Hash256`/`HashChain` in core, uppercase domains, string `KeyId` | `Hash256` core, `HashChain`/`MerkleTree` library (⚑K11), `yfex.audit.*.v1` domains, `KeyId` | nothing written yet | doc amendment |

> **Phase 1 status (2026-10-04):** **I10/I11 DONE** (Web.Data `SealedSecretProtector`, L2 golden, `AddProtectedApiEndpoints`; 12 §5).
> **I7 DONE** (EventChannel `MessageToken`, shared ring via `cryptography.EventAudienceTokens()`; 12 §5).
> **I18 (2026-10-05):** keyed fingerprint written in Payment; the repo is left as is (user: Payment will be rewritten).
> The library side of I15, I18 is ready (`MessageToken`, `ScopedKeyRings`,
> `KeyDerivation`, `MessageAuthentication`, `CanonicalWriter`); the consumers themselves are not migrated yet. For I8 (Phase 2), the
> signing slice and the L3 golden (`YFex.Rules.Tests/Golden/approval-token-v1.tsv`) exist; the move itself is Authorization's Phase 4.

> **Phase 0 status (2026-10-04):** I1–I6 are **done** (log in 12 §5). As built: there is no `storage.LegacyKey(…)`; the offline Phase 0
> storage key is imported automatically (`ImportLegacyStorageKey`, default on) under `EncryptedKeyValueStore.LegacyStorageKeyId`, and an
> app-supplied key is `storage.UseKey(bytes)`. The options are properties (`storage.StoreName = "farma"`), the ring defaults to
> `"device/storage"`. The `yfex-storage report` tool is not built yet (F1-era values are counted by `UnreadableValues` instead).

## 2. How F1 and F2 are fixed through the library

*(Interim fix already shipped by offline Phase 0, see §1. This section describes the end state once the library takes over.)*

```csharp
// ✍ Before — terminal composition root (F1: key derived from randomized DataProtection output; F2: no AAD)
services.AddYFexSqliteStorage(path);
services.AddYFexStorageEncryption(EncryptionKeySource.OsKeyStore);

// ✍ After
services.AddYFexCryptography(cryptography => cryptography
    .UseCngRootKey()                                   // TPM if present, else software KSP; reports KeyProtection
    .StoreKeyRingsInFiles(AppPaths.Data("keys")));
services.AddYFexSqliteStorage(path);
services.AddYFexStorageEncryption(storage => storage.StoreName = "farma");   // ring "device/storage" by default
```

What happens at the first start after the upgrade:

1. `KeyRingProvider` finds no `device/storage` ring. It generates a **random** store key and a random ring key, wraps the store key with
   the TPM root (one RSA-OAEP operation) and writes the ring file. From then on every start unwraps the **same** key (≈ 75 ms once):
   **F1 fixed**.
2. Every `SetAsync("outbox:042", bytes)` seals with AAD `yfex.store.v1 [farma, outbox:042]`. A value copied to `outbox:043` returns
   `AuthenticationFailed`: **F2 fixed**.
3. `GetAsync` on an old `0x01` value tries the legacy key (if one was provided). Otherwise the value is counted, logged once as
   `StorageKeyMismatch`, and surfaced as `StorageIntegrityException`, not as "missing" (C10).

**Deploy rule for F1 data (unrecoverable by construction):** before upgrading a terminal that used `EncryptionKeySource.OsKeyStore`,
let it sync online so the outbox is empty. Phase 0 also ships a one-off tool `yfex-storage report` that counts unreadable `0x01` values,
so support can see whether a store lost queued operations in the past.

## 3. Golden and compatibility tests

| Format | How the golden is produced | Assertion |
|---|---|---|
| L1 Persistence `0x01` | **before** any change, the current `AesGcmValueProtector` seals 5 known plaintexts with a fixed provided key; the bytes and key are committed as `Golden/persistence-v1.hex` | the new legacy reader opens all five, `NeedsReseal = true`, and the re-sealed value is v2 |
| L2 Web.Data | same, with the current `AesSecretProtector` → `Golden/web-secret-v1.txt` | opens, re-seals with the `y3.` prefix |
| L3 approval token v1 | the current `HmacOverrideTokens` and `DeviceOverrideTokenIssuer` issue tokens with fixed keys, fixed time and fixed fingerprints → `Golden/approval-token-v1.hex` (payload + signature). The existing `ApprovalTokenTests` cases are re-run against the new codec | `ApprovalTokenVersion1Codec` produces **byte-identical payloads** and verifies both signatures (the HMAC is byte-identical; ECDsa signatures are randomized, so they are verified, not compared) |
| Sealed value v3, recipient seal v1, password verifier v1, message token v1, detached signature, key-ring file | fixed test keys + the internal fixed-nonce seam | byte-identical output, and opens again. Changing a format without a new version byte fails these tests |

All goldens are generated in **step 0 of their phase, with the code that exists today**, before any refactor touches it.

## 4. Doc amendments when ⚑K1 is confirmed

| Doc | Change |
|---|---|
| privacy 05 §5.2, §5.3, §6 | point to this design for `KeyId`, `KeyLease`→`SecretKey`, `IRootKeyProvider`, `KeyRing`, envelope v2 → sealed value v3 (05 §1); keep pseudonyms, vault, tokens, shredding |
| privacy README ⚑P12/P13 | "mechanism: YFex.Cryptography (⚑K6, ⚑K4)" |
| offline 03 §2, 05 §2–§3, README ⚑O5/O8/O9 | key stores via `ISigningKeyStore`/`CngSigningKeyStore`; `CngRootKeyProvider` lives in `YFex.Cryptography.Windows`; `BundleHeader.KeyId` → `KeyId`, signature → `DetachedSignature` |
| audit 06 §2, §3, §10, README ⚑A31 | `Hash256` type in core (✅ 2026-10-04), canonical writer in the library (`YFex.Canonical` namespace), `HashChain`/`MerkleTree` in the library; domains `yfex.audit.*.v1`; anchor keys = `ISigningKey` |
| authorization-consolidation 06 §1.1 | approval tokens v2 on YFex.Cryptography; v1 codec; decision digest |
| YFex CLAUDE.md | 03 §5 |
