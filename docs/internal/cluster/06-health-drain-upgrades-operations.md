# 06 — Health, drain, rolling upgrades, maintenance, clock check, sticky sessions, admin view

## 1. Health, readiness, liveness (⚑CL16)

| Probe | Meaning | Answers 200 when | Typical consumer |
|---|---|---|---|
| **startup** | the process finished starting | the node reached `Ready` once | Kubernetes `startupProbe` (slow first migration) |
| **liveness** | the process is not stuck | the runner and heartbeat loops ticked within 3× their interval | Kubernetes `livenessProbe` (restart), Hosting supervisor |
| **readiness** | send me traffic | state `Ready` **and** every `IClusterReadinessCheck` passes (database reachable, migrations at the expected version, licensing evaluated, trusted time anchored if the app requires it) | balancer, Kubernetes `readinessProbe`, IIS ARR health test |

- Liveness never depends on the database: a database outage must not make Kubernetes restart every pod (that would turn an outage
  into a restart storm). Readiness may.
- The endpoints are mapped by **YFex.Web.AspNetCore** (`app.MapYFexHealth()`, request 10 §2) from `IClusterHealth`; Cluster has no
  ASP.NET Core reference. YFex.Hosting reads the same `IClusterHealth` for its module status, tray and CLI.
- Responses: plain `200`/`503` with a short JSON body `{ "state": "Draining" }`; no versions, hosts or tenant names (information
  disclosure); details only on the admin view (§7) with permission.

## 2. Drain (⚑CL16)

Triggered by the host stopping (`IHostApplicationLifetime.ApplicationStopping`: SIGTERM, Windows service stop, Ctrl+C, IIS recycle),
by maintenance (§4), by an operator command, or by an upgrade step.

| Step | Order | What happens | Budget (default) |
|---|---|---|---|
| 1. Not ready | — | state `Draining`; readiness 503; heartbeat says `Draining`; placement stops choosing this node | 0 |
| 2. Wait for the balancer | — | keep serving normally while the balancer notices readiness (it polls every few seconds) | 5 s (`WaitForBalancer`; 0 for on-premises without a balancer) |
| 3. Requests | 100 | stop accepting new HTTP requests (Kestrel), wait for in-flight ones | 20 s |
| 4. Workers | 200 | set `HandOverRequested`; wait for each worker to return; then cancel its token; then wait again | 10 s |
| 5. Leases | 300 | release every lease still held + `cluster.released` notices (next holders acquire in ~1 s) | 2 s |
| 6. Sockets | 400 | Messaging.Rpc closes WebSockets with status **1012 (service restart)**; clients reconnect elsewhere and re-subscribe | 3 s |
| 7. Leave | — | heartbeat `Left`; listener closed | 1 s |

- The total (≈ 40 s) must fit the host's stop timeout (08 §1). If the host kills the process earlier, nothing breaks: leases expire,
  claims are reclaimed; only the takeover is slower.
- Drain is **idempotent** and can be cancelled only by an operator command (`cluster.operate`), never by a request.

## 3. Rolling upgrades (⚑CL20)

During a rolling deploy, version N and N+1 run together for minutes. Two things can go wrong: two nodes migrate the database at once,
and the new version writes a stored or wire format that old nodes cannot read (YFex rule: a byte format changes only with a new
version tag and a golden test).

### 3.1 Feature tags

- Each library declares the format tags its binary **understands**: `cluster.Understands("audit.seal.v2")`. Tags are published in the
  node record.
- Before **writing** a new format, the owner asks `IClusterFeatures.IsOnEverywhereAsync("audit.seal.v2")`: true only when every
  `Ready` and `Draining` node understands it. Until then it keeps writing v1 (it can already read v2).
- **Latch:** the first time it is true, the tag is written to `cluster.features` (latched). From then on it stays on even if an old
  node appears, and **an old node that does not understand a latched tag refuses to become Ready** (startup error naming the tag):
  rolling back a binary below a latched format is prevented instead of corrupting data.
- In-memory engine: the node's own tags are "everywhere".
- Site engine (later): the broker computes the same from the roster's app versions.

### 3.2 Minimum version

