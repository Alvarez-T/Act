# 05 — Moving tenants, read replicas, failover

## 1. Moving a tenant (⚑DB9)

| Step | What happens | Tenant sees |
|---|---|---|
| Plan | admin command `placement.move` (permission, audited); checks residency, target schema version ≥ source | nothing |
| Copy | logical export of the tenant (same code as the per-tenant backup, 06 §2) streamed into the target, **not** through files | nothing |
| Catch up | repeat deltas: rows changed since the copy start (`updated_at`/row version columns, or Postgres logical decoding filtered by tenant where available) until the delta is small | nothing |
| Pause | Cluster **tenant maintenance** (⚑CL21): tenant's leases released, Web returns 503 + `Retry-After` for that tenant only; target seconds | ~seconds of 503 |
| Final delta + switch | last delta; `placement.tenants.home = target`, `version + 1`; notice `placement.changed`; source rows marked read-only | |
| Verify | row counts and checksums per table between source and target | |
| Resume | maintenance off; workers for the tenant start on the new database | normal |
| Retire | after N days (default 7) the source rows are deleted by a job; until then a rollback is a switch back | |

- Side effects in flight (outbox rows, Jobs runs in `Running`) move as rows; their owners' `OnRestore`/`OnMove` rules (03 §6) say
  whether they continue on the target.
- Cross-region moves are the same steps plus a residency check; copy traffic crosses regions **once**, encrypted in transit (TLS).
- Owners with their own sequence or chain state (Audit chains, Cluster fencing tokens, Jobs next-fire) are told by an event
  `TenantMoved(tenant, from, to, version)` and must not assume the database-local sequence continues (requests 09 §2).

## 2. Read replicas (⚑DB10, ⚑DB29)

- **Who decides what:** the **query** declares whether old data is acceptable — in the Cqrs **Aggregate Configuration**
  (`.ReadFromReplica(maxLag)` / `.ReadFromPrimary()`), or `ITenantDatabase.Read(maxLag)` inside a method; **Placement** decides which
  replica and when to fall back; **Cluster** only runs the lag probe on one node per region. Cluster or an orchestrator cannot decide,
  because only the query knows whether a 5-second-old answer is safe.

- Configured by the platform; listed in `placement.databases` as `Replica` with `primary_of`.
- Lag: platform metric when available; else `now() - pg_last_xact_replay_timestamp()` (Postgres) / `sys.dm_hadr_database_replica_states`
  (SQL Server) sampled every 5 s by one node per region (Cluster worker) and published as a notice + gauge `yfex.data.replica.lag`.
- **Read-your-writes:** a request scope that wrote stays on the primary for a window (default 5 s) — the router keeps a per-scope
  "last write" timestamp (and Web can carry it in a cookie for the next request).
- Never used for: commands, anything under `DataIntent.Write` or `ReadFromPrimary`, fenced work, money/fiscal checks.

## 3. High availability inside a region

Platform job (managed Postgres HA, Patroni, SQL Server AG). The router reconnects on failure through Resilience retry with the same
host name (the platform moves the endpoint). Cluster's fencing tokens survive because the lease table is in the coordination
database, which has its own HA.

## 4. Failover between regions (⚑DB11)

Manual, from a runbook, because asynchronous replication across regions loses the last seconds of writes and residency limits which
region may take over.

1. Operator declares region A **down** (admin command `placement.region.failover`, two-person approval).
2. Platform promotes the standby databases in region B (allowed by every affected tenant's residency — the command lists tenants
   that **cannot** fail over and leaves them paused).
3. Catalog: A's databases → `Retired`/`Standby`, B's standbys → `Primary`, tenants' `home` updated, versions +1.
4. B's cluster accepts those tenants; DNS/edge routes them to B (platform).
5. **Restore epoch** bump for every moved tenant (06 §4): Cluster fencing tokens, outbox and Jobs claims re-checked, since the lost
   window may have contained sends that B does not know about.
6. Failback is a normal tenant move (§1) once A is healthy.

The plan reports the expected data loss (RPO = replication lag at failure) and records it in Audit.
