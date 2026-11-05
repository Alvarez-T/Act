# 01 — Current state, inventory and defects

Verified on 2026-10-02 with the RoselYn MCP tools and targeted searches. Paths are relative to `C:\Dev\YFex`. Nothing here was
changed.

## 1. What already exists (and is reused)

| Area | What exists | Where | Offline role today |
|---|---|---|---|
| Offline command queue | `IOutbox`, `OutboxEntry(IdempotencyKey, CommandTypeName, Payload, EnqueuedAt, AttemptCount, LastFailure, LastAttemptAt)`, `InMemoryOutbox`, `StorageBackedOutbox`, `OutboxReplayer` (drains on reconnect, backoff, conflict policy), `ISyncFailureLog` (dead letters) | `YFex.Messaging/src/YFex.Messaging.Rpc/IOutbox.cs`, `OutboxReplayer.cs`, `StorageBackedOutbox.cs`, `ISyncFailureLog.cs` | `IQueueable` commands are queued when the connection is down and replayed later |
| Local effect while queued | `IOfflineHandler<TCommand[, TResult]>`, `.OnOffline<THandler>()`, `.Optimistic<TQuery, TResult>(…)` | `YFex.Cqrs/src/YFex.Cqrs/IOfflineHandler.cs`, `Configuration/CommandConfigurationBuilder.cs:236-273` | the UI sees the effect before the server accepts it |
| Replay conflicts | `ConflictPolicy {Escalate, RetryLater, Discard}`, `IConflictResolver<T>`, `OutboxConflictResolution` | `YFex.Cqrs`, `YFex.Messaging.Rpc` | what to do when the server rejects a replayed command |
| Offline reads | `ICacheable` queries, `IClientCache`/`ICache` (`TryGetAsync` returns hit or stale), `MarkStaleAsync`, tags | `YFex.Persistence/src/YFex.Persistence/ICache.cs`, `Messaging.Rpc/QueryCacheKey.cs` | serves the last value when offline |
| Connectivity | `INetworkStatus {IsConnected, State}`, `SyncState {Connected, Disconnected, Reconnecting, Syncing}`, `FusionNetworkStatus`, bindable `SyncStatus` | `Messaging.Rpc/INetworkStatus.cs:3` | one boolean world: connected to *the* server or not |
| Caller context | `CallContext` (validation D29): identity bound to the WebSocket connection (default), token header or Fusion session; scope header; `CallContextMiddleware` stage 2; `ICallPermissions` + `PermissionsVersions` | `Messaging.Rpc/CallContext/` | who calls, in which tenant scope |
| Storage | `IKeyValueStore`, `ISyncLocalStore`, `IDurableQueue<T>`; backends Memory/FileSystem/Sqlite/IndexedDb/Data | `YFex.Persistence/*` | durable outbox, cache and journals |
| Encryption at rest | `EncryptedKeyValueStore` (AES-256-GCM, layout `0x01`), `IKeyProvider` (`DataProtectionKeyProvider` = "OS key store", `ProvidedKeyProvider`) | `YFex.Persistence/src/YFex.Persistence.Encryption/` | confidentiality of the local stores |
| Scope partitioning | `ScopeIdentity` (canonical text such as `0=acme/1=loja7`), `IScopePartitioner`, `ScopedKeyValueStore` | `YFex.Tenancy/src/YFex.Tenancy`, `YFex.Persistence/src/YFex.Persistence.Tenancy` | keeps tenants and stores apart on a shared device |
| Rule data on devices | `RuleSetSync` (incremental pull + `RulesetPublished` push), `IRuleDataCache`, `KeyValueRuleDataCache(keepVersions = 16)` | `YFex.Messaging/src/YFex.Rules.Admin/RuleAdminSetup.cs:87-140` | price/discount tables survive an offline restart |
| Approvals | `OverrideToken` (`Kind {Server, Device}`), `HmacOverrideTokens` (online), `DeviceOverrideTokenIssuer(deviceId, ECDsa privateKey, lifetime)`, `DeviceOverrideTokenVerifier(IDeviceKeyStore, OfflineApprovalCap[])`, `ApprovalVerification.Flagged` (validation D34) | `YFex.Rules/src/YFex.Rules/Approvals/OverrideTokens.cs` | supervisor override while offline, capped and always flagged |
| Permission snapshot (designed) | `PermissionSnapshot`, `UseServerPermissions()`, persisted copy valid 72 h (⚑Z9) | `docs/internal/authorization-consolidation/05` | offline UX + local-only operations |
| Audit on devices (designed) | durable `AuditJournal`, node chain + device ECDsa signature, `AuditShipper`, LAN relay "later option", device identity "to be moved to a shared place" | `docs/internal/audit/05 §3, §6`, `06 §3, §10` | lossless evidence produced offline |
| Logging on devices (designed) | spool + shipper, **relay mode** (`AddYFexLoggingRelay()`), `NodeIdentity`, clock-offset estimation, replay links | `docs/internal/logging/05 §3–§6` | diagnostics produced offline |
| Personal data offline (designed) | device seals new personal data for the server (ECIES with the tenant intake public key); device key pair; `IRootKeyProvider` (DPAPI on Windows) | `docs/internal/privacy/05 §5` | a POS can register a customer offline without holding subject keys |
| Jobs | `HubUnreachableBehavior {FailOpen, FailClosed}` per job | `YFex.Jobs/src/YFex.Jobs.Abstractions/Primitives/Enums.cs:74` | what a job does when its hub is unreachable |
| LAN tooling (planned) | YFex.Devices "LAN discovery" and "Remoting" (TLS, PSK, allow-lists in its phase 10) | `YFex.Devices/README.md`, `docs/04-serial.md:102` | sharing peripherals over IP |
| Fusion hooks | per-peer `RpcWebSocketClientOptions.ConnectionUriResolver` and `WebSocketOwnerFactory` (client certificates), `RpcOutboundCallOptions.RouterFactory` (route a call to a peer) | `C:\Dev\OpenSource\Fusion\src\ActualLab.Rpc\Clients\RpcWebSocketClientOptions.cs:22-25`, `Configuration\Options\RpcOutboundCallOptions.cs:20` | enough to route calls to "central" or "site broker" peers without forking Fusion |

