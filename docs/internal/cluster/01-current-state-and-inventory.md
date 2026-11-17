# 01 — Current state and inventory (checked against source 2026-11-10)

Rule: current source wins over design docs. Everything below was read in the source on 2026-11-10. Line numbers drift; the
symbol names are the stable reference.

## 1. Summary

| Need | What exists | Works across processes? |
|---|---|---|
| Node identity | core `NodeIdentity` (`YFex/src/YFex/Diagnostics/NodeIdentity.cs`), used by Logging (`LoggingBuilder`, `LoggingEngine`, `.ylog` header) and `YFex.Audit/src/YFex.Audit/AuditTrail.cs` | describes a **producer of records**, stable per installation; no per-process incarnation, no link to `InstallationId` |
| Exclusive ownership (lease) | Jobs `IJobStore.TryAcquireLeaseAsync` + `ILease`; Audit's designed `pg_try_advisory_lock` | **no**: no store implements it across servers, and the Jobs engine never calls it |
| Run a due item once | Jobs `ClaimDueAsync`; Notifications `INotificationOutbox.ClaimDueAsync` | Jobs EF: **not atomic**; Notifications: in-memory only (N2 brings durable stores) and a claim never expires |
| Cross-node change signal | Authorization `IAuthorizationSignals` (in-process); FusionCache (no backplane configured); Messaging `IEventBus` (in-process) | **no** |
| Duplicate filter | Messaging.Rpc `IIdempotencyStore` + `InMemoryIdempotencyStore` | **no** (its own summary says farms need a shared store) |
| Migrations once | YFex.Data `SchemaInstaller` + `AcquireMigrationLock` (Postgres advisory lock, SQL Server `sp_getapplock`, SQLite per unit) | **yes** ✅ |
| Health / readiness / drain | nothing: no `IHealthCheck`, no `MapHealthChecks` anywhere in `C:\Dev\YFex`; Hosting designs `IHostModuleHealth` for its modules | **no** |
| Shared quota counter | Resilience: `RateLimiterPolicy` + `PerScope` (built, in-process); the quota of notifications ⚑N9 is agreed, not built | **no** |
| Trusted time | core `ITrustedClock` / `TimeProvider.GetTrustedNow()` / `TrustedInstant.IsUsableForSecurity` (`YFex/src/YFex/Time/ITrustedClock.cs`), engine YFex.Time | per node ✅ (Cluster uses it only for the clock check) |
| Metrics | Logging `MeterExport` (`YFex.Logging/src/YFex.Logging/Bridges/MeterExport.cs`): BCL `Meter`s `YFex.Logging.Operations` (count, duration, p99 per operation; **dimensions operation/kind/outcome only**) and `YFex.Logging` | per node ✅ (the platform aggregates) |
| Multi-machine test harness | Installations `InstallationHarness` (`YFex.Installations.Testing`), on Networking's `InMemoryNetwork`; Time's `FakeDeviceClock`, `FakeTimeSource` | the model to copy for `ClusterHarness` |
| Shop-site coordination | Installations roster (`Active`/`Standby` broker), Offline's signed `offline.feed.ranking` (only the broker ranks); **4f broker choice open** | yes, at the site (the substrate for the site engine, [09](09-site-engine.md)) |

## 2. Per library

### 2.1 Core `NodeIdentity`

```csharp
public sealed record NodeIdentity(string App, string Version, string Environment, Guid InstanceId, string Host, int ProcessId,
    DateTimeOffset StartedAt, NodeKind Kind, string? Region = null, string? DeviceId = null);
```

- `InstanceId` is "stable per installation (persisted), survives restarts". Two processes of one app on one machine (an IIS
  overlapped recycle, a service and a console run by an admin) share it. A cluster node needs **one id per process start** (⚑CL3):
  `(InstanceId, ProcessId, StartedAt)` identifies it today, but awkwardly; Cluster adds `ClusterNodeId` (02 §1).
- `NodeKind.Server` / `Worker` exist. Nothing links a node to an `InstallationId` (core `YFex/src/YFex/Installations/`): request
  ⚑CL4.

### 2.2 YFex.Audit — sequencer (designed, not built)

- Design [audit 06 §4](../audit/06-tamper-evidence.md) and ⚑A16: one sequencer per chain (top-level tenant, retention class, UTC
  day), exclusive through `pg_try_advisory_lock(chain key hash)` held on a session; "decided 2026-11-04: not a YFex.Jobs lease"
  because no Jobs store implements one across servers.
