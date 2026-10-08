# 01 — Current state & inventory

Survey of every repo under `C:\Dev\YFex` plus `C:\Dev\YFex.Automation` (2026-10-01), searching for `ConsentLevel`, `Consent*`,
`Scrub`, `Mask`, `Redact`, `Hash`/`HashData`/`HMAC`, `AesGcm`, `Sensitive`, `PII`, `LGPD`, `Anonymi*`, `Pseudonym*`, `salt`, and raw
`string` members named like personal or secret data. Legacy folders (`Act*`, `_Backup`, websites, `Hacks`, `CamillPlan`, `Engine`)
are excluded. Paths are relative to `C:\Dev\YFex` unless noted.

## 1. Consent

| Where | What exists | Defects / observations |
|---|---|---|
| `YFex/src/YFex/Consent/ConsentLevel.cs` | `enum ConsentLevel { None, Functional, Analytics, Full }` | A ladder, not purposes. Cannot express "yes to loyalty e-mails, no to analytics" (LGPD art. 8 §4: consent must refer to determined purposes) |
| `YFex/src/YFex/Consent/ConsentState.cs` | native `union ConsentState(NotAsked, Declined, FunctionalOnly, AnalyticsConsent, FullConsent)`, each with `ConsentedAt` + `PolicyVersion` | Good start (timestamp + policy version). No subject, no channel, no evidence, no history (only the *current* state is stored, so a withdrawal overwrites the grant and the proof is lost) |
| `YFex/src/YFex/Consent/ConsentRecord.cs` | `record ConsentRecord(ConsentState, RecordedAt, PolicyVersion, BrowserDataEnabled, ActiveWindowTitleEnabled, NetworkCaptureEnabled, ProcessDetailEnabled, FileActivityEnabled, TrafficInterceptionEnabled)` | **Not used anywhere.** The six flags are de facto *purposes*, which shows the ladder is not enough. `YFex.Security.Windows/SecurityServiceCollectionExtensions.cs:41` says sensitive watchers "should additionally be gated by the matching `ConsentRecord` flag", but nothing enforces it. Traffic interception and file activity run with no per-source consent |
| `YFex/src/YFex/Consent/IConsentStore.cs` | `Load()`, `Save(ConsentState)`, `HasBeenAsked` | Synchronous, current-state only, no subject parameter (one consent per device/user profile) |
| `YFex.Profiling/YFex.Profiling/Consent/FileConsentStore.cs` | JSON `consent.json` over `ISyncLocalStore` (partitioned per scope; see `custom-logic-integration-plan.md` §12.3) | Persists only `ConsentState`, **drops `ConsentRecord` flags**. Corrupt file → silently `NotAsked`. `DateTimeOffset.Parse` is culture-sensitive. Uses `JsonDocument`/`JsonObject` (allocation, fine on cold path) |
| `YFex.Profiling/YFex.Profiling/Telemetry/ITelemetryConsent.cs`, `TelemetryConsent.cs` | `IsAllowed(ConsentLevel)`, `UpdateConsent`, events `ConsentChanged`, `ConsentDowngraded` | Ladder check. Not thread-safe (`_current` field, no barrier). Events are `Action<>` (no unsubscribe safety) |
| `YFex.Profiling/YFex.Profiling/Telemetry/TelemetryCollector.cs` | Gates each subsystem with `IsAllowed(...)`; on downgrade `async void OnConsentDowngraded` → `FlushAsync` | On withdrawal it **ships** the buffered events collected under the broader consent. That is lawful (art. 8 §5 keeps processing done before withdrawal valid) but surprising; the policy should be explicit (discard or ship, 04 §6). At `Analytics` it collects `machineName` and `userName` |
| `YFex.Profiling/YFex.Profiling/Telemetry/TelemetryEvent.cs` + `TelemetryEventQueueSerializer.cs` | Every event stamps `ConsentAtCollection` | The offline queue serializer stores only the type and **rebuilds it as `new AnalyticsConsent(DateTimeOffset.UtcNow, "restored")`** (lines 64–66): after a restart the proof of which consent covered the event is lost |
| `YFex.Profiling/YFex.Profiling/Unions/DataAvailability.cs` | `Denied(string Reason, ConsentLevel RequiredLevel)` | Becomes `Denied(string Reason, Purpose RequiredPurpose)` (04 §8) |
| Docs: `YFex/docs/public/system.md` §Consent Model, `YFex.System/docs/01-consent-and-privacy.md` | Describe the ladder and the scrubber | Stale namespaces (`YFex.System.Consent`, `YFex.System.Telemetry`; the code is `YFex.Consent` + `YFex.Profiling`). Describe the scrubber as salted hashing "not reversible", which is false for low-entropy inputs (§3) |
| `YFex/docs/internal/logging/07-audit-privacy-compliance.md` §6 | Logging joins Profiling sessions only when consent allows | Assumes a consent attribute per session. Covered by 04 §9 |

