# 07 — Scaling and storage: what the platform does, what YFex does

Agreed with the user (2026-11-10): **YFex does not scale servers or storage.** Autoscaling is the platform's job. YFex makes scaling
safe and gives the platform good signals.

## 1. The split

| Concern | Platform (not YFex) | YFex.Cluster | Other YFex owners |
|---|---|---|---|
| Add/remove server processes | Kubernetes HPA / KEDA, cloud scale sets, App Service scale-out, an admin starting a second Windows service | nodes join without configuration; leases, placement and drain make add/remove safe | — |
| Route traffic | balancer | readiness | Web.AspNetCore endpoints |
| Decide **when** to scale | HPA/KEDA rules, scheduled scaling (KEDA cron scaler, scale-set schedules) | publishes the signals (§2) and the **per-tenant hourly history** (§3) | owners publish their backlog gauges |
| Pick which pod to remove | Kubernetes (pod-deletion-cost hint possible later) | drains whatever is stopped | — |
| Database size, IOPS, replicas, failover | managed database (storage autogrowth, read replicas, HA) | none | YFex.Data (connections, dialects) |
| Table growth | — | none (its own tables are tiny and swept) | each owner's retention/archiving/partitioning (§4) |

## 2. Signals the platform scales on (YFex.Logging metrics)

All are BCL `Meter` instruments, exported by whatever the app configures (OpenTelemetry exporter, Prometheus scraping, Azure Monitor).
KEDA's Prometheus scaler or the HPA external-metrics adapter reads them. **No tenant dimension** (cardinality; per-tenant data is §3).

| Signal | Instrument | Owner | Scale when |
|---|---|---|---|
| Request rate, latency | `yfex.operation.count`, `yfex.operation.duration.p99` (Meter `YFex.Logging.Operations`, built) | Logging | p99 above target / rate per pod above target |
| Notifications backlog | `yfex.notifications.outbox.pending`, `.oldest.age` (to add) | Notifications | oldest pending age > 1 min |
| Jobs backlog | `yfex.jobs.due`, `yfex.jobs.lateness` (to add or confirm; Jobs has `JobLateness`) | Jobs | due jobs late > threshold |
| Audit backlog | `yfex.audit.unsealed`, `.oldest.age` (to add) | Audit | unsealed age > SealInterval × 10 |
| Server-side replay / ingest queues | `yfex.messaging.ingest.pending` (to add when a server queue exists) | Messaging | |
| Work classes (⚑CL31) | `yfex.cluster.work.backlog`, `.oldest_age` by class and role; `yfex.cluster.pressure` ([11 §3.4](11-work-classes-priority-and-control.md)) | Cluster | backlog of a class grows → scale the pool of its role |
| Cluster health | `yfex.cluster.nodes`, `yfex.cluster.lease.takeovers`, `yfex.cluster.lease.renew.failures` (02 §9) | Cluster | not a scaling signal: an alarm |

Backlogs are measured from the **shared store** by the node holding a light `Worker("…metrics")` lease (one node reports the
cluster-wide number; every pod reporting the same number would multiply it in sums). Cluster offers the pattern
(`cluster.ClusterGauge("yfex.notifications.outbox.pending", read)`); owners provide the read.

## 3. Per-tenant, per-hour load history (busy hours)

**Why:** pharmacies have strong daily and weekly peaks (opening hours, lunch, the evening rush, the 5th business day when chronic
patients buy). Reactive autoscaling lags behind a peak by minutes; **scheduled** scaling ("scale to 8 pods weekdays 11:30–14:00 BRT")
needs history. And the vendor wants to know which tenants make the peaks (sizing contracts, pinning a big chain, ⚑CL10 cons).

```sql
create table cluster.load_hourly (
    scope       varchar(400) not null,          -- top-level tenant (TenancyPath depth 1); "" = cluster total
    hour_utc    timestamptz  not null,
    kind        varchar(100) not null,          -- "requests", "notifications.sent", "jobs.run", "audit.sealed", app kinds
    count       bigint       not null,
    busy_ms     bigint       not null,          -- summed duration
    max_minute  bigint       not null,          -- the busiest minute's count inside the hour
    primary key (scope, hour_utc, kind)
);
```

- Each node accumulates in memory (a per-(scope, kind) `long` pair, no allocation per event after the first) and upserts **additively**
  once a minute (`count = count + @c`, `max_minute = greatest(...)`). One row per tenant, hour and kind; ~300 tenants × 5 kinds × 24 h
  ≈ 36 000 rows a day; retention **400 days** (a full year of seasons), configurable; older rows are rolled up to daily.
- Feeds: Web request counting (per tenant, middleware), and owners calling `IClusterLoad.Add(scope, kind, count, duration)`.
- Read side: `ClusterLoadProfileQuery(scope?, kind, weeks: 8)` → expected count per **hour of week** in the tenant's time zone (the
  zone comes from Tenancy), with p50/p95. Operators turn it into scheduled-scaling rules; generating KEDA cron triggers automatically is
  a later idea, not a promise.
- Privacy: a tenant id is not personal data; no user, session or customer ids are stored. On-premises: off by default (one customer,
  nothing to compare); the vendor cloud turns it on.
- In-memory engine: history disabled (no table).

## 4. Storage growth stays with its owners

| Data | Grows by | Owner's answer |
|---|---|---|
| Audit entries and seals | every audited action | Audit retention classes, day seals, archiving to object storage (audit 07, 08) |
| Logs | every operation | Logging storage tiers, retention (logging 14) |
| Jobs history | every run | `PruneHistoryAsync`, `JobHistorySampler` (Jobs) |
| Notifications outbox | every message | N2 retention of sent messages |
| Messaging idempotency / Cluster claims | every command | retention per kind (05 §7) |
| Cluster tables | leases, nodes, notices, counters, load history | swept by Cluster (free leases 7 d, nodes 1 d, notices 5 min, counters after their period + 35 d, history 400 d) |
| Business tables | sales, stock movements | the app + YFex.Data (partitioning, archiving) |

Database size, autogrowth and replicas are the managed database's or the customer's DBA's. YFex.Data owns partitioning helpers if they
are ever needed (none requested by this design).
