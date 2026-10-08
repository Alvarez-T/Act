# 11 — Phases, acceptance and implementation log

## 1. Working rules (every phase)

- `C:\Dev\YFex` is **not a git repository** and edits are live. Before touching a file, copy it to `C:\Temp\offline-backup\<phase>\`
  with the same relative path.
- Check modification times before editing shared files. Parallel sessions own: Authorization consolidation (Cqrs, Messaging.Rpc
  CallContext, Rules approvals), Logging Phase 2 (Cqrs, Messaging.Rpc), Privacy Tier 1+ (core, `CLAUDE.md`), Audit (core `YFex.Auditing`).
  Coordinate through their docs' implementation logs.
- Use the RoselYn MCP tools for C# exploration and `dry_run_compile` before writing non-trivial edits.
- No new NuGet package without asking the user. Everything here is BCL + the ASP.NET Core shared framework.
- Record test counts before and after in §5, and update the memory file `yfex-offline.md`.
- Native AOT smoke must stay at 0 warnings for every touched library that has one.

## 2. Dependencies on other designs

| This design needs | From | Phase here |
|---|---|---|
| core `ActorContext`/`ActorIdentity` (⚑Z3) | authorization consolidation Phase 1–2 | 3 |
| `PermissionSnapshot` moved out of Rpc + client `UseServerPermissions()` (⚑Z9) | authorization Phase 3a ✅ 2026-10-04 (payload only, online); signed + persisted = authorization Phase 3b | 2 (signing) / 3 (use) |
| approval tokens in Authorization (⚑Z12) | authorization Phase 4 | 3 (as-of), 4 (site kind) |
| `TrustedTimeProvider` (⚑O10) | the shared `YFex.Time` library (`../time/README.md`, ⚑T1–T12 approved 2026-10-04/05; core types in namespace `YFex.Time` replace the planned `YFex.Offline` ones; T0+T1 in progress) | 1 (adopt), 4 (site time) |
| `IRootKeyProvider`, key rings, sealed value v3 (was privacy's envelope v2) | **YFex.Cryptography** Phase 0 — ✅ 2026-10-04 | 0 (done), 1 |
| core `Hash256`, `KeyId`, `DetachedSignature`, `SignatureAlgorithm`, `ICanonicalValue<T>` | YFex core (cryptography ⚑K2, types only) — ✅ 2026-10-04 | 1 (bundle header, envelope) |
| canonical writer, `MessageAuthentication`, `PasswordHashing` | YFex.Cryptography Phases 0–1 (writer ✅) | 1–2 (codecs, offline login) |
| `ISigningKeyStore` + `CngSigningKeyStore`, `SignedPayload`, `SignatureVerifier`, `RecipientSealing` | YFex.Cryptography Phase 2 | 1–3 (installation keys, bundles, envelopes) |
| `CertificateAuthority`, `CertificateTrust`, `HashChain` | YFex.Cryptography Phase 3 | 1 (tenant CA, mTLS), 3 (installation sequence) |
| audit journal + shipper, logging shipper | audit / logging phases | 4 (relays) |
| installation identity, keys, enrollment, trust/friends, signed bundles + floors, peer channels, relay, pairing | **YFex.Installations** ([../installations](../installations/README.md), ⚑I1–I20, proposal 2026-10-05) | 1–5 |

## 3. Phases

### Phase 0 — Fix existing defects (independent of the offline-design decisions; needs ⚑O9, ⚑O16 only for direction, ⚑O22)

| Task | Change | Acceptance |
|---|---|---|
| 0.1 F1 | `Persistence.Encryption`: random 32-byte storage key generated once, stored wrapped. Interim wrapping provider `CngStorageKeyProvider` (Windows, package-free), plus the existing `ProvidedKeyProvider`. `DataProtectionKeyProvider` marked obsolete with a pointer | two-process test: write, restart, read back. Unreadable `0x01` values logged once as `StorageKeyMismatch` |
| 0.2 F2 | AES-GCM with `AAD = "yfex.store|" + store + "|" + key` (layout `0x02`, reads `0x01`) | value copied to another key fails; legacy value readable and re-sealed on write |
| 0.3 F3 | `yfex-idempotency` header on every command call (online + replay). Server `IdempotentHandlerInvoker` decorator + `IIdempotencyStore` (in-memory; SQL later). `CommandPolicy.IdempotencyKey` honoured when configured | same command sent twice after a simulated lost reply → handler ran once, second call gets the stored result |
| 0.4 F4 (interim) | `OutboxEntry` captures `Subject`, `Scope`, `InstallationSequence` at enqueue. Replay sends the **captured** scope header. A different connected subject → warning log + `SyncStatus` notice. Full fix in Phase 3 | cross-scope replay impossible; test with a scope switch between enqueue and replay |
| 0.5 F5 | replay order by `InstallationSequence`; TTL expiry raises an alert and keeps the entry when `KeepExpired = true` (default true) | clock set back between enqueues does not reorder |
| 0.6 F12 | single `EncryptionKeySource` (Persistence.Encryption); doc map in `YFex/CLAUDE.md` corrected | builds; doc paths exist |

### Phase 1 — Contracts and the YFex.Offline repo

> **2026-10-05 (⚑N6):** the `YFex.Installations` core types, `IInstallationKeyStore`, the signed-bundle format and
> `IDeviceKeyStore` → `InstallationKeyDirectory` move to **YFex.Installations** Phases I0–I1 ([../installations](../installations/README.md), 08). This
> phase then needs I0–I1 and keeps only the `YFex.Offline` contracts. Mapping: Phase 2 central trust (CA, enrollment, assertion,
> revocation/roster bundles) → I2; Phase 4 mTLS host, validator, discovery, site/peer time transport → I3–I4 (broker services,
> site time table, custody, feed stay here); Phase 5 offline pairing → I5.

- Core `YFex/src/YFex/Installations/` (namespace `YFex.Installations`): `InstallationId`, `InstallationIdentity`, `LocalInstallation`,
  `InstallationRole`.
- Core `YFex/src/YFex/Offline/` (namespace `YFex.Offline`): `SiteTopology`, `ConnectivityMode`, `IConnectivity`, `Reachability`,
  `BundleKind`, `BundleHeader`, `SignedBundle`, `OfflineProvenance`, `OfflineFlags`, `OfflineEnvelope`.
- Time: no own clock types. Register YFex.Time (`AddYFexTime`) in the harness and use core `YFex.Time.TrustedInstant` in envelopes and
  provenance (05 §4).
- Repo `C:\Dev\YFex\YFex.Offline` (`offline.slnx`): `YFex.Offline` (key store contracts, bundle codec and
  verifier, envelope codec), `YFex.Offline.Windows` (thin `IInstallationKeyStore` over YFex.Cryptography.Windows' `CngSigningKeyStore`; `CngRootKeyProvider`
  already lives in YFex.Cryptography.Windows), `YFex.Offline.Testing` (harness
  skeleton, fake clock), `.Tests`, `.AotSmoke`.
- `IDeviceKeyStore` (Rules) → `IInstallationKeyRegistry` (coordinated with ⚑Z12).
- **Acceptance:** golden encodings pinned. Envelope carries a `TrustedInstant`; rollback flags reach the envelope (clock tests live in YFex.Time). CNG key created non-exportable (TPM when present). AOT smoke
  at 0 warnings.

### Phase 2 — Central trust and signed data

- `YFex.Offline.Central`: tenant CA, enrollment codes, installation certificates (URN SANs), manifest and revocation bundles, `IBundleSigner`,
  delivery receipts, installation assertion validation on the WebSocket upgrade.
- Rules.Admin publish pipeline signs rule-data versions; `RuleSetSync`/`KeyValueRuleDataCache` verify with floors (fixes F6).
- Signed `PermissionSnapshot` (with the authorization Z9 phase).
- **Acceptance:** S4 (tampered table) passes. Enrollment end-to-end in the harness. Revocation reaches a connected installation within one
  push.

### Phase 3 — The lone terminal, end to end

- Offline login (cached verifiers, sessions, lockout); `.Offline(...)` verb (`RecordsFact`, `Consumes`, `ClaimsUnique`,
  `SharedWithSite`, `Lane`) next to the existing `.OnOffline<T>()`, compiled availability, `ErrorType.Unavailable` (wire v2,
  additive); envelopes replace the outbox payload; installation replay with the envelope actor (full F4 fix).
- Central reconciliation pipeline (08 §3) with facts/requests, `IFactReconciler<T>`, findings, `IOfflineReviewQueue` (in-memory +
  query); approvals as of the operation time (F7); budgets granted directly to terminals.
- **Acceptance:** S2 (standalone column), S3, S5, S6 pass in the harness. Property test "exactly once + no overspend" under random
  partitions passes. Benchmarks meet 10 §4.

### Phase 4 — The site broker

- `YFex.Offline.Broker`: Kestrel mTLS host + validator, two Fusion peers + router, failover state machine, discovery (manifest +
  beacon), custody + site journal + receipts, coordination services (budget reservations and slices, unique claims), site feed +
  `ISiteFeedProjection<T>` (store price override, S11), site sessions and verifier bundle, `ApprovalTokenKind.Site`, logging/audit
  relays (with those designs), generic Windows service host (no app code).
- **Site time (⚑T12, time README §8, 05 §4.1):** broker answers `TimeReply` in `Sites.Queries.Health` via `TrustedTimeProvider.CreateReply`;
  terminal `UseSiteAnchor()` (authenticated `ITimeSource`, inherits broker uncertainty/flags, stratum rule); `InstallationTimeReport` in
  every probe; broker site time table + findings `time.device-skew` / `time.rollback` / `time.disagreement` + resync requests; terminal
  beacons in Standalone mode carry `TimeReply` fields; peer comparison as evidence only. Optional SNTP server on the broker = ask first.
- **Acceptance:** S1, S2 (site column), S8, S9, S10, S11 pass. Site time: internet down → terminals anchored to the broker with the
  broker's uncertainty added; a terminal whose Windows clock is set back shows up in the site time table within one probe; an
  unanchored broker cannot make a terminal anchored; two agreeing peers flag a lone terminal's rollback but never move its clock.
  Also: TLS negative tests (10 §3) pass. 30 simulated terminals on one site
  broker. An architecture test proves the broker host references no app assembly.

### Phase 5 — Hardening and UI

- Offline pairing (SAS + provisional certificates, S7), hardware-key policy, chaos and property suites, operations checklist in the
  installer, status bar + review-queue UI with the Avalonia phase.
- **Acceptance:** S7 passes. Every scenario test is green. AOT smoke at 0 warnings.

## 4. Docs to update when the phases land

`YFex/CLAUDE.md` (solution map, F12), `YFex/src/Messaging/docs/03-rpc-and-offline.md` (modes, envelopes, `.Offline`),
`authorization-consolidation/05` and `06` (signed snapshot, site approvals, as-of checks), `audit/05 §6` and `logging/05 §6` (relay = site
server), `privacy/05 §5` (installation keys, `CngRootKeyProvider`), `validation/13` (D34 refinement), YFex.Devices remoting (shared installation
identity), `docs/index.md` row status.

## 5. Implementation log

### Phase 0 — DONE 2026-10-03 (user confirmed ⚑O9, ⚑O16, ⚑O22)

Coordinated live with the authorization-consolidation session (its Phases 2a/2b edited the same Messaging.Rpc/Cqrs files the
same morning; it re-read every file listed below before its own edits). Backups: `C:\Temp\offline-backup\phase0\`.

**Tasks**

| Task | Done | Where |
|---|---|---|
| 0.1 F1 storage key | Random 32-byte key generated once, stored only wrapped: `WrappedKeyProvider` (file `<KeyDirectory>\<KeyName>.storage-key`, temp-file + move so concurrent first starts agree, never regenerates silently → `StorageKeyUnavailableException`), `IKeyWrapper` + `KeyProtection {Hardware, Software, DataProtection}`, `CngKeyWrapper` (RSA-OAEP-SHA256 under a persisted non-exportable CNG key, TPM first with a verified round trip, software KSP fallback, user or machine scope), `DataProtectionKeyWrapper` (cross-platform, Protect/Unprotect of the key — not a KDF), `StorageEncryptionOptions` (`StoreName`, `KeyName` = entry assembly, `KeyDirectory`, `MachineScope`, `PreferHardwareKey`). `DataProtectionKeyProvider` (internal) deleted | `YFex/src/Persistence/YFex.Persistence.Encryption/` |
| 0.2 F2 associated data | `IValueProtector.Protect(plaintext, associatedData)` / `TryUnprotect(…) → UnprotectStatus {Opened, OpenedLegacyLayout, AuthenticationFailed, UnsupportedVersion, Malformed}` (old one-argument calls kept). Layout `0x02`; `0x01` still read (`OpenedLegacyLayout`) and re-sealed on the next write; a `0x02` blob relabelled `0x01` fails. `EncryptedKeyValueStore(inner, protector, storeName, logger)`: AAD `"yfex.store|" + store + "|" + key`, `ReadFailed` event, `UnreadableValues`/`LegacyValuesRead` counters, first failure of each kind logged once as `StorageKeyMismatch` (keys never logged) | same project |
| 0.3 F3 idempotency | Client: `yfex-idempotency` header on every command dispatch (`Guid.CreateVersion7()`), replay re-sends the entry's key (`OutboxReplayContext`). Server: `IIdempotencyStore` + `InMemoryIdempotencyStore` (24 h) + `IdempotentExecution`, run by `ValidatingHandlerInvoker` **before** validation/rules/authorization; key = business key (`.IdempotencyKey(…)`) or header, bound to message type + scope header; only successful executions are remembered; concurrent duplicates await the first | `YFex.Messaging.Rpc/Idempotency.cs`, `ValidatingHandlerInvoker.cs`, `FusionMessageBus.cs`, `RpcMessagingExtensions.cs` |
| 0.4 F4 (interim) | `OutboxEntry(…, Subject, Scope, Sequence)` captured at enqueue by `IOutboxCallerCapture` (`DefaultOutboxCallerCapture`: `IPrincipalAccessor` + `IScopeContext`) inside the outbox implementations (so `LocalDispatcher`'s enqueue is covered too); replay sends the **captured** scope; a different signed-in subject → warning + `SyncStatus.ReplaysUnderAnotherSubject` | `IOutbox.cs`, `InMemoryOutbox.cs`, `StorageBackedOutbox.cs`, `OutboxReplayer.cs`, `SyncStatus.cs` |
| 0.5 F5 | Persisted monotonic sequence (`outbox-sequence` key, outside the `outbox:` prefix); `OutboxEntry.CompareReplayOrder` (sequence, then enqueue time for legacy 0); `OutboxOptions.KeepExpiredEntries = true` (default): expired entries are kept and replayed with a one-time warning | same files |
| 0.6 F12 | Duplicate `EncryptionMode`/`EncryptionKeySource` enums and the never-read `Encryption`/`EncryptionKeySource` client options removed (F15); `StorageDirectory` `[Obsolete]`; `CLAUDE.md` map (Messaging.Rpc line, Persistence tree, Layer 5) corrected | `YFexMessagingRpcClientOptions.cs`, `CLAUDE.md` |
| **F14** (found while implementing) | The client never used the durable outbox: `OutboxPair` now picks `StorageBackedOutbox` + `StorageBackedSyncFailureLog` when `Storage != InMemory` and the registered store is not `MemoryKeyValueStore` | `OutboxPair.cs`, `RpcMessagingExtensions.cs` |

**Files touched** (for sessions re-reading before their edits): Persistence.Encryption — `IValueProtector.cs`, `AesGcmValueProtector.cs`,
`EncryptedKeyValueStore.cs`, `EncryptionServiceExtensions.cs`, `.csproj` (description), new `IKeyWrapper.cs`, `CngKeyWrapper.cs`,
`DataProtectionKeyWrapper.cs`, `WrappedKeyProvider.cs`, deleted `DataProtectionKeyProvider.cs`; new project
`YFex.Persistence.Encryption.Tests` (+ `yfex.slnx`). Messaging.Rpc — `IOutbox.cs`, `InMemoryOutbox.cs`, `StorageBackedOutbox.cs`,
`OutboxReplayer.cs`, `SyncStatus.cs`, `FusionMessageBus.cs` (HeadersAsync + 2 command call sites), `ValidatingHandlerInvoker.cs`
(public InvokeAsync → idempotency → renamed `InvokeValidated*`), `RpcMessagingExtensions.cs` (client outbox block, server idempotency
registration), `YFexMessagingRpcClientOptions.cs`, new `Idempotency.cs`, `OutboxPair.cs`. Tests — Messaging.Tests
`Rpc/OutboxTtlTests.cs` (opts out explicitly + new kept-by-default test), new `Rpc/OutboxPhase0Tests.cs`; Messaging.Rpc.Tests new
`IdempotencyTests.cs`. Docs — `src/Messaging/docs/03-rpc-and-offline.md`, `04-storage-and-backends.md` (rewritten: it described
extensions that do not exist), `docs/public/messaging.md` (client example + encryption section), `YFex/CLAUDE.md`.

**Deviations from §3 (with reason)**

1. 0.1 names: `CngKeyWrapper` + `WrappedKeyProvider` + `DataProtectionKeyWrapper` instead of one `CngStorageKeyProvider` — wrapping
   (platform) and file handling (portable) are separate concerns, and the same `WrappedKeyProvider` serves both platforms.
   `DataProtectionKeyProvider` was internal, so it was deleted rather than obsoleted.
2. 0.1 two-process test: done as a real two-process run with a file-based app (`C:\Temp\yfex-edits\twoprocess\twoprocess.cs`: process
   13692 wrote with a TPM-wrapped key, process 35956 read `Opened`), plus in-process restart tests (independent providers, two DI
   containers). An xunit test spawning a child process was not added (slow, fragile on CI).
3. 0.3: idempotency is a step inside `ValidatingHandlerInvoker`, not a separate outer decorator: `LocalDispatcher` unwraps
   `ValidatingHandlerInvoker` by type and the Wolverine adapter re-runs `Decorate` — a separate decorator would have needed edits to
   both (owned by the authorization session for Phase 2b) or run validation twice in-process. Semantics are the planned ones
   (duplicates answered before every stage).
4. 0.4: capture lives in the outbox (`IOutboxCallerCapture`) instead of the dispatchers, so `LocalDispatcher` needed no change.
5. 0.6: F15 (dead options) and F14 (durable outbox never wired) were not in the plan; both are live data-loss/false-security bugs
   in files Phase 0 already touched.

**AOT:** Messaging.Rpc (`IsAotCompatible`) builds with no IL/CA1416 warning in any touched file; Persistence.Encryption has no
warning (CNG calls are guarded by `[SupportedOSPlatform("windows")]` / `OperatingSystem.IsWindows()`). No AOT publish was run.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Persistence.Encryption.Tests (new) | — | **15** |
| YFex.Messaging.Tests | 148 + 9 failing (authorization 2a in progress) + 2 skipped | **164** + 2 skipped (+5 outbox, +1 TTL) |
| YFex.Messaging.Rpc.Tests | (authorization added tests the same day) | **55** (+6 idempotency over real RPC) |
| YFex.Rules.Admin.Tests | 22 | 22 |
| YFex.Persistence.Tenancy.Tests | 9 | 9 |
| YFex.Persistence.Smoke | 146/149 | 147/149 (the 2 remaining failures are pre-existing) |
| YFex.Persistence.Tests | does not compile (pre-existing: references `YFex.State`, `UndoContext`, `MemorySnapshotStore`) | unchanged |

**Open items**

- **Identity at replay** is still the connection's (F4 full fix = signed envelope, Phase 3). Retrying a dead letter
  (`ISyncFailureLog.RetryAsync`) loses `Subject`/`Scope` because `SyncFailure` has no such fields.
- **Shared `IIdempotencyStore`** (SQL over YFex.Data, ≥ 30 days) for farms and server restarts — needed before production use of
  replays across server restarts.
- **Decorator order with tenancy:** register `AddYFexStorageEncryption` **before** the `ScopedKeyValueStore` decorator so the
  encryption sees the scope-prefixed key in its associated data (`Scoped(Encrypted(store))`); the reverse order leaves tenants
  separable only by prefix. Document/enforce in `YFex.Persistence.Tenancy` (not changed here).
- `YFex.Persistence.Tests` does not compile (pre-existing); `docs/public/messaging.md` still lists the old
  `YFex.Messaging.Rpc.Sqlite/IndexedDb/Encryption` names in its header line and later sections.
- The parallel "shared cryptography library" evaluation (`docs/internal/cryptography`, separate session) may relocate
  `IKeyWrapper`/`CngKeyWrapper`/`WrappedKeyProvider`; the on-disk formats (`.storage-key` `0x01`, value layout `0x02`) are the
  compatibility contract to keep. → **Done 2026-10-04**, see the addendum below.

### Addendum 2026-10-04 — encryption at rest moved onto YFex.Cryptography (cryptography Phase 0)

The user approved the shared cryptography library (docs/internal/cryptography, ⚑K1–K18) and asked to update this design accordingly.
What changed in the Phase 0 code (log: cryptography 12 §5):

| Offline Phase 0 (2026-10-03) | Now |
|---|---|
| `IKeyWrapper`, `CngKeyWrapper`, `DataProtectionKeyWrapper` | `IRootKeyProvider` (YFex.Cryptography), `CngRootKeyProvider` (YFex.Cryptography.Windows, byte-compatible blobs); DataProtection dropped |
| `WrappedKeyProvider` + `{KeyName}.storage-key` | key ring `device/storage` in `{KeyDirectory}\{KeyName}\` (`store.key` + `.keyring`, HMAC-tagged); the old file is imported once as the ring's first key |
| `IValueProtector`, `AesGcmValueProtector` (layout `0x02`, `'|'` AAD, one shared `AesGcm`) | `SealedValue` v3 (`0x03`, key id + algorithm in the header, length-prefixed context, one pooled `AesGcm` per key: cryptography C1) |
| `UnprotectStatus`, unreadable value → `null` + event | `OpenResult` union; `EncryptedKeyValueStore.TryGetAsync` → `StoredValueStatus`; `GetAsync` throws `StorageIntegrityException` (cryptography C10, ⚑K15) |
| `AddYFexStorageEncryption(EncryptionKeySource, byte[]?, configure)` | `AddYFexStorageEncryption(storage => …)` (`StoreName`, `KeyRing`, `KeyName`, `KeyDirectory`, `MachineScope`, `PreferHardwareKey`, `RotateEvery`, `ImportLegacyStorageKey`, `UseKey(bytes)`); the old overload is `[Obsolete]` for one release |
| `ActivatorUtilities` to build the inner store | keyed re-registration `"yfex.storage.inner"` (no reflection helpers; cryptography C8); package marked `IsAotCompatible` |

Kept: the default key directory `%LOCALAPPDATA%\YFex\Keys` (`%PROGRAMDATA%` with `MachineScope`), the decorator-order rule above, the
`StorageKeyMismatch` log (once per kind), `ReadFailed`, `UnreadableValues`, `LegacyValuesRead`. Tests: Persistence.Encryption.Tests 15
(golden `0x02` values produced by the interim code before the move; `0x01`; import of a real interim `.storage-key`; restart; erase;
reload after another installation rotates), Messaging.Tests 167 (+2 skipped), YFex.Persistence.Smoke encryption scenario all green.