## 2. Scrubbing and masking

| Where | What exists | Defects |
|---|---|---|
| `YFex.Profiling/YFex.Profiling/Telemetry/PrivacyScrubber.cs` | `Scrub(TelemetryEvent, salt)` walks property **keys**: `url/uri` → host hash, `*title*` and `*mac*` → hash, `*ip*` (not `zip`/`script`) → IP mask | **No value patterns at all** (no CPF/CNPJ/e-mail/phone/card/JWT), contrary to logging 07 §3.3. Substring matching on keys misfires: `"multipleMonitors"` / `"shipping"` / `"recipient"` contain `ip` → replaced by `"masked"`; `"machineName"` gets hashed only because it contains `mac`; `"userName"` passes in clear. `MaskIpAddress` uses `Split`, `Contains`, string concatenation (allocations on every event). `HashUrl` swallows all exceptions with a bare `catch` |
| `YFex.Profiling/YFex.Profiling/Telemetry/TelemetryBatch.cs:99` | `batch.Select(e => PrivacyScrubber.Scrub(e, salt)).ToList()` | LINQ + a new dictionary per event per batch |
| `YFex.System/YFex.System.Windows/WindowsSystemInfoProvider.cs:51,108` | `HashedMac = SHA256(mac)` **without salt**; private copy of `MaskIpAddress` | A MAC is 48 bits with a public vendor prefix (OUI): the unsalted hash is reversible by enumeration. Duplicate masking code |
| `YFex.Search/src/YFex.Search.Public` (`FormatKind`, `IFormatDetector`, `QueryAnalyzer`) | Detects `Cpf`, `Cnpj`, `CpfCnpj`, `Rg`, `Email`, `Telefone`, `Ean` in search **queries** | Not a scrubber (it classifies a whole query) but the detection logic overlaps with 06. Search queries typed by users often *are* a CPF and end up in caches/frecency (08 §11) |
| `YFex.Authorization/src/YFex.Authorization.Public` `AccessToken` | `ToString()` returns `"Bearer ****"` | **Good pattern** to generalize: secret types never print their value (YFP0005) |
| `YFex.Payment/src/YFex.Payment/Providers/PaymentProviderBase.cs:37` | "Default: nothing (bodies may contain PII)" when extracting provider errors | Good instinct, manual. Covered by the scrubber in 06 |

## 3. Hashing of personal values

| Where | Construction | Problem |
|---|---|---|
| `PrivacyScrubber.HashString` (Profiling) | `SHA256(UTF8(value + salt))` | Salt = `TelemetryOptions.TransportSaltKey`, configuration text (docs example: `"my-app.telemetry"`). Anyone with the salt can enumerate IPs/hosts/titles. Not an HMAC. Concatenation ambiguity (`"ab"+"c"` = `"a"+"bc"`) |
| `YFex.Profiling/YFex.Profiling/Data/UrlCategorizer.cs:46` | `SHA256(host + salt)` | Same |
| `YFex.Profiling/YFex.Profiling.Windows/WindowsHardwareFingerprint.cs:28` | `SHA256(machineGuid + salt)` | Acceptable for a device id (high entropy), but should be a keyed pseudonym with a domain (05 §3) |
| `YFex.System/.../WindowsSystemInfoProvider.cs:51` | `SHA256(mac)` | No key at all |
| `YFex.Data/src/YFex.DataMining/Projects/ProjectSignature.cs:50`, `YFex.Payment/.../CreatePaymentValidator.cs:65`, `RoselYn.MCP`, `FileScanner` | SHA-256 of non-personal content (signatures, idempotency, file hashes) | Not privacy-relevant. **Out of scope** (listed so nobody migrates them by mistake) |

## 4. Cryptography and keys