**Not present anywhere:** offline login, a second endpoint or failover list, any concept of a local (site) server, installation enrollment or
certificates, signed data for devices, a trusted clock, server-side idempotency, a review queue for offline decisions.

## 2. Defects found (to fix whatever the offline-design decisions are)

| # | Defect | Evidence | Consequence (retail example) |
|---|---|---|---|
| **F1** | The "OS key store" storage key is **not stable across restarts**. `DataProtectionKeyProvider.GetKey()` derives the AES key with HKDF over `protector.Protect(fixed seed)`. ASP.NET Core DataProtection output is randomized per call (fresh key modifier and IV), so every process start gets a different key. | `Persistence.Encryption/DataProtectionKeyProvider.cs:26-33` | The POS restarts during an outage → `EncryptedKeyValueStore.GetAsync` gets `null` from `Unprotect` (`EncryptedKeyValueStore.cs:26`) → **queued sales, cached prices and snapshots silently disappear.** No test covers a second process (`Messaging.Tests/Encryption/EncryptionTests.cs` only uses `ProvidedKeyProvider`). The doc comment also claims Keychain/libsecret, which DataProtection does not use by default. To confirm with a two-process test in Phase 0. |
| **F2** | AES-GCM without associated data: a ciphertext is not bound to its key name or tenant. | `AesGcmValueProtector.cs:56,75`, `EncryptedKeyValueStore.cs:29-30` | Copying yesterday's encrypted permission snapshot or an older rule-data entry over today's key still decrypts (rollback by copy). Privacy 05 §6.3 already plans envelope v2 with AAD. |
| **F3** | The outbox idempotency key **never reaches the server**, and nothing enforces it there. Replay headers carry only the token and the scope. `CommandPolicy.IdempotencyKey` is compiled but has no reader. | `FusionMessageBus.cs:174-186`, `CompiledMessagingRegistry.cs:294,551` | The server executes a replayed `FinalizarVenda`, the reply is lost on a flaky link, and the replayer sends it again → **duplicate sale and duplicate stock decrement.** Audit 05 §7 assumes this protection exists. |
| **F4** | Replay runs as **whoever is connected at replay time, in the scope of replay time**. `OutboxEntry` stores neither actor nor scope. Identity is bound to the connection (D29), and the scope header comes from the current `IScopeContext`. | `IOutbox.cs:9`, `FusionMessageBus.cs:174-186` | Cashier Ana sells offline at 10:00. The connection returns at 14:00, after the shift change, while Bruno is logged in → **Ana's sales are executed as Bruno's**, permission checks use Bruno's rights, and audit blames Bruno. A manager laptop that switched to store 8 replays store 7's commands **into store 8**. |
| **F5** | Outbox order and TTL use the device wall clock (millisecond `EnqueuedAt`, ties not broken). Expired entries go to the failure log after 7 days. | `StorageBackedOutbox.cs:99`, `OutboxReplayer.cs:133`, `YFexMessagingRpcClientOptions.cs:24` | An NTP correction or a clock set back reorders "open cash drawer → sale → close drawer". A sale (a fact that already happened) can "expire" into a dead-letter log. |
| **F6** | The rule-data device cache is **unsigned** and keeps 16 versions. Integrity relies on optional storage encryption (which an admin on the device can bypass). | `RuleAdminSetup.cs:87-140` | Someone with file access deletes the newest version entries, so the POS falls back to an older table with a higher discount cap, or edits an unencrypted table directly. Nothing detects it. |
| **F7** | Device approval tokens: (a) `MaxAge` is compared with the **replay** time because `RulesStage` passes server `now`; (b) the approver's permission is re-checked **now**, not at the time of the approval; (c) `IssuedAt` is the device clock; (d) where the private key lives is unspecified (`ECDsa` passed in); (e) `IDeviceKeyStore` lives in Rules. | `RulesStage.cs:148-160`, `OverrideTokens.cs:158-206` | A supervisor approves a discount on Friday during an outage that lasts until Monday. With `MaxAge = 24 h`, the replay on Monday **rejects** an approval that was legitimate. A supervisor promoted away on Saturday also invalidates Friday's approval. Meanwhile, setting the device clock back makes an old token look fresh. |
| **F8** | **No offline login.** Identity comes from the identity provider through the WebSocket upgrade or a token. | search for `Pbkdf2`, `Rfc2898`, `OfflineLogin`: none | With no internet at opening time, nobody can log in, so the D34/⚑Z9 offline features are unreachable in practice. |
| **F9** | One endpoint (`WebSocketEndpoint`) and binary connectivity. There is no "connected to the local server but not to the cloud" state. | `RpcMessagingExtensions.cs:40`, `INetworkStatus.cs:3` | No way to express the user's case 2 (shop LAN with a local server). |
| **F10** | The designed `PermissionSnapshot` is persisted unsigned and its `ValidUntil` is checked against the device clock. | authorization 05 §2–§3 | Edit the file or set the clock back → expired or forged permissions drive offline decisions. |
| **F11** | Three LAN-relay proposals (logging relay, audit relay, Devices remoting TLS/PSK) and three device identities (logging `NodeIdentity.DeviceId` = Profiling fingerprint hash, audit device key, privacy device key). | logging 05 §3/§6, audit 06 §3, privacy 05 §5.1, `YFex.Devices/docs/04-serial.md:102` | Three pairings, three trust stores and three keys per terminal, each enrolled separately. |
| **F12** | `CLAUDE.md` still lists `YFex.Messaging.Rpc.Sqlite/IndexedDb/Encryption`, which now live in `YFex.Persistence/*`. `EncryptionKeySource` is declared twice (Messaging.Rpc and Persistence.Encryption). | `YFexMessagingRpcClientOptions.cs:5`, `EncryptionServiceExtensions.cs:8-14` | Agents look for projects that don't exist. Two enums can drift. |
| **F13** | Approval verification happens before the replay knows it is a replay. There is no way to say "this command records something that already happened" versus "this command asks for something". | Cqrs/Rpc pipeline | Rejecting a replayed **sale** cannot undo the sale. The customer left with the medicine. Facts need findings, not rejections ([08](08-sync-reconciliation-conflicts.md)). |
| **F14** | The RPC client **never used the durable outbox**: `AddYFexMessagingRpcClient` always registered `InMemoryOutbox`/`InMemorySyncFailureLog`; `StorageBackedOutbox` was constructed nowhere, even with SQLite registered (found in Phase 0) | `RpcMessagingExtensions.cs:59-78` (before Phase 0) | Every queued offline sale was lost on restart, encryption or not. **Fixed in Phase 0.** |
| **F15** | `YFexMessagingRpcClientOptions.Encryption`/`EncryptionKeySource`/`StorageDirectory` were **never read** (found in Phase 0) | `YFexMessagingRpcClientOptions.cs` (before Phase 0) | An app setting `Encryption = AesGcm` believed its outbox was encrypted; it was not. **Fixed in Phase 0** (removed; `StorageDirectory` obsolete). |