- Code: no sequencer yet (Audit Phase 3 "Store & server core" lists "sequencer with leases"). No `advisory` call in `YFex.Audit`.
- Already safe by construction: seals have **PK = chain key + index** and a unique entry id, so two sequencers cannot both write
  index 1 001. The lease adds placement, quick takeover and the admin view, not that correctness (03 §3, cons).

### 2.3 YFex.Jobs — claim, lease, dead letters

- `IJobStore` (`YFex.Jobs/src/YFex.Jobs.Abstractions/Store/IJobStore.cs`): `SupportsClustering`, `ClaimDueAsync(ClaimRequest)`,
  `TryAcquireLeaseAsync(LeaseRequest)` → `ILease { LeaseId, ExpiresAt }`, `ClaimedJob.LockToken`.
- `EfCoreJobStore.ClaimDueAsync` (`YFex.Jobs.Persistence.EntityFrameworkCore/EfCoreJobStore.cs` ~l.31): "For brevity in MVP, use a
  plain SELECT + UPDATE with row-version check" — but the entity has **no concurrency token** (no `IsConcurrencyToken`,
  `IsRowVersion` or `[ConcurrencyCheck]` in Jobs). **Two nodes can claim the same row** and both run the job. ⚠ finding F1.
- `EfCoreJobStore.TryAcquireLeaseAsync` returns a `TrivialLease` in every case ("Real impl would use a leases table…").
  `InMemoryJobStore` always grants; `KeyValueJobStore` locks within one process. **No engine code calls `TryAcquireLeaseAsync`**:
  the method has no consumer. Finding F2.
- `SchedulerHost` reads `SupportsClustering` (logging only).
- Dead letters: `JobRunStatus.DeadLettered`, `JobHistoryKind.JobDeadLettered`, `IJobObserver.OnDeadLettered` in `JobDispatcher`.
  They stay with Jobs (README §2).

### 2.4 YFex.Notifications — dispatcher (N0 built)

- `NotificationDispatcher : BackgroundService` (`YFex.Notifications/src/YFex.Notifications/NotificationDispatcher.cs`): claims due
  messages, sends, owns retries; "not a YFex.Jobs job".
- `INotificationOutbox.ClaimDueAsync(now, max)` marks entries `OutboxState.Sending` (`Outbox.cs`); "claims are the outbox's own (no
  Jobs lease)". The in-memory outbox is per process; durable outboxes come in phase N2.
- A `Sending` claim has **no claimer and no expiry**: if the node dies after claiming, the message stays `Sending` forever (no
  reclaim path found). Finding F3, reported to the owner in [10 §2](10-phases-and-acceptance.md).

### 2.5 YFex.Authorization — versions and signals

- `IPermissionVersions { Get, Bump, BumpAll }` and `IAuthorizationSignals { PermissionsChanged, AllPermissionsChanged, RevokedAsync,
  Changed }` in `YFex.Authorization/src/YFex.Authorization.Public/AuthorizationSignals.cs`. The remark on `IPermissionVersions`:
  *"Farms: this is the only state. Back it with a shared store, or feed `IAuthorizationSignals` from the message bus on every node
  (05 §4)."* Neither is built. Licensing LC-P2 calls `AllPermissionsChanged()` when a License changes: on a farm only the node that
  received the License re-masks.

### 2.6 YFex.Resilience — limits and quotas

- `RateLimiterPolicy(int Permits, TimeSpan Window, int QueueLimit)` and `PerScope` (`YFex.Resilience/src/YFex.Resilience/Policies.cs`):
  in-process.
- Quota (notifications ⚑N9, agreed 2026-11-09): "a counter per (quota key, Tenancy scope, period) … with a durable shared-store
  adapter so restarts and several central nodes count once". Not built. Resilience must not be referenced by Tenancy (cycle).

### 2.7 YFex.Persistence — FusionCache

- `ZiggyCreatures.FusionCache` 2.6.0 (`YFex.Persistence.csproj`), `FusionCacheAdapter : ICache` (L1 memory + optional L2). **No
  backplane** is configured: on a farm, node A's L1 keeps a stale value after node B changed it, until expiry.
  YFex.Web.Data also references FusionCache.

### 2.8 YFex.Messaging — outbox, dead letters, idempotency, Fusion

