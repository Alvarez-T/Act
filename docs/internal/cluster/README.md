# YFex.Cluster — several server processes behaving as one: leases, single workers, notices, health and drain — Design

> **Status:** DESIGN 2026-11-10, extended 2026-11-11 (⚑CL27–⚑CL33 work classes, priority, roles, runtime control: [11](11-work-classes-priority-and-control.md)).
> Docs only: no code, no repo. Decisions in §6: see the 2026-11-11 record there for what is approved.
> **Origin:** 2026-11-10, while planning YFex.Licensing LC-P3/P4 (central seats and the vendor service, built together, with a SQL
> adapter for multi-node centrals). We found that **no YFex library coordinates several server processes**: Audit plans a Postgres
> advisory lock for its sequencer, Jobs has a lease method that no store implements across servers, Authorization says "back it with a
> shared store" in a remark, Notifications ⚑N9 asks Resilience for a counter that "several central nodes count once". Each library
> solves its own piece, or does not. The user agreed to one infrastructure library, **YFex.Cluster**, for the vendor cloud, on-premises
> central and (in the last phases, through Offline/Installations) shop sites. The user wants **every point discussed recorded here**,
> **starting small**, with the **site scenarios in the final phases**.
> **Repo (proposed):** `C:\Dev\YFex\YFex.Cluster` (sibling, template YFex.Time / YFex.Networking; own `cluster.slnx`).
> **Audience:** the user first, then the sessions that implement the phases ([10](10-phases-and-acceptance.md)) and the owners of the
> libraries that adopt it ([10 §2](10-phases-and-acceptance.md)).
> **Normative vs sketch:** every name in this folder is a **proposal** until the user approves §6; names in code blocks marked
> `// (sketch)` are illustrations only. Names of existing YFex code were checked against source on 2026-11-10 ([01](01-current-state-and-inventory.md)).

## 1. What it is, in one paragraph

When two or more copies of the same server run behind a load balancer, the app must still behave as **one**: a tenant's outbox is
sent by one process, the Audit chain of a day has one sequencer, a permission change reaches every process, a process that is being
stopped finishes its requests and hands its work over, and a new version does not write a format that older processes still running
cannot read. **YFex.Cluster gives app and library code those guarantees** with a few small contracts — **lease with fencing token,
single worker per key or scope, cross-node notice, node list, health and drain** — and engines per place (in memory, SQL through
YFex.Data, and later the shop site through Offline/Installations).

## 2. What it is not (agreed with the user)

| Not | Who does it | Cluster's part |
|---|---|---|
| **Routing traffic** (load balancing, TLS termination, sticky cookies) | the platform: nginx, YARP, cloud balancers, Kubernetes services, IIS ARR | readiness that tells the balancer when to stop sending (06 §1); notes for balancer settings (06 §6) |
| **Scaling servers or storage** (adding pods, scale sets, bigger databases, replicas) | the platform: Kubernetes HPA/KEDA, cloud scale sets, managed database growth | nodes are safe to add/remove (join, leases, drain); health; the **signals** the platform scales on (queue lengths, request rate, latency) as YFex.Logging metrics; **per-tenant, per-hour load history** for scheduled scaling ([07](07-scaling-and-storage.md)) |
| **Storage growth** (old rows, big tables) | each owner's retention, archiving and partitioning (Audit seals, Logging storage, Jobs history) + YFex.Data | none ([07 §4](07-scaling-and-storage.md)) |
| **Dead letters** | their owners: Messaging (offline outbox → `ISyncFailureLog`), Jobs (`JobRunStatus.DeadLettered`, history, observer), Notifications (outbox states) | only that **one node** replays or handles a given item |
| **Retry policy** (delays, budgets, breakers) | YFex.Resilience | none |
| **Consensus among shop terminals** (Raft, Paxos) | nobody: not built (⚑CL23) | the site engine is a coordinator lease from the broker, low-stakes work only ([09](09-site-engine.md)) |
| **Money or fiscal decisions at the shop** | central, or Offline's allowance model | refuses: such work requires a **fenced** lease, which a site cannot grant (⚑CL24) |