F1, F3 and F4 are live bugs in shipped code, independent of any offline-design decision. **Phase 0 (done 2026-10-03, 11 §5)
fixed F1, F2, F3, F5, F12, F14, F15 and the scope half of F4.** Cryptography Phase 0 (2026-10-04) then moved the
encryption-at-rest mechanics of that fix into the shared YFex.Cryptography library ([../cryptography/12](../cryptography/12-phases-and-acceptance.md) §5),
keeping its on-disk formats readable.

## 3. Vocabulary alignment (no code change)

- **YFex.Jobs `HubUnreachableBehavior.FailOpen/FailClosed`** is the job-level version of this design's "may this run without the central
  server?". This design does not change Jobs (frozen wire contract, same reasoning as ⚑Z16). Docs cross-reference each other.
- **Authorization `ActorContext` (⚑Z3)** is the single place that knows the real actor. Offline replay reuses it (an offline-recorded
  actor is still an actor). No fourth "who is calling" source is created.
- **Audit `AuditEntryFlags.Offline`**, **logging `LinkKind.Replay`** and **Rules `ApprovalVerification.Flagged`** are three ways of saying
  "decided offline". This design adds one shared value (`OfflineProvenance`, [05](05-protecting-offline-data.md) §6) that all three read.

## 4. Test baseline

Use the baselines recorded in the authorization consolidation memory (2026-10-02): Messaging 150 (+2 skipped), Messaging.Rpc 41, Rules 64,
Rules.Admin 22, Tenancy 27, Authorization 41. Persistence and Persistence.Tenancy counts are to be recorded at the start of Phase 0.
