# 10 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules (every phase)

- **No code before the user confirms ⚑CL1–⚑CL26** (README §6). Each phase starts by re-reading the current source of the libraries it
  touches (rule: current source wins).
- `C:\Dev\YFex` is **not a git repository** at the root; each `YFex.*` sibling is its own Git root. Commit on `main` of the repo you
  changed, only when the user asks. Before touching an existing file in another repository, copy it to
  `C:\Temp\cluster-backup\<phase>\` with the same relative path.
- Never edit another library's code to land an adoption: the requests in §2 are done **by their owners**, who decide the exact shape.
  Coordinate through the linked sessions and their implementation logs.
- RoslynMcp tools for C# exploration; `dry_run_compile` before non-trivial edits; the `yfex-csharp` skill.
- No new NuGet package without asking (none is planned: Npgsql comes through YFex.Data.Postgres).
- Build with `--artifacts-path C:/Temp/cluster-artifacts`; AOT smoke at 0 IL warnings for every new package; tests on Windows **and**
  Linux (`linuxKit`); the Postgres suite in Docker through `linuxKit`.
- Record test counts before/after in §5 and keep the memory file (`yfex-cluster.md`) current.

## 2. Requests to other libraries (what each should adopt later, and why; owners decide the shape)

### 2.1 YFex.Audit — sequencer onto the lease

| # | Request | Why | When |
|---|---|---|---|
| AU1 | Run each chain's sequencer as `WorkerPerScope<ChainSequencer, OpenChains>("audit.sequencer")` with `RequireGuarantee(Fenced)` instead of `pg_try_advisory_lock` (revisit the 2026-11-04 decision in audit 06 §4) | portable to SQL Server/SQLite; chains spread over nodes; handover on drain in ~1 s; admin view; no pooled-connection trap | Audit Phase 3, once CL-P2 exists |
| AU2 | `FenceAsync` once per seal batch inside the batch transaction; keep PK = chain + index (it stays the last line of defence) | a paused sequencer's batch rolls back instead of failing on the PK later | with AU1 |
| AU3 | `OpenChains : IClusterWorkScopes` from the inbox (chains with unsealed rows) and a notice `audit.inbox` on ingest | idle chains hold no lease | with AU1 |
| AU4 | Gauge `yfex.audit.unsealed` / `.oldest.age` reported by one node (07 §2) | scaling signal | CL-P4 |

### 2.2 YFex.Jobs — scheduler and claim

**Keep Jobs' own claim where the store guarantees it.** A run claim is "this row, this fire, once": an atomic claim in the store
(`UPDATE … WHERE state = Active AND next_fire <= @now … RETURNING` with `FOR UPDATE SKIP LOCKED` on Postgres/SQL Server, or a
concurrency token) is cheaper and finer than a lease per job key, scales with the number of rows, and needs no renewal. A lease is for
*long-held ownership*, which a run claim is not.

| # | Request | Why |
|---|---|---|
| JO1 | Make `EfCoreJobStore.ClaimDueAsync` atomic (finding F1: SELECT + UPDATE without a concurrency token) — `SKIP LOCKED` or a conditional `UPDATE … RETURNING`; record the claiming node and a claim expiry so a crashed node's claims are reclaimed (`AbandonGracePeriod` exists in `SchedulerHost`) | today two nodes can run the same job |
| JO2 | Replace `IJobStore.TryAcquireLeaseAsync` / `ILease` (no cross-server implementation, no caller — F2) with `IClusterLeases` through an optional bridge (`YFex.Jobs.Cluster`), or delete it | one lease concept in the ecosystem |
| JO3 | One-node Jobs maintenance (history pruning, missed-run detection, schedule sync from code if it is not idempotent) as `Worker`s through that bridge | avoid N nodes pruning the same history |
| JO4 | Optionally, a job option "never overlap across nodes" mapped to a lease per job key — the owner decides if it is needed beyond the atomic claim | long jobs whose next fire comes before the run ends |
| JO5 | Jobs backlog gauges (`yfex.jobs.due`, lateness) — 07 §2 | scaling signal |
| JO6 | A work class on jobs (`[WorkClass("reports")]` or a registration option) and per-class shares in the scheduler's claim loop (11 §3.1); backlog gauges by class | ⚑CL27, ⚑CL28 |

### 2.3 YFex.Notifications — dispatcher as a single worker per tenant

| # | Request | Why |
|---|---|---|
| NO1 | `NotificationDispatcher` runs as `WorkerPerScope<…, OutboxScopes>("notifications.dispatch")` on central farms (keep the plain `BackgroundService` path for one-node hosts, or always use the runner — the in-memory engine is free) | spread per tenant; one tenant's backlog does not block others |
| NO2 | Durable outboxes (N2) record **claimer + claim expiry** on `Sending` and reclaim expired claims (finding F3) | a crashed node leaves messages stuck today |
| NO3 | `OutboxScopes : IClusterWorkScopes` (tenants with pending messages) + notice `notifications.enqueued` | idle tenants hold no lease |
| NO4 | Quotas (⚑N9) through Resilience's quota on Cluster counters (block mode for rate-like budgets, exact for money-like ones) | several nodes count once |
| NO5 | Gauges `yfex.notifications.outbox.pending` / `.oldest.age` | scaling signal |

### 2.4 YFex.Authorization — farm backing

| # | Request | Why |
|---|---|---|
| AZ1 | A shared `IPermissionVersions` (version per subject + global version in the database; re-read at most every 30 s and on notice) | the remark on `IPermissionVersions` asks for it (F5) |
| AZ2 | Relay `IAuthorizationSignals` across nodes with a notice (`authorization.changed`; coalesce storms into one "everyone"), sketch in 03 §4; `RevokedAsync` must also close the subject's connections on other nodes | a revocation or License change reaches every node in ms, not 30 s |
| AZ3 | `cluster.view` / `cluster.operate` permissions use the normal catalogs (`ClusterPermissions` in YFex.Cluster) | admin view |

### 2.5 YFex.Resilience — shared quota counter

| # | Request | Why |
|---|---|---|
| RE1 | The ⚑N9 quota gets a durable adapter on Cluster counters (bridge package owned by Resilience, e.g. `YFex.Resilience.Cluster`): exact mode for budgets that cost money, block mode for high-rate budgets | "several central nodes count once"; no second counter table |
| RE2 | ~~A bulkhead with `PerScope`~~ **exists** (checked 2026-11-11: `BulkheadPolicy` + `ResourcePolicies.PerScope`, `src/YFex.Resilience/Policies.cs:58,75`; Polly slots per scope in `PollyResiliencePipelineFactory.cs:63`): use it for `MaxConcurrencyPerScope` (04 §1.2) | per-tenant fairness with one concept of limits |
| RE3 | Limits that can **change at runtime** (bulkhead size, rate permits): today `ResourcePolicies` is a frozen descriptor compiled once; a reload path keyed by a policy version (11 §4) | the vendor orchestrator tunes limits without restarts (⚑CL32) |
| RE4 | A priority-aware **admission gate** (load shedding): given the node's pressure level and a work priority, admit, delay or refuse with `Retry-After` (11 §3.2); no such type exists today | ⚑CL29 |

### 2.6 YFex.Licensing — the seams being built in LC-P3/P4 (decisions 2026-11-10)

| # | Request | Why |
|---|---|---|
| LI1 | The activation-delta outbox (central → vendor) has **one sender per tenant**: shape the sender as a loop that can run under `WorkerPerScope(…, Fenced)` later; until Cluster exists, a store-level claim with claimer + expiry | two central nodes must not send the same delta twice or out of order |
| LI2 | "License of tenant X changed" is a **signal with a durable version** (the stored License) so a cross-node notice (`licensing.changed`, transactional publish 05 §5.1) can be added without changing callers; each node reloads `ICentralLicenses.For(tenant)` and calls `AllPermissionsChanged()` locally | LC-P2 re-masks only the node that received the License |
| LI3 | Vendor check-in endpoint (LC-P5) uses the duplicate filter for request nonces when it runs on several nodes | replayed check-ins |
| LI4 | Licensing meters (LC-P6) use exact counters | sums must be exact |

### 2.7 YFex.Installations — durable registry and events

| # | Request | Why |
|---|---|---|
| IN1 | The durable `IInstallationRegistry` (open, installations 08 §5 2026-11-10) solves the **last-seat race inside its own transaction under a per-tenant lock** (YFex.Data transaction-scoped lock, §2.10 DA1) — **not** with a Cluster lease | the decision belongs in the same transaction as the enrollment rows; a lease outside the transaction cannot make it atomic |
| IN2 | Consumers of registry events (seat counts, activation deltas) run under leases (`WorkerPerScope` per tenant or one `Worker` with a durable cursor) | one consumer per cursor on a farm |
| IN3 | Optional: `NodeIdentity.Installation` filled on central nodes from `LocalInstallation`; later, node records signed with the evidence key (08 §3) | admin view, detection of unknown nodes |
| IN4 | Site engine pieces: a `cluster` peer channel; membership from `FriendSet` (§2.11) | last phases |

### 2.8 YFex.Messaging — replays, Fusion, sticky sessions

| # | Request | Why |
|---|---|---|
| ME1 | Shared `IIdempotencyStore` on Cluster's duplicate filter (finding F4); results encoded by Messaging (MemoryPack, versioned) | a retry that lands on another node must not run twice |
| ME2 | `messaging.fusion.invalidate` notices so computed values invalidate on every node | Fusion RPC refresh across nodes |
| ME3 | WebSocket drain participant: close with 1012, clients reconnect and re-subscribe; `ServerPermissionsFeed` keeps working per node with AZ2 | rolling deploys without errors on clients |
| ME4 | Document: no sticky sessions required; per-connection state must be rebuildable (06 §6) | balancer settings |
| ME5 | Client offline outbox and `ISyncFailureLog` stay as they are (dead letters stay with Messaging) | — |
| ME6 | Server queues per work class (more consumers for Critical classes) when a broker transport is used (Orchestration.Rpc `IMessageBroker`) | priority for message-driven work (11 §2) |

### 2.9 YFex.Persistence — FusionCache backplane

| # | Request | Why |
|---|---|---|
| PE1 | A FusionCache backplane over `IClusterNotices` in a bridge package (e.g. `YFex.Persistence.Cluster`) | stale L1 on other nodes (F6) without requiring Redis |

### 2.10 YFex.Data — dialect primitives, migrations once

| # | Request | Why |
|---|---|---|
| DA1 | A public **transaction-scoped named lock** per dialect: `pg_advisory_xact_lock(hash)`, `sp_getapplock @LockOwner = 'Transaction'`, SQLite `BEGIN IMMEDIATE` | Installations' last-seat transaction (IN1) and any owner needing a short exclusive section in one transaction |
| DA2 | Keep `AcquireMigrationLock` as the "migrations once" mechanism; expose "schema at version N" for a readiness check (06 §3.3) | rolling deploys |
| DA3 | A way to run a statement on the caller's open connection/transaction (`YFexConnection`) that Cluster.Data's `FenceAsync` and transactional notice publish can use | fencing and transactional notices |
| DA4 | Database clock function per dialect (`clock_timestamp()`, `SYSUTCDATETIME()`, SQLite UTC) in the dialect | lease expiry on the database clock |

### 2.11 YFex.Logging / core Diagnostics — metrics and node identity

| # | Request | Why |
|---|---|---|
| LO1 | `NodeIdentity` gains `InstallationId? Installation = null` (core); the `.ylog` header writes it under the format's version rule | ⚑CL4 |
| LO2 | Guidance: libraries publish their own BCL `Meter`s (as `MeterExport` does); per-tenant data never as a metric dimension | 07 §2–§3 |

### 2.12 YFex.Offline + YFex.Installations — the site engine (last phases)

| # | Request | Why |
|---|---|---|
| OF1 | `SiteClusterEngine : IClusterEngine` (`BestEffort`) with the Active broker as coordinator, its table in the durable broker store (4c), tokens with a coordinator epoch | same app contracts at the store (09) |
| OF2 | `WhenCoordinatorAway.Pause` / `RankedFallback` using the signed ranking (4e/4f) | broker off at night |
| OF3 | Overlap detection at central from reported tokens (review queue) | detection of split duplicates |
| OF4 | Depends on: Offline 4f (broker choice), Hosting H0, Installations I5 where relevant | ordering |

### 2.13 YFex.Hosting — host integration

| # | Request | Why |
|---|---|---|
| HO1 | The host runs `IClusterHealth.DrainAsync` on stop and shows node state, leases held and workers in module status, tray and CLI | one host per machine (⚑H1) |
| HO2 | Windows service: request additional stop time while draining; systemd: `Type=notify`, `TimeoutStopSec`; launchd: `ExitTimeOut` (08 §1) | drains that finish |
| HO3 | Join/leave per module generation: a generation change drains the modules being removed (their workers hand over) | profile changes without abandoned leases |
| HO4 | Node roles from the module profile (11 §3.3); the profile may also carry the vendor-signed work policy for on-premises centrals (⚑CL32 open point) | roles and runtime policy on premises |

### 2.14 YFex.Web — health endpoints and maintenance

| # | Request | Why |
|---|---|---|
| WE1 | `app.MapYFexHealth()` in `YFex.Web.AspNetCore` over `IClusterHealth` (startup, live, ready; 06 §1) | balancers and Kubernetes |
| WE2 | Drain participant for HTTP requests (stop accepting, wait in-flight) | 06 §2 step 3 |
| WE3 | Per-scope maintenance middleware: 503 + `Retry-After` for scopes in maintenance (06 §4) | tenant data repair |
| WE4 | Per-tenant request counting into the load history (07 §3) and per-tenant rate limits through Resilience at the entry | busy hours, noisy neighbour |
| WE5 | `WithWorkClass("…")` on endpoints; the admission gate (RE4) answers 503 + `Retry-After` for shed classes | load shedding for HTTP (⚑CL29) |

## 3. Dependencies

| Needs | From | Status | Phase here |
|---|---|---|---|
| ⚑CL1–⚑CL26 | user | **pending** | all |
| core `NodeIdentity`, `TenancyPath`, `ITrustedClock` | core | ✅ built | P1 |
| `NodeIdentity.Installation` (LO1) | core/Logging | not requested yet | P2 (optional field; P1 works without) |
| YFex.Data dialects, `SchemaInstaller`, `YFexConnection` | Data | ✅ built | P2 |
| DA3, DA4 | Data | not requested yet | P2 |
| DA1 (transaction lock) | Data | not requested yet | not needed by Cluster itself; needed by Installations IN1 now |
| Resilience bulkhead `PerScope` (RE2) | Resilience | ✅ built (checked 2026-11-11) | P4 |
| Runtime limits (RE3), admission gate (RE4) | Resilience | not requested yet | P6 |
| `FakeTimeProvider`, `FakeDeviceClock` | YFex.Time.Testing | ✅ built | P1 |
| Web.AspNetCore endpoints (WE1, WE2) | Web | not requested yet | P3 |
| YFex.Hosting H0+ (HO1–HO3) | Hosting | plan only | P3 (Hosting part waits for the repo) |
| Offline 4f, Hosting H0 | Offline, Hosting | open | P8 |

## 4. Phases ("start small", ⚑CL26)

### CL-P0 — Decisions and small requests

- The user answers ⚑CL1–⚑CL26. Ask Data for DA1 now (it unblocks Installations' durable registry independently of Cluster).

### CL-P1 — Repo, contracts, in-memory engine, runner, drain, harness

- Repo `C:\Dev\YFex\YFex.Cluster` (`cluster.slnx`): `YFex.Cluster`, `YFex.Cluster.Testing`, `tests/YFex.Cluster.Tests`,
  `tests/YFex.Cluster.AotSmoke`, `benchmarks/YFex.Cluster.Benchmarks`, `AGENTS.md`.
- `ClusterNodeId`, `LeaseKey`, `FencingToken`, `LeaseGuarantee`, `Lease`, `IClusterLeases`, `IClusterWorker` + runner (`Worker`,
  `WorkerPerScope`, `HandOverRequested`, back-off), `IClusterNotices` (in-process), `IClusterMembership`, `IClusterHealth` + drain
  coordinator + `IClusterDrainParticipant`, `IClusterEngine`, in-memory engine, Meter `YFex.Cluster`, `AddYFexCluster`.
- `ClusterHarness`: N nodes over a shared harness engine with Fenced semantics, fake time, `Partition`, `Pause`, `Skew`, `Drain`,
  `Join`, BestEffort mode.
- Work-class **metadata only** (⚑CL27): `WorkClassId`, `WorkPriority`, `cluster.WorkClass(...)`, `.Class(...)` on registrations, so
  adopters label their work from day one; no shares or roles yet.
- **Acceptance:** one node: `TryAcquireAsync` ≤ 100 ns and 0 B after warm-up, no background thread without workers; harness (3 nodes):
  exactly one holder per key at every step of 10 000 random partition/pause/join/drain steps (property test); a paused holder's fenced
  write after expiry is refused; drain hands every key over in < 2 s of fake time; a worker ignoring `HandOverRequested` is force-stopped
  at the budget and logged; `RequireGuarantee(Fenced)` fails registration on a BestEffort engine; AOT 0 warnings; Windows + Linux.

### CL-P2 — SQL engine on Postgres

- `YFex.Cluster.Data`: schema (`cluster.leases`, `cluster.fencing`, `cluster.nodes`, `cluster.notices`), lease operations, `FenceAsync`,
  heartbeat + view, `LISTEN/NOTIFY` listener with `Gap`, polling table fallback, transactional publish, sweeps.
- **Acceptance:** the P1 contract suite passes against Postgres in Docker (`linuxKit`); kill -9 of a holder → takeover ≤ 35 s; drain →
  ≤ 2 s; a fenced transaction blocks a concurrent takeover until it commits (test with two real connections); listener reconnect raises
  `Gap`; 50 nodes × 1 000 keys: renewal load measured and recorded; two connections per node at rest.

### CL-P3 — Host integration and first adopters

- Web.AspNetCore WE1/WE2 (by Web's owner), Messaging ME3 (WebSocket drain), Hosting HO1–HO3 when the repo exists; a spike per host of
  08 §1 (Kubernetes, Docker, systemd, Windows service incl. system shutdown, IIS overlapped recycle).
- First adopters through their owners: Licensing LI1/LI2 (activation sender, `licensing.changed`), Authorization AZ1/AZ2,
  Notifications NO1–NO3, Persistence PE1.
- **Acceptance:** a rolling restart of 3 pods under load (k6 or a test client) drops **zero** requests and no WebSocket client sees an
  error beyond a reconnect; a License change reaches all nodes' masks in < 1 s; Notifications on 3 nodes sends each message once.

### CL-P4 — Per-tenant spread, fairness, metrics, load history

- Rendezvous placement, preferred/second-ranked grace, handover after 60 s Ready and ≤ N per minute, pinning; `MaxConcurrencyPerScope`
  on the Resilience bulkhead (RE2); one-node gauges pattern; `cluster.load_hourly` + `IClusterLoad` + `ClusterLoadProfileQuery`.
- Work classes in behaviour (11 §3): weighted shares with minimum share in the runner (⚑CL28), node roles from configuration and
  placement filtered by role (⚑CL30), per-class metrics (⚑CL31); Jobs JO6.
- **Acceptance:** 300 scopes on 3 nodes → each node holds 100 ± 15; adding a 4th moves ~75 within 2 min with no key held twice and no
  key idle > 5 s; removing it moves them back; a tenant with 100 000 queued items does not delay another tenant's first item by more
  than one batch; history `Add` ≤ 20 ns, 0 B; per-tenant hourly rows correct after node restarts (additive upserts).
  Classes: with fiscal 60 / notifications 30 / reports 10 and all three saturated, slot use stays within ± 5 points of the weights
  over 10 000 passes and reports never drop below the 5 % minimum; a `fiscal`-only node never runs another class; an idle class's
  share goes to the others within one pass.

### CL-P5 — Shared counters and duplicate filter

- `cluster.counters` (exact, block), `cluster.claims`; owners' bridges: Resilience RE1, Messaging ME1, Licensing LI3/LI4.
- **Acceptance:** 3 nodes spending a budget of 1 000 with block size 50 → never above 1 000, at least 1 000 − 3 × 50 spent before
  refusal; a crashed node's block is lost, never double-spent; a command retried on another node within retention returns the stored
  result; an `InProgress` claim of a dead node is reclaimed after expiry.

### CL-P6 — Upgrades, maintenance, clock check, admin view

- Runtime work policy (⚑CL32): `cluster.work_policy`, `cluster.node_roles`, commands `SetWorkPolicy`/`SetNodeRoles`/`PreviewWorkPolicy`/
  `RevertWorkPolicy`, temporary overrides with expiry, notice `cluster.policy.changed`; pressure levels and load shedding (⚑CL29)
  with Resilience RE3/RE4 and Web WE5; signed policy delivery on premises (HO4 or Licensing, decided then).
- Feature tags + latch + refusal of old nodes; minimum version; schema-version readiness (DA2); node/scope/cluster maintenance +
  Web WE3; clock check; `ClusterPermissions`, admin queries as Capabilities; `cluster.operate` commands audited.
- **Acceptance:** a v1/v2 mixed cluster keeps writing v1 until the last v1 node leaves, then latches; a v1 node started after the latch
  refuses Ready; scope maintenance stops that tenant's workers within 2 s and Web answers 503 for it only; a node with +10 s skew does
  not take leases; the admin view hides other tenants' keys from non-operators.
  Policy: a `SetWorkPolicy` reaches every node in < 1 s (Postgres) and is applied at the next pass; an invalid policy (Critical shed,
  class with no role on any Ready node) is refused; a temporary override expires by itself; `RevertWorkPolicy` restores the old
  behaviour; every change is audited. Shedding: under synthetic pressure `High`, Low endpoints answer 503 + `Retry-After` and Low
  workers pause at a safe point while Critical throughput is unchanged; a role removed at runtime hands its scopes over without restart.

### CL-P7 — SQL Server and SQLite engines (on-premises)

- Dialect rendering of every statement, SQL Server polling notices, SQLite single-machine guard; two-node failover on Windows services
  (Hosting) with SQL Server.
- **Acceptance:** the contract suite passes on SQL Server (Docker) and SQLite; a second SQLite node on another host is refused with the
  documented message; Windows service stop drains; failover in a two-server setup ≤ 35 s after a power-off of one server.

### CL-P8 — Site engine (Offline/Installations; their phases)

- OF1–OF2, IN4; `cluster` peer channel; coordinator table in the broker store; ranking fallback.
- **Acceptance (theirs):** at Loja Centro (harness: broker + 2 terminals), one `DeliveryOrdersPoller` runs; broker off with `Pause` →
  none runs; with `RankedFallback` → the top-ranked terminal runs it; broker back → exactly one again within one renewal; a Fenced
  worker refuses to register on a terminal.

### CL-P9 — Site scenarios end to end

- `InstallationHarness` + `ClusterHarness` together: switch failure splitting the store, standby broker promotion (4f), revoked
  terminal, night shutdown of the broker, overlap detection at central (OF3).
- **Acceptance:** every scenario of 09 §4 as a test; overlaps during a split are reported to central with both tokens.

## 5. Implementation log

(empty — no phase started)
