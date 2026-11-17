# 04 — The three places it runs (main design)

The same contracts (02) run in three places with different engines and guarantees. App code is written once; the composition root
picks the engine, and the guarantee a worker requires decides where it may run.

```
 VENDOR CLOUD (primary)                     ON-PREMISES CENTRAL                     SHOP SITE (last phases)
 ┌─────────────── balancer ───────────┐     ┌──────── (balancer or DNS) ──────┐      ┌──────── store LAN ─────────────┐
 │ pod A   pod B   pod C … (autoscaled)│     │ SRV-01            [SRV-02]       │      │ PC-GERENTE (broker, coordinator)│
 │   └──────┬───────┘                  │     │   └──────┬────────┘              │      │ CAIXA-01  CAIXA-02  (terminals) │
 │   managed Postgres: cluster.* tables│     │ Postgres / SQL Server / SQLite   │      │ no shared database; mTLS mesh   │
 │ engine: Data · Fenced               │     │ engine: InMemory (1) / Data (2)  │      │ engine: Site (Offline) · BestEffort │
 └─────────────────────────────────────┘     └──────────────────────────────────┘      └─────────────────────────────────┘
```

## 1. Vendor cloud (SaaS central, vendor licensing service)

**Shape:** short-lived processes (pods, scale-set instances) behind a balancer, added and removed by the platform at any time;
managed Postgres; **many tenants per process**. The vendor licensing service (`YFex.Licensing.Vendor`, LC-P4) runs here too, often
in the same process as the SaaS central (`IVendorLicensing` in-process).

**What runs where:**

| Work | Kind | Placement |
|---|---|---|
| HTTP / RPC requests | any node | the balancer |
| Notifications dispatch, per tenant | `WorkerPerScope` | spread (§1.1) |
| Audit sequencing, per chain (tenant · class · day) | `WorkerPerScope` | spread |
| Licensing activation-list sender, per tenant (LC-P3/P4) | `WorkerPerScope`, Fenced | spread |
| Vendor check-in sweeper (find on-premises centrals that missed check-ins) | `Worker` (one) | any node |
| Exports and long reports, per tenant | Jobs claim, optionally per-tenant concurrency (§1.2) | the node that claims |
| Installations registry event consumers (seat counts, activation deltas) | `WorkerPerScope` per tenant, or one `Worker` with a cursor | spread |
| Messaging server-side replays, Fusion invalidation | every node, with notices | — |

### 1.1 Per-tenant spread of background work (⚑CL10)

**Problem:** with one worker per tenant and 300 tenants, the first pod that starts would take all 300 leases; a new pod added by the
autoscaler would get none, so background work never scales out.

**Mechanism:**

1. **Which keys exist:** the owner of the work lists scopes with work (`IClusterWorkScopes`, ⚑CL9) — the Notifications outbox returns
   tenants with pending messages; Audit returns open chains. Refreshed every 30 s and on the owner's notice ("new work for tenant X").
   Tenants with nothing to do hold no lease.
2. **Who should own a key:** **rendezvous (highest-random-weight) hashing**: for key *k*, every Ready node computes
   `score(node, k) = weight(node) × −1/ln(hash(node.Id, k) / 2⁶⁴)` and the highest score is the **preferred** node. Every node computes
   the same answer from the same node list, with no leader and no placement table. A node join or leave moves only ~1/N of the keys.
3. **Who competes:** the preferred node tries at once; the second-ranked node tries only when the lease has been free for a grace (default
   = one lease duration), the others after two. So a crash moves the key to the next-ranked node, not to whoever polls first.
4. **Join (scale-out):** a new node becomes preferred for ~1/N of the keys. Current holders see they are no longer preferred and set
   `HandOverRequested` — but only after the new node has been **Ready for 60 s** (no churn during a deploy wave) and at most **N
   handovers per node per minute** (default 10). The worker returns at its next safe point, the runner releases, the notice wakes the
   preferred node, which acquires within ~1 s.
5. **Leave (scale-in, drain):** the draining node hands over everything; keys go to their next-ranked nodes.
6. **Disagreement is harmless:** two nodes with different views of the node list may both believe they are preferred; the lease
   decides, and the loser simply waits. Placement is an optimization; **the lease is the only truth**.

**Cons:** keys are spread by count, not by load (a tenant with 100× the messages counts as one key). Weighting by measured load
(from the hourly history, 07 §3) is a later refinement; until then, a very large tenant can be **pinned** to a node by configuration
(`cluster.Pin("notifications.dispatch", "0=bigchain", weightFactor: 5)`) — an operator lever, not a default.

### 1.2 Per-tenant fairness and concurrency (noisy neighbour, ⚑CL11)

| Risk | Answer | Owner |
|---|---|---|
| a tenant's 2-million-row import occupies every export slot | Jobs claim + a **per-scope bulkhead** (YFex.Resilience bulkhead with `PerScope`: e.g. 2 concurrent exports per tenant, 16 per node) | Resilience policy, applied by Jobs / the app |
| one tenant's outbox has 100 000 messages, the others wait | the dispatcher worker is per tenant (spread), so other tenants' workers run on their own leases; inside one node, workers share a **round-robin** scheduler with a batch budget per pass | Cluster runner + Notifications |
| one tenant floods the API | per-tenant **rate** limiter: Resilience `RateLimiterPolicy` + `PerScope` at the Web/RPC entry; per-tenant **budget** (SMS per day) = Resilience quota on shared counters (05 §6) | Resilience, Web |
| a tenant's slow database queries starve the pool | per-tenant connection limits are YFex.Data's (out of scope here); the metric shows it | Data |