`ClusterView.MinimumVersion` (lowest `NodeIdentity.Version` among ready nodes) is shown in the admin view and can gate admin actions
("rotate keys only when every node runs ≥ 2.4"). Feature tags are preferred over version comparisons in code.

### 3.3 Migrations exactly once

Already solved by YFex.Data (`SchemaInstaller` + `AcquireMigrationLock`: Postgres advisory lock, SQL Server applock, SQLite per unit).
Cluster adds only a readiness check "schema at the version this binary expects" so a node does not become Ready before the migration
another node is running has finished. Expand/contract migrations (add column → both versions run → drop later) are YFex.Data's
guidance, referenced, not repeated.

## 4. Maintenance mode (⚑CL21)

| Mode | Effect | Set by |
|---|---|---|
| **Node** | drain without stopping the process (steps 1–6); the node stays up for diagnostics; `cluster.operate` resumes it | operator command, Hosting CLI |
| **Scope** (tenant) | every lease whose key has that scope is released and **not re-acquired** by any node; Web answers requests for that scope with **503 + `Retry-After`**; background work for other tenants continues | operator command (data repair, tenant move) |
| **Cluster** | every node in node maintenance; readiness 503 | operator command before a risky database operation |

Flags live in `cluster.maintenance` (scope, mode, until, reason, actor) and reach nodes by notice and at heartbeat. Commands are
audited (YFex.Audit) and need `cluster.operate`. Web's 503 per scope is a Web.AspNetCore middleware reading `IClusterMaintenance`
(request 10 §2).

## 5. Node clock check (⚑CL22)

At each heartbeat: `skew = trustedNow(YFex.Time) − databaseNow` (round-trip-corrected). Metric `yfex.cluster.clock.skew`; warning
above 2 s; above `SafetyMargin` (5 s) the node leaves `Ready` for leases (keeps serving requests unless a readiness check says
otherwise) until it is back below 2 s. Leases never use wall clocks (05 §3), so this check catches **broken machines**, not normal
drift. When YFex.Time has no anchor, the check compares with the device clock and says so in the metric dimension.

## 6. Sticky sessions and WebSocket RPC (⚑CL19)

- No stickiness needed for correctness. A WebSocket stays on the node it connected to; on drain it gets 1012 and reconnects to any
  node; Messaging.Rpc re-subscribes and Fusion RPC refreshes computed values. Per-connection state must be rebuildable from the client
  or the database, never kept only in a node's memory.
- Cross-node invalidation: a change on node B must invalidate computed values watched on node A → `messaging.fusion.invalidate` notices
  (request 10 §2, Messaging owner).
- Balancer notes (docs only): WebSocket idle timeout ≥ RPC keep-alive × 2; connection draining on (AWS ALB deregistration delay, nginx
  `worker_shutdown_timeout`, YARP drain); **Azure App Service "ARR affinity" is on by default** — leave it on or off, both work; IIS ARR
  health test URL = `/health/ready`.

## 7. Admin view (⚑CL25)

Read-only queries (CQRS, YFex.Cqrs) exposed as Capabilities, permission catalog `ClusterPermissions`:

| Query | Shows |
|---|---|
| `ClusterNodesQuery` | nodes: id, app, version, host, installation, state, features, last seen, leases held count |
| `ClusterLeasesQuery(kind?, scope?)` | key, holder node, token, acquired, expires in, last fence |
| `ClusterPlacementQuery(kind)` | per scope: preferred node, actual holder, pinned |
| `ClusterFeaturesQuery` | tags, latched at, nodes not understanding |
| `ClusterMaintenanceQuery` | active maintenance flags |

- `cluster.view` for queries; `cluster.operate` (later phase) for: drain node, resume node, maintenance on/off, move scope (release a
  scope's lease so the preferred node takes it), unpin. Every command audited.
- Tenant admins never see the cluster (it is the vendor's or the on-premises IT's view). Lease keys contain tenant paths: the query
  filters by the caller's scopes when the caller is not a cluster operator.
- UI belongs to the app (vendor console, on-premises admin); Capabilities makes the actions searchable and AI-callable with the same
  permissions.