## 3. Shape and packages

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Cluster` | contracts (`ClusterNodeId`, `LeaseKey`, `FencingToken`, `Lease`, `IClusterLeases`, `IClusterWorker`, `IClusterNotices`, `IClusterMembership`, `IClusterHealth`), the **engine seam** `IClusterEngine` (public: Offline implements the site engine), the **in-memory engine** (single node: leases granted at once), the worker runner, the drain coordinator, placement (rendezvous hashing), Meter `YFex.Cluster`, `AddYFexCluster` | YFex core, YFex.Resilience (per-tenant limits), `Microsoft.Extensions.Hosting.Abstractions` |
| `YFex.Cluster.Data` | the **SQL engine** over YFex.Data: lease table with fencing, node table with heartbeat, notices (Postgres `LISTEN/NOTIFY`, polling table elsewhere), later counters, duplicate filter, load history; schema through YFex.Data's `SchemaInstaller` | the above + YFex.Data (+ the dialect package the app picks) |
| `YFex.Cluster.Testing` | `ClusterHarness`: several nodes in one process, fake time, a fake database that can be cut from one node, paused nodes, skewed clocks | the above + YFex.Time.Testing |
| tests, AotSmoke, Benchmarks | `.Tests` (Windows and Linux; the Postgres suite in Docker through `linuxKit`), `.AotSmoke` (0 warnings), BenchmarkDotNet | |

**No `.Public` package** (⚑CL1): the main package is small (BCL + abstractions), so adopters and the site engine reference it
directly. **No OS package**: the per-OS parts (stop signals, service hosts) belong to YFex.Hosting and the ASP.NET Core host
([08 §1](08-platforms-security-aot.md)). **No third-party package**: Postgres `LISTEN/NOTIFY` comes through the Npgsql driver that
YFex.Data.Postgres already uses. **No core types** except an optional link from core `NodeIdentity` to an `InstallationId` (⚑CL4).

## 4. The three places (main design: [04](04-three-places.md))

| | **Vendor cloud** (primary user) | **On-premises central** | **Shop site** (last phases) |
|---|---|---|---|
| Processes | many, short-lived, behind a balancer, added/removed by the platform | usually **one**, sometimes two for failover | the broker + terminals of one store |
| Shared state | managed Postgres | Postgres, SQL Server or SQLite | **none**: no shared database; splits are normal |
| Engine | `YFex.Cluster.Data` (Postgres) | in-memory (one node, costs nothing); `.Data` per dialect for two | **site engine implemented by Offline/Installations**, broker as coordinator |
| Node identity | per process; **not** enrolled as installations | per process, optionally linked to the `Central` installation | the installation itself (evidence key, mTLS) |
| Guarantee | **fenced** | **fenced** | **best effort** (a split can run work twice): low-stakes work only |
| Extra | per-tenant spread of background work, per-tenant fairness (noisy neighbour) | failover in seconds, no extra infrastructure | same contracts as central, so app code does not change |

## 5. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Node** | one running server process that joined the cluster; a new process start is a new node | `central-7f9c` pod, started 10:02 |
| **Lease** | the right of one node to own a **key** for a short time, renewed while held | `audit.sequencer:0=farmaviva/2026-11-10/standard` |
| **Fencing token** | a number that grows every time a key changes holder; stores refuse writes carrying an older one | token 42 → node crashes → new holder has 43 |
| **Single worker** | a long-running loop that runs on exactly one node per key or per scope, taken over when its node goes | the delivery-orders poller of FarmaViva |
| **Notice** | a small best-effort message to every node ("permissions of everyone changed"); a hint, never data | `authorization.all-changed` |
| **Drain** | the steps a stopping node takes: not ready → finish requests → hand over leases and workers → close WebSockets → leave | rolling deploy, scale-in, Windows service stop |
| **Placement** | which node *should* own a per-scope key (rendezvous hashing over ready nodes); the lease decides who *does* | FarmaViva's outbox prefers node B |
| **Engine** | the implementation behind the contracts in one place | in-memory, SQL (`.Data`), site (Offline) |
| **Guarantee** | what an engine promises: **Fenced** (exclusive, checked by the store) or **BestEffort** (may overlap during a split) | central = Fenced, shop = BestEffort |

Pharmacy words (FarmaViva, Loja Centro, SNGPC, iFood) appear **only in examples**. The library knows nodes, keys, scopes
(`TenancyPath`), workers and notices.

## 6. Decisions to confirm (⚑CL)

Prefix **⚑CL** (checked 2026-11-10: unused; ⚑C, ⚑L, ⚑LC are taken or close).
**2026-11-11 user:** ✅ CL1, ✅ CL2 as recommended; ✅ CL7 with timings configurable (default and per key).
✅ CL3 **both**: `ClusterNodeId` per process start (correctness) + optional installation link (CL4) for identity. ✅ CL4. ✅ CL5.
✅ CL6 **opt-in**, plus guidance for AI agents and docs (rule/skill text: "exclusive database write under a lease → evaluate
`FenceAsync`; external side effect → idempotency key"), so it is not forgotten. ✅ CL8 **option C**: Cluster workers for always-on
loops; Jobs adopts Cluster leases for its scheduler/claims; a Jobs `[Singleton]` attribute on Cluster comes later. ✅ CL9. ✅ CL13.
✅ CL14 with heartbeat interval and gone-timeout configurable. CL10, CL11, CL12, CL15, CL16, CL17 under discussion; the rest
pending. CL27–CL33 added 2026-11-11 at the user's request (priority by work class, configurable at runtime by the vendor
orchestrator); pending. Multi-database/regions and backups are not Cluster's job: separate design plan started 2026-11-11 (one cluster per region).

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **CL1** | Where does it live, which packages? | Sibling repo **`C:\Dev\YFex\YFex.Cluster`**: `YFex.Cluster` (contracts + in-memory engine + runner), `YFex.Cluster.Data`, `YFex.Cluster.Testing`. No `.Public`, no OS packages, no third-party package | inside YFex.Jobs (Jobs would own coordination for Audit, Notifications, Licensing, which are not jobs); one package per dialect (`YFex.Cluster.Postgres` breaks the `YFex.<Engine>` naming rule) | §3 |
| **CL2** | Boundaries | Cluster does **not** route, scale, keep dead letters or own retry policy (§2). It makes nodes safe to add/remove, says when a node is ready, ensures one node handles an item, and publishes the signals | a "platform" library that also autoscales (duplicates Kubernetes/cloud; cannot scale databases anyway) | §2, 07 |
| **CL3** | What is a node? | **One process start** = `ClusterNodeId` (Guid v7, new at every start) + core `NodeIdentity` (app, version, host…). An IIS overlapped recycle or two services on one machine are two nodes | `NodeIdentity.InstanceId` (stable per installation: two processes on one machine would share it) | 02 §1 |
| **CL4** | Link to Installations? | Add **optional `InstallationId? Installation`** to core `NodeIdentity` (core change, Logging owns the record). On-premises central nodes fill it (their `Central` installation); **cloud processes are not enrolled** and leave it empty. Used for the admin view and audit, **not for trust** in v1 | every cloud process enrolls (thousands of short-lived installations in the registry); no link (an on-premises admin cannot tell which server a node is) | 02 §1, 08 §3 |
| **CL5** | How are leases stored? | A **lease table** in the shared database (`cluster.leases`: key, holder, token, expires-at on the **database clock**), renewed by the holder; the same table on Postgres, SQL Server and SQLite. Dialect locks (`pg_advisory_xact_lock`, `sp_getapplock`) only for **transaction-scoped** locks (the registry's last-seat transaction, migrations) | Postgres session advisory locks for everything (no fencing token, no admin view of holders, lock lost silently with a pooled connection; not portable) | 05 §1–§3 |
| **CL6** | Fencing | Every lease carries a **`FencingToken`** that grows on every change of holder. Writes that must be exclusive call `lease.FenceAsync(connection)` inside their own transaction (one `UPDATE … WHERE token = @t AND holder = @me` on the lease row): 0 rows → `LeaseLostException`, the transaction rolls back. External side effects (an SMS, a marketplace call) cannot be fenced: they rely on idempotency keys | no fencing (a paused node — GC, VM freeze — writes after its lease expired); tokens checked by every owner table (each owner adds a column and its own checks) | 05 §2 |
| **CL7** | Lease timing | Default **30 s** duration, renewed every **10 s**, holder treats it as lost **5 s** before expiry (local monotonic time). Crash takeover ≤ ~35 s; **drain hands over in ~1 s** (explicit release + notice). Configurable per key | short leases (5 s: database load and false takeovers on a slow database); long (5 min: work stops for minutes after a crash) | 05 §3 |
| **CL8** | Single worker API | `IClusterWorker.RunAsync(ClusterWorkContext, CancellationToken)`; registered in one line: `cluster.Worker<T>(key)` (one in the cluster) or `cluster.WorkerPerScope<T>(key, scopes)` (one per tenant). The token fires on stop, drain **or lease lost**. No attribute or generator in v1 | `[ClusterWorker]` + generator (one line of registration does not need one); wrapping `BackgroundService` (cannot stop the loop when the lease is lost) | 02 §4, 03 §2 |
| **CL9** | Where do per-scope keys come from? | From the **owner of the work** (`IClusterWorkScopes`: e.g. the Notifications outbox lists tenants with pending messages), refreshed every 30 s and on a notice. Tenancy has no tenant enumeration (`ITenantStore` looks up by id only) | a tenant catalog in Cluster (a second tenant list); one worker for all tenants (no spread, one slow tenant blocks the others) | 04 §1.1 |
| **CL10** | Per-tenant spread | **Rendezvous hashing** over ready nodes decides the *preferred* node per (work kind, scope); only the preferred node competes at once, others after a grace; on join, holders hand over at a safe point after the new node was ready 60 s, at most N handovers per minute. The lease stays the only truth | a leader that writes a placement table (one more election, more failure modes); every node competes for every key (the first node takes everything) | 04 §1.1 |
| **CL11** | Per-tenant fairness (noisy neighbour) | Per-tenant work runs through a **YFex.Resilience bulkhead with `PerScope`** (concurrency limit per tenant) and a round-robin batch budget per pass; per-tenant **rate** limits stay Resilience `RateLimiterPolicy` with `PerScope` | none (a tenant importing 2 million products slows everyone's outbox) | 04 §1.2 |
| **CL12** | Cross-node notices | `IClusterNotices`: topic + ≤ 4 KB payload, **best effort, at most once, no personal data**. Every consumer keeps a durable **version** it can re-read (notices only make it fast). Engines: in-memory; Postgres `LISTEN/NOTIFY`; a polling table (SQL Server, SQLite, fallback); later the Messaging bus | reliable delivery (needs a broker: Redis/RabbitMQ in every on-premises install) | 05 §5 |
| **CL13** | FusionCache backplane | **YFex.Persistence implements a FusionCache backplane over `IClusterNotices`** (bridge package `YFex.Persistence.Cluster`), so L1 caches invalidate across nodes without Redis | FusionCache's Redis backplane as Cluster's notice engine (Redis becomes mandatory for a two-node on-premises central) | 05 §5.2 |
| **CL14** | Node list | `cluster.nodes` table: heartbeat every **5 s**, a node is **gone after 15 s** without one (database clock). Used for placement, admin view, minimum version, metrics — **never for correctness** (leases are) | gossip between nodes (needs node-to-node network and authentication) | 02 §2, 05 §4 |
| **CL15** | Node-to-node traffic | **None in v1**: all coordination goes through the shared database (leases, nodes, notices). Trust = the database credential. On-premises evidence keys (Installations `Central` role) may later sign node records; direct notices over Installations' peer endpoint only if measured need | direct mTLS between nodes now (cloud processes have no installation certificate) | 08 §3 |
| **CL16** | Health, readiness, drain | `ClusterNodeState` **Joining → Ready → Draining → Left**; liveness = process responsive; readiness = Ready and own checks pass; drain order: not ready → wait for the balancer (configurable delay) → finish requests (budget) → stop workers at a safe point → release leases + notice → close WebSockets (1012 "service restart") → leave. **Web.AspNetCore maps the endpoints**, Hosting runs the drain for services | each app writes its own shutdown code (forgotten; leases expire instead of handing over) | 06 §1–§2 |
| **CL17** | Shared counters (later) | Two modes: **exact** (one database round trip per take, for money-like budgets) and **block** (a node takes a block of permits — e.g. 100 SMS — spends them locally and returns the rest at drain: cheap, never over-spends). Resilience's quota uses them through a bridge; Licensing meters use exact sums | a counter per owner (Resilience, Licensing, Notifications each write the same table code); in-memory per node (N nodes = N × the budget) | 05 §6 |
| **CL18** | Duplicate filter (later) | Cluster.Data ships a generic **claim-once table** (key, scope, state, result bytes, expiry); Messaging's `IIdempotencyStore` gets a shared adapter on it, and webhook ingress / vendor check-in use it | each owner its own table (three copies); keep `InMemoryIdempotencyStore` (a retry on another node runs the command twice) | 05 §7 |
| **CL19** | Sticky sessions (WebSocket RPC) | **No stickiness needed for correctness**: a WebSocket is bound to its node anyway; on reconnect any node serves; state must be rebuildable (Fusion RPC re-subscribes; invalidations reach every node by notice). Docs list balancer settings only (timeouts, drain) | require sticky cookies (breaks on scale-in, uneven load, Azure ARR affinity surprises) | 06 §6 |
| **CL20** | Rolling upgrades | Nodes publish their version and **feature tags** (`audit.seal.v2`); `IClusterFeatures.IsOnEverywhere(tag)` gates a new stored/wire format; once on, a durable **latch** keeps it on and a node that does not know a latched tag refuses to become Ready. Migrations run once through YFex.Data's existing migration lock | "deploy all at once" (downtime); version checks per library (each owner invents its own) | 06 §3 |
| **CL21** | Maintenance mode | Per node (= drain without stopping) and per scope (the scope's leases are released and not re-taken; Web answers 503 + `Retry-After` for that scope). Flags in `cluster.maintenance`, set through admin commands with permission | per node only (a tenant's data repair needs the whole cluster stopped) | 06 §4 |
| **CL22** | Clocks | Lease expiry uses the **database clock** only; nodes use local **monotonic** time to stop early. At heartbeat each node compares its YFex.Time trusted time with the database clock: skew above 2 s → metric and warning; above the lease margin (5 s) → node stays **not Ready** for leases | node wall clocks (a skewed node steals or keeps leases) | 05 §3, 06 §5 |
| **CL23** | Shop site | Cluster ships **contracts only** for the site; the **site engine is implemented by Offline/Installations** with the broker as coordinator (after Offline 4f broker choice and Hosting H0). **No consensus protocol** among terminals | Raft among terminals (weak devices, splits normal, complex to operate); no site support (app code differs at the shop) | 09 |
| **CL24** | What may run on a site lease? | **Low-stakes work only.** A worker declares the guarantee it needs; `LeaseGuarantee.Fenced` work **cannot be placed** on the site engine (it only grants `BestEffort`). Money and fiscal work stay with central or Offline's allowances | trust site leases for everything (a split at the store sends an SNGPC report or charges twice) | 09 §3 |
| **CL25** | Admin view | Read-only queries (nodes, lease and worker holders, tenant placement, features, maintenance) through Capabilities, permission `cluster.view`; commands (`cluster.operate`: drain node, move scope, maintenance) in a later phase, audited | an HTTP dashboard in the library (UI belongs to the apps) | 06 §7 |
| **CL27** | Work classes (user 2026-11-11: "the domain defines the work, the cluster groups and controls") | The **domain** declares a class id and default `WorkPriority` (Background…Critical) on workers, jobs and endpoints; Cluster carries the label and knows no domain names; ≤ 32 classes per cluster | priority per worker only (no grouping; the vendor cannot say "all fiscal") | 11 §1–§2 |
| **CL28** | Priority inside a node | **Weighted fair shares** of worker/scheduler slots per class with a **minimum share** (default 5 %, no starvation); Critical served first up to its share; per-tenant fairness (CL11) inside each class; optional `MaxConcurrentPerNode` | strict priority (low classes starve under load); FIFO (fiscal waits behind reports) | 11 §3.1 |
| **CL29** | Load shedding | Each node computes a **pressure level** (Normal/Elevated/High/Critical) from its own signals; the policy maps levels to the priorities paused (workers at a safe point, endpoints 503 + `Retry-After`); **Critical is never shed**. Resilience owns the admission gate (RE4) | no shedding (overload hurts everything equally); shedding per app (each invents it) | 11 §3.2 |
| **CL30** | Node roles and pools | A node announces **roles** at start (configuration, Kubernetes env, Hosting profile); the policy maps class → roles; placement filters by role; **runtime role changes** hand over at safe points without restart. No roles = everything (default) | a separate app per kind of work (microservices; heavier to deploy); no roles (fiscal cannot be isolated) | 11 §3.3 |
| **CL31** | Per-class metrics | `yfex.cluster.work.backlog / oldest_age / running / shed` by class and role, `yfex.cluster.pressure`; one node reports cluster-wide backlogs; later **capacity hints** the platform may read | per-owner metrics only (an autoscaler cannot scale "the fiscal pool") | 11 §3.4, 07 §2 |
| **CL32** | Runtime control (user: "configurable at runtime by my vendor API/orchestrator") | Layers: code defaults < app config < **versioned cluster policy** (`cluster.work_policy`) < scope overrides < **temporary overrides with expiry**. Changed by audited Cqrs commands (`SetWorkPolicy`, `SetNodeRoles`, `PreviewWorkPolicy`, `RevertWorkPolicy`; permission `cluster.operate`), validated, spread by notice + version re-read; last good version kept. On-premises: the policy arrives **signed** on the existing vendor → central channel (which channel: open) | static configuration (restart to change; no reaction to Black Friday); vendor writes the database directly (no validation, no audit) | 11 §4 |
| **CL33** | Creating machines | **Not Cluster**: nodes never hold credentials that create machines. A separate control-plane component owned by the vendor (proposed **`YFex.Provisioning`**: Kubernetes operator/API first, Pulumi or cloud SDKs later, Hosting profiles on premises) gets its own design plan and reads Cluster's metrics and capacity hints. **Plan written 2026-11-11: [provisioning](../provisioning/README.md) (⚑PV1–⚑PV27, pending)** | provisioning inside the app (a bug or breach can create or delete machines) | 11 §8 |
| **CL26** | Phase order ("start small") | P1 contracts + in-memory + harness; P2 Postgres engine; P3 host integration (health, drain) + first adopters; later counters, duplicate filter, upgrades, maintenance, clock check, admin view, SQL Server/SQLite, per-tenant spread, metrics; **site engine last** | per-tenant spread first (the cloud's biggest gain, but needs everything below it) | 10 §4 |

## 7. Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | what exists today (file:line), per library; defects found while checking |
| 02 | [02-model-and-contracts.md](02-model-and-contracts.md) | node, node list, lease, fencing token, keys, single worker, notice, health, features, engine seam |
| 03 | [03-api-and-examples.md](03-api-and-examples.md) | **dev-side code first**: composition roots per place, delivery poller before/after, Audit sequencer before/after, notices, drain, tests, cons |
| 04 | [04-three-places.md](04-three-places.md) | **main design**: vendor cloud (spread, fairness), on-premises central (one node free, failover, dialects, identity), shop site (summary) |
| 05 | [05-leases-fencing-and-engines.md](05-leases-fencing-and-engines.md) | lease algorithm, fencing, timing and clocks, node table, notices, counters, duplicate filter, engines per dialect |
| 06 | [06-health-drain-upgrades-operations.md](06-health-drain-upgrades-operations.md) | health, drain, rolling upgrades, maintenance mode, clock check, sticky sessions, admin view |
| 07 | [07-scaling-and-storage.md](07-scaling-and-storage.md) | the split between the platform and YFex: what scales what, metrics, per-tenant hourly history, storage growth |
| 08 | [08-platforms-security-aot.md](08-platforms-security-aot.md) | per-OS / per-host differences, threats, node trust, privacy, AOT |
| 09 | [09-site-engine.md](09-site-engine.md) | the shop: contracts, the site engine by Offline/Installations, low-stakes rule, scenarios |
| 10 | [10-phases-and-acceptance.md](10-phases-and-acceptance.md) | working rules, requests to other libraries, dependencies, phases CL-P0…CL-P9, acceptance, empty log |
| 11 | [11-work-classes-priority-and-control.md](11-work-classes-priority-and-control.md) | work classes, priority shares, load shedding, node roles/pools, per-class metrics, runtime policy from the vendor orchestrator, provisioning kept outside |

Related: [licensing 10](../licensing/10-phases-and-acceptance.md) (LC-P3/P4 decisions 2026-11-10), [audit 06 §4](../audit/06-tamper-evidence.md)
(sequencer), [notifications README ⚑N9](../notifications/README.md), [installations 06](../installations/06-site-mesh.md) (site mesh),
[offline 11 Phase 4](../offline/11-phases-and-acceptance.md) (broker choice 4f), [hosting](../hosting/README.md) (⚑H1, one host per
machine), [data-schema 04](../data-schema/04-migrations-and-versioning.md) (migrations).

## 8. Open questions (not blocking the decisions)

- **Redis as an optional engine** for very large clouds (leases and notices without database load). Not needed while one Postgres
  serves the cloud; would be an optional package (third-party client) if measured.
- **Several databases in the cloud** (tenants sharded across Postgres servers): leases of a tenant would live in the tenant's
  database. v1 assumes one coordination database per cluster; sharding is a later design with YFex.Data.
  **2026-11-11:** one cluster per region (agreed). Tenant placement, routing, residency, moves, replicas, region failover and
  backups/restore are designed in [databases-and-backups](../databases-and-backups/README.md) (⚑DB1–⚑DB30, pending); tenant
  registration, home region and residency in [tenancy](../tenancy/README.md) (⚑TN1–⚑TN18, pending).
- **Kubernetes Lease objects** (`coordination.k8s.io`) as an engine: avoided (cluster API credentials in the app, not portable to
  on-premises).
- **Who decides the scaled-in node?** Kubernetes picks the pod; Cluster only drains whatever is stopped. A "prefer to stop nodes
  holding few leases" hint (pod-deletion-cost annotation) could come with the metrics phase.