Cluster's own contribution is small: the per-tenant worker split, the round-robin runner, and `MaxConcurrencyPerScope` on
`WorkerPerScope`, implemented with the Resilience bulkhead so there is one concept of "limit per scope".

### 1.3 Identity in the cloud

Cloud processes are **not installations** (⚑CL4): thousands of short-lived pods would flood the registry and need certificates
issued at every start. A node is a `ClusterNodeId` + `NodeIdentity` with `Installation = null`; trust = the database credential and
the platform (network policy, managed identity). Calls **from** installations (terminals) to the cloud still carry installation
assertions (`InstallationCallContext`), unchanged.

## 2. On-premises central (the customer's server)

**Shape:** usually **one node**; sometimes **two** for failover (two VMs behind a small balancer or DNS name, or an active/passive
pair under Windows failover clustering). The database may be **Postgres, SQL Server or SQLite**. No platform autoscaling; the admin
starts and stops services by hand or through YFex.Hosting / the Windows service manager.

### 2.1 One node must cost nothing

- Default engine **in-memory** when nothing is configured: leases granted at once with an in-process token, notices in-process, no
  database tables created, no heartbeat writes. Health and drain still work (they matter for a Windows service stop too).
- Code paths are the same as in the cloud, so the two-node setup is a configuration change, not a code change.

### 2.2 Two nodes: engines per dialect through YFex.Data

| Database | Leases and nodes | Notices | Transaction-scoped locks (registry last seat) | Limits |
|---|---|---|---|---|
| **Postgres** | `cluster.leases` / `cluster.nodes` tables (database clock `clock_timestamp()`) | `LISTEN/NOTIFY` on channel `yfex_cluster` (payload ≤ 4 KB, under Postgres' 8 KB) + table fallback | `pg_advisory_xact_lock(key hash)` | none |
| **SQL Server** | same tables (`SYSUTCDATETIME()`) | polling table `cluster.notices` every 1 s (rows kept 5 min) | `sp_getapplock @LockOwner = 'Transaction'` | notices ~1 s late (Service Broker / query notifications rejected: setup burden on customer servers) |
| **SQLite** | same tables in the same file | in-process only | `BEGIN IMMEDIATE` (database-wide write lock) | **single machine only**: SQLite over a network share is unsafe, so a second node on another machine is refused at join (the node table records the host; a different host → startup error with the reason) |
| Oracle | not planned (no current central on Oracle) | | | add when needed |

Transaction-scoped locks are a **YFex.Data** primitive (request 10 §2), not a Cluster lease: they protect one transaction (the
Installations registry enrolling the last seat), are released by commit/rollback, and need no token.

### 2.3 Failover behaviour

- Active/active (both nodes serve requests): workers spread over the two nodes; one node down → the other takes its keys after ~35 s
  (crash) or ~1 s (drain).
- Active/passive (Windows failover cluster moves the service): the passive node is not running, so it is simply a new node when it
  starts; leases of the old node expire (or were released at its stop).
- Database failover (SQL Server Always On, Postgres replica promotion): lease rows replicate with the data; a promotion that loses
  the last seconds may lose a renewal → holders see a renew failure and stop at the safety margin. A promotion may also hand out a
  token value again (the sequence rolled back with the lost seconds), but the writes fenced with the lost token were lost in the same
  failover, because fence and data live in the same database (05 §2.3).

### 2.4 Node identity on-premises (⚑CL4, ⚑CL15)

- The central server is an installation with the **`Central` role** and evidence keys (YFex.Installations). Its node fills
  `NodeIdentity.Installation`, so the admin view says "SRV-01 (installation 0196…)".
- Could nodes authenticate to each other with those evidence keys? Yes, but in v1 **nodes never talk to each other**: everything goes
  through the database, whose credential is the trust boundary. A later phase may sign node records with the evidence key so a
  rogue process with database access but no installation key shows as "unverified" in the admin view (detect, not prevent).

## 3. Shop site (broker + terminals, offline mesh) — summary; design in [09](09-site-engine.md)

- **Cluster does not own coordination there.** Installations and Offline do (friends, gossip, broker ranking, Offline 4f): there is
  no shared database, splits are normal, devices are less trusted, trust comes from enrollment evidence and mTLS.
- App code needs the **same contracts** ("only one machine in the store polls the delivery marketplace", "only one sends the SNGPC
  report"). So Cluster ships the contracts and the runner, and **Offline/Installations implement `IClusterEngine` for the site**,
  with the Active broker as coordinator.
- **Low-stakes work only** (⚑CL24): the site engine grants `BestEffort` leases; a worker that requires `Fenced` cannot be registered
  there. Money and fiscal work stay with central or Offline's allowance model. **No consensus protocol** among terminals (⚑CL23).
- Built in the **last phases** (CL-P8/P9), after Offline 4f (broker choice) and Hosting H0.

## 4. Same app, three places

| Code | Cloud | On-premises (1 node) | On-premises (2) | Shop |
|---|---|---|---|---|
| `DeliveryOrdersPoller : IClusterWorker` (BestEffort enough; orders dedup by id) | per tenant, spread | runs at once | one of the two | one machine of the store |
| `ActivationListSender` (Fenced) | per tenant, spread | runs at once | one of the two | **refused at registration** |
| `ChainSequencer` (Fenced) | per chain, spread | runs | one per chain | not registered (sites ship entries, central seals) |
| `/health/ready` | balancer readiness | Hosting status | balancer or DNS health | Hosting status (tray) |
