# 08 — Platforms and hosts, security, privacy, AOT

## 1. Per-OS and per-host differences (rule: infrastructure.md "Cross-platform")

YFex.Cluster's own code is BCL only and identical everywhere. What differs is **how the stop signal arrives and how long the host
waits**, which decides whether a drain completes or leases simply expire.

| Host | Stop signal | Time allowed by default | What to configure | Readiness consumer |
|---|---|---|---|---|
| **Linux container (Kubernetes)** | SIGTERM → .NET `ConsoleLifetime` → `ApplicationStopping` | `terminationGracePeriodSeconds` 30 s; .NET `HostOptions.ShutdownTimeout` 30 s | grace period ≥ 60 s, `ShutdownTimeout` ≥ drain total + 5 s; optional `preStop` sleep = WaitForBalancer (then set WaitForBalancer 0) | `readinessProbe` |
| **Linux container (plain Docker, ECS, Container Apps)** | SIGTERM | `docker stop` 10 s (!), ECS `stopTimeout` 30 s | raise `--stop-timeout` / `stopTimeout` | the platform's health check |
| **Linux systemd service** | SIGTERM (`KillMode`), then SIGKILL | `TimeoutStopSec` 90 s | `Type=notify` with `UseSystemd()`; `TimeoutStopSec` ≥ drain | none (Hosting status) or a balancer |
| **Windows service** (YFex.Hosting / `UseWindowsService`) | SCM stop → `WindowsServiceLifetime` | SCM waits per the service's wait hints; system shutdown gives ~20 s (`WaitToKillServiceTimeout`) | `HostOptions.ShutdownTimeout`; Hosting requests additional time while draining; **system shutdown cuts the drain short**: leases expire instead | Hosting status / tray; balancer if two servers |
| **IIS (in-process or out-of-process)** | app pool recycle / stop → `ApplicationStopping` | `shutdownTimeLimit` 90 s; **overlapped recycle starts the new process before the old stops** | nothing special: two processes are two nodes (⚑CL3); leases hand over | ARR health test or none |
| **macOS launchd** (Mac terminals; no central planned) | SIGTERM, then SIGKILL after `ExitTimeOut` (20 s) | 20 s | `ExitTimeOut` in the plist | — |
| **Azure App Service** | SIGTERM (Linux) / process stop (Windows); slot swaps | ~30 s | health check path = `/health/ready`; ARR affinity either way (06 §6) | built-in health check |
| **Console / developer run** | Ctrl+C → `ApplicationStopping` | until the drain ends | none | — |

Notes:
- **Who owns the host side:** YFex.Hosting (service hosts, per-OS packages), ASP.NET Core / Web.AspNetCore (HTTP), the platform
  (manifests). Cluster only reacts to `ApplicationStopping` and exposes `IClusterHealth` (⚑CL16). No `.Windows`/`.Linux`/`.MacOS`
  package in Cluster.
- **Containers vs services:** containers are disposable and get a fresh `ClusterNodeId` (and a fresh `NodeIdentity.InstanceId` when
  the file system is not persisted); Windows services on-premises keep their `InstanceId` and installation link across restarts.
- **Postgres listener on Windows and Linux:** the same Npgsql code; TLS to managed databases through Npgsql settings owned by
  YFex.Data. Nothing OS-specific.
- Unverified platform behaviour (Windows service stop under system shutdown, Container Apps timeouts) is a **spike** in CL-P3.

## 2. Threats and answers

| Threat | Answer | Residual |
|---|---|---|
| Paused node writes after its lease expired (GC, VM freeze, network cut) | fencing in the same transaction (05 §2) | external side effects: idempotency keys |
| Clock skew between nodes | database clock for expiry, monotonic local deadlines, skew check (05 §3, 06 §5) | a database server with a wrong clock affects everyone equally (still consistent) |
| Split between a node and the database | node stops at its local deadline; another takes over | a few seconds of no progress |
| Rogue process with the database credential | can take leases and publish notices: **the database credential is the trust boundary** (⚑CL15). Mitigations: a dedicated database role for `cluster.*` tables (no access to business tables needed for coordination; the business role cannot write `cluster.*` except `FenceAsync`'s row); admin view shows unknown nodes; on-premises nodes may later sign node records with their installation evidence key | a holder of the business credential can already change data directly; Cluster adds no new power |
| Notice spoofing / flooding | notices carry no authority: consumers re-read their state from the database; payload size limit; per-topic rate metric | — |
| Notices leaking data | **no personal data** in notices (topic + ids/keys only); Privacy's rule applies | — |
| Lease starvation by a buggy worker that never returns | runner forces stop after the drain budget; `HandOverRequested` ignored → logged; admin "move scope" | — |
| Maintenance flags abused | `cluster.operate` permission, audited | — |
| Site: a less trusted terminal claims a site lease | site engine trusts installations by enrollment evidence + mTLS; low-stakes only (09) | split → duplicate low-stakes work |

## 3. Node trust (⚑CL15, ⚑CL4)

- **v1:** no node-to-node traffic. A node is trusted because it reaches the coordination database with the right credential.
- Cloud nodes: not installations; platform identity (managed identity, network policy) protects the database.
- On-premises nodes: `NodeIdentity.Installation` links the `Central` installation, shown in the admin view.
- Later (optional): node records signed by the installation evidence key (YFex.Cryptography `SignedPayload`, domain
  `yfex.cluster.node.v1`); unsigned or unknown-key nodes flagged "unverified". Detection, not prevention.
- Direct node-to-node calls (if a measured need appears, e.g. notices without database load) would use Installations' peer endpoint
  (mTLS, tenant CA) on-premises — never in the cloud, where nodes have no certificates.

## 4. Privacy

Cluster stores node metadata (host names, process ids), lease keys (tenant paths), counters and hourly counts per tenant. No personal
data. Lease keys and claim keys chosen by owners must not contain personal values in clear (Privacy rule: use ids, hashes or keyed
hints). The duplicate filter stores the owner's **result bytes**: the owner classifies and, if needed, seals them (YFex.Cryptography)
before handing them over.

## 5. AOT, trimming, performance

- `IsAotCompatible`, zero IL warnings, `.AotSmoke` app (in-memory engine + a worker + drain; Data engine against SQLite in the smoke).
- No reflection: workers are registered with generic methods (`Worker<T>`), resolved with `GetService<T>()`.
- Encodings (notice envelope, node features) are hand-written spans; **wire formats** (Postgres notice payload, node features text,
  site lease messages) carry a version tag and golden tests (stored and wire format rule).
- Hot paths: `IClusterLeases.TryAcquireAsync` on the in-memory engine (≤ 100 ns, 0 B after warm-up), `Lease.IsHeld` (a field read),
  `FenceAsync` (one round trip, allocation-free after warm-up apart from the driver), load-history `Add` (≤ 20 ns, 0 B). BenchmarkDotNet
  + allocation tests.
- Dependencies: `YFex.Cluster` → YFex core, YFex.Resilience, `Microsoft.Extensions.Hosting.Abstractions`; `YFex.Cluster.Data` → YFex.Data.
  No third-party package (Npgsql arrives through YFex.Data.Postgres, which the app already references).