- `IOutbox` / `OutboxEntry` / `OutboxReplayer` (`YFex.Messaging.Rpc`): the **client-side** offline queue (a terminal replays to
  central). Dead letters → `ISyncFailureLog` / `SyncFailure` ("outbox", "saga-compensation"…). Stays with Messaging.
- Server side: `IIdempotencyStore { ClaimAsync, CompleteAsync, AbandonAsync }`, only `InMemoryIdempotencyStore` ("enough for one
  server process; farms and restarts need a shared store (SQL, later — offline design 08 §3 keeps keys ≥ 30 days)"). A replay that
  reaches another node runs the command again. Finding F4.
- `ServerPermissionsFeed : BackgroundService` pushes permission changes to connected clients **of this node**.
- `YFex.Messaging.Fusion` (`FusionLiveState` over ActualLab.Fusion `ComputedState<T>`): invalidation is in-process; a change on node
  B does not invalidate computed values on node A.
- `IEventBus` (in-process), `IRemoteEventBus` (RPC to clients). No server-to-server bus.

### 2.9 YFex.Data — dialects and migrations

- `ISchemaDialect` + `SchemaMigrationLockScope { Unit, Session }`; `SchemaSqlGenerator.AcquireMigrationLock`:
  Postgres `pg_advisory_lock(hashtext('yfex.schema:module'))`, SQL Server `sp_getapplock` (exclusive, session), SQLite/Oracle per
  unit of work (`SchemaInstaller` ~l.513/543). **Migrations already run once** when several nodes start together.
- No public primitive for a **transaction-scoped** named lock (`pg_advisory_xact_lock`, `sp_getapplock @LockOwner='Transaction'`):
  needed by Installations' durable registry (last-seat race) — request in 10 §2.
- No `ITransactionParticipant` exists yet (Audit's design names one). `YFexConnection` is the connection type.

### 2.10 YFex.Installations

- Core `InstallationRole { Central = 1, SiteBroker = 2, Terminal = 3 }`; evidence keys; `InstallationHarness` with `HarnessSite`,
  `HarnessCentral`, `HarnessDisk`, partitions through `InMemoryNetwork`.
- Registry: only `InMemoryInstallationRegistry`; **durable registry open** ([installations 08 §5](../installations/08-phases-and-acceptance.md)
  "2026-11-10"): the last-seat race needs `IEnrollmentPolicy` inside the registry transaction under a per-tenant lock.

### 2.11 YFex.Offline

- Site broker: generic service, no app code; roster `Active`/`Standby` broker; signed ranking `offline.feed.ranking` (only the broker
  ranks, ✅ 2026-10-30). **4f** (broker choice: ranking, pinning, hysteresis; site time table; YFex.Hosting) is open and waits for the
  user. ⚑O7 pairing policy open.

### 2.12 YFex.Hosting

- Plan only (⚑H1 one host per machine). `IHostModuleHealth`, module states, supervisor. H0 (host for the site broker) is needed by
  Offline 4f. Nothing on HTTP readiness or on multi-node drain.

### 2.13 YFex.Tenancy

- `ITenantStore<TTenant>` finds a tenant by identifier or id; **no enumeration**. Core `TenancyPath` (`0=farmaviva/1=loja-centro`)
  and Tenancy `ScopeIdentity` exist. Per-scope work keys therefore come from the owner of the work (⚑CL9).

### 2.14 YFex.Web

- `YFex.Web.AspNetCore` exists (tenant middleware, `ApplicationBuilderExtensions`). No health endpoints.

## 3. Findings to hand to owners (not fixed here)

| # | Where | Finding | Effect on a farm |
|---|---|---|---|
| F1 | Jobs `EfCoreJobStore.ClaimDueAsync` | SELECT then UPDATE without a concurrency token or `SKIP LOCKED` | the same job runs on two nodes |
| F2 | Jobs `IJobStore.TryAcquireLeaseAsync` | no cross-server implementation and no caller | a contract that suggests safety it does not give |
| F3 | Notifications `INotificationOutbox` | a `Sending` claim has no holder and no expiry | a crash leaves messages stuck; durable stores (N2) must add a claim expiry |
| F4 | Messaging.Rpc `InMemoryIdempotencyStore` | per process | a replay landing on another node executes twice |
| F5 | Authorization `IPermissionVersions` | in-process only | revocations and License changes reach one node |
| F6 | Persistence `FusionCacheAdapter` | no backplane | stale L1 entries on the other nodes |
| F7 | everywhere | no health/readiness/drain | rolling deploys cut requests and abandon work until expiry |