| Where | What | Notes |
|---|---|---|
| `YFex/src/Persistence/YFex.Persistence.Encryption/` | `IValueProtector` (`Protect/Unprotect` → `byte[]`), `AesGcmValueProtector` (blob `0x01 ‖ nonce(12) ‖ ciphertext ‖ tag(16)`), `IKeyProvider` (`GetKey()` → 32 bytes), `DataProtectionKeyProvider` (HKDF from an ASP.NET DataProtection-protected seed, info `"YFex.AesGcm.StorageKey.v1"`), `ProvidedKeyProvider`, `EncryptedKeyValueStore` (decorator), `AddYFexStorageEncryption(EncryptionKeySource)` | **Whole-store, single key per device.** No key id in the blob → no rotation. No AAD (a ciphertext can be moved to another key). Allocates per call. This becomes a thin adapter over the envelope (05 §6), keeping a reader for version 0x01 |
| `YFex/src/Messaging/YFex.Messaging.Rpc/YFexMessagingRpcClientOptions.cs` | `enum EncryptionMode { None, AesGcm }`, default `None` | Option for encrypting the client outbox/cache. Tests in `Messaging.Tests/Encryption` use `EncryptedKeyValueStore`. `YFex/CLAUDE.md` lists a `YFex.Messaging.Rpc.Encryption` project, but **no such folder exists**: encryption comes from `YFex.Persistence.Encryption` |
| `YFex.Web/src/Web/YFex.Web.Data/Security/AesSecretProtector.cs` | AES-GCM, `nonce ‖ ciphertext ‖ tag`, base64; optional `HKDF(key, TenantSalt)` per tenant | **Second implementation**, different layout (no version byte). Protects `ApiRow.Secret/Token/ApiKey` (API credentials, not personal data) |
| `YFex/src/Validation/YFex.Rules/Approvals/OverrideTokens.cs` | HMAC-SHA256 **key ring by key id** (current + previous keys, ≥ 32 bytes), ECDsa P-256 device signatures for offline approvals (D34) | The best existing key-ring code. The pattern generalizes to `KeyRing` (05 §5). Device-signing is reused for offline consent entries (04 §7) |
| `YFex/src/Messaging/YFex.Messaging.Rpc/EventChannel.cs` | HMAC-SHA256 audience tokens, key random per process | Not personal data; could use `KeyRing` for multi-instance servers (suggestion only) |
| `C:\Dev\YFex.Automation\src\…\Secrets\` | `ISecretStore` (string get/set); `DpapiSecretStore` (Windows, `ProtectedData`), `LinuxSecretStore`, `PortableSecretStore` | **`PortableSecretStore` writes secrets in plain text** to `*.secret` files (env vars first). Secrets are app credentials, not personal data, but the root-key providers can be shared (05 §5.2) |
| `YFex.Web/src/Web/YFex.Web/Credentials/*.cs` | `record BasicCredential(string Username, string Secret)`, `record BearerTokenCredential(string Token)`, `OAuthClientCredential(…, string Secret, …)` | **Compiler-generated record `ToString()` prints the secret** (no `PrintMembers` override). Any log/exception that formats the credential leaks it (YFP0005) |

## 5. Personal data holders (sample, not exhaustive: the generator inventory in 03 replaces this table)

| Where | Members | Observation |
|---|---|---|
| `YFex.Domain/src/YFex.Entity.Public` (namespace `YFex.Entity.Public`) | `Cpf`, `Cnpj`, `CpfCnpj` (`[Union]`), `Email`, `Telefone`, `Rg`, `FullName`, `CompanyName`, `PartyName` (`[Union]` Person \| Company), `InscricaoEstadual` | **The value objects already exist.** None carries a classification. `CompanyName.cs` has **no namespace declaration** (the type lives in the global namespace; tools display it as `CompanyName.CompanyName`) |
| `YFex.Farma/app/YFexFarma.Desktop.Presentation/` | `CustomerModel`, `AddCustomerViewModel`: `string CpfCnpj, Phone, Email, Address, Cep`; `SupplierCrudModel`, `ConvenioCrudModel`, `LabCrudModel`: `string Cnpj, Phone, Email, Cep, Address` | Raw strings for data that has value objects (YFP0001). A pharmacy will also hold **health data** (medication history, prescriptions, SNGPC controlled drugs): `SensitivePersonal`, art. 11 |
| `YFex.Farma/src/YFexFarma.Entity.Public/Customer.cs:23` | `record CreateCustomerCommand(string Name, string Email)` | Should be `(FullName Name, Email Email)` |
| `YFex.Payment/src/YFex.Payment.Public/Payer.cs` | `CpfCnpj? Document` (good), `string? Email`, `string? Phone`, `ZipCode` | Mixed |
| `YFex.Brain/YFex.Brain.Entity` | `Entity { CpfCnpj?, DisplayName, LegalName, RiskScore, Contacts (Email/Telefone), Addresses (street…lat/long), Identifiers (IdentifierType + string Value: Rg, Nis, Pis, Passport…), Sources (RawPayload string) }` | **A person/company master-data store**: the largest personal-data concentration in the ecosystem. `RawPayload` is unstructured personal data with no retention. `RiskScore` is profiling with possible automated decisions (art. 20: right to review). `EntityIdentifier.Value` sensitivity depends on `Type` (02 §5.4) |
| `YFex.Data/src/YFex.DataMining` | `AnchorPack`/`TargetSchemaDefinition` semantic tags (`BrCpf`, `BrCnpjOrCpf`, `Email`, `Phone`, `BrCep`); `AiSemanticWorm` prompt has "Sample values: (TODO: pull from SampleAsync)" | Semantic tags are a ready-made **classifier for databases we do not model in C#** (03 §5.3). Once samples are wired, real customer values will be sent to an LLM (08 §8.2) |
| `YFex.Web/src/Web/YFex.Web.Data/Storage/ApiRow.cs` | `Secret`, `Token`, `ApiKey` (protected) | Secrets, not personal data |
| Logging design (`docs/internal/logging/02 §5.2`) | `[Sensitive(DataSensitivity, Mask)]`, `DataSensitivity` with 6 values; CPF → `SensitivePersonal` | Moves here, with the legal correction of ⚑P3 |

**Bare `Name` is noise.** A grep for `string Name` finds 32 hits in YFex.Data alone (schema/table names), 20 in YFex core, 15 in
Automation. None of them is personal. The analyzer must never flag a bare `Name` (03 §4.2).

## 6. Gaps (what does not exist anywhere)

1. A classification of data types that every library can read.
2. Purposes and legal bases; consent per purpose; consent history and proof; withdrawal propagation to servers and devices.
3. A keyed pseudonymization primitive; blind indexes for searching encrypted values.
4. Per-data-subject keys (crypto-shredding), a key vault, key ids in ciphertexts, rotation.
5. A data inventory ("where do we store CPF?"), a ROPA (art. 37 record of processing), retention per category, legal holds.
6. Data subject request handling (art. 18/19: confirmation, access, correction, anonymization, deletion, portability, sharing info,
   revocation) across stores.
7. Redaction before AI calls and for AI tool results.
8. Analyzers that stop new raw `string Cpf` members, unkeyed hashes and secrets in `ToString()`.

## 7. Old → new mapping

| Today | Becomes | Migration (11 §3) |
|---|---|---|
| `YFex.Consent.ConsentLevel` (core) | `ConsentPreset` in YFex.Profiling: named bundles of `TelemetryPurposes` (04 §8) | `[Obsolete]` in core for one release, then deleted |
| `YFex.Consent.ConsentState` / `ConsentRecord` / `IConsentStore` (core) | `YFex.Privacy.Consent`: `ConsentEntry`, `ConsentSnapshot`, `IConsentLedger`, `ConsentClient` | Converter imports `consent.json` into one ledger entry per purpose of the preset, keeping the original timestamp and policy version |
| `ITelemetryConsent.IsAllowed(ConsentLevel)` | `ConsentSnapshot.Allows(Purpose)` | Shim `IsAllowed(level)` → `Allows(preset purposes)` for one release |
| `Denied(Reason, ConsentLevel RequiredLevel)` | `Denied(Reason, Purpose RequiredPurpose)` | Mechanical |
| `ConsentRecord.*Enabled` flags | Purposes `telemetry.browser-data`, `telemetry.active-window-title`, `security.network-capture`, `security.process-detail`, `security.file-activity`, `security.traffic-interception` | Enforced at subscription time (08 §12) |
| `PrivacyScrubber` (Profiling) | `YFex.Privacy.TextScrubber` + `PropertyRedactor` (06) | Shim class delegating for one release; `TelemetryBatch` uses the new API without LINQ |
| `SHA256(value + salt)` ×4 | `Pseudonymizer` with domain `"telemetry"` | Pseudonyms change once; server-side joins on old hashes end (documented) |
| `MaskIpAddress` ×2 | `Masks.IpAddress` (05 §2) | Delete duplicates |
| `IValueProtector`/`IKeyProvider`/`AesGcmValueProtector` | Adapter over `AesGcmEnvelope` + `IKeyVault` device key | Reader for 0x01 blobs kept; new writes use envelope v2 |
| `AesSecretProtector` (Web.Data) | Adapter over `AesGcmEnvelope` with a tenant field key | Reader for legacy layout selected by column marker (05 §6.3) |
| `OverrideTokens` HMAC ring | `KeyRing` (05 §5.3) | Optional; same algorithms |
| `TelemetryOptions.TransportSaltKey` | Tenant/app pseudonym key from `IKeyVault` | Option removed after the shim release |
