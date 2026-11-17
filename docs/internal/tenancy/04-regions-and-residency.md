# 04 — Regions and residency

## 1. Region catalog (⚑TN4, ⚑TN15)

- `tenancy.regions` in the **global catalog database** (cloud), the same database as Data.Placement's catalog; replicated read-only to
  every region by the platform; writes only from the global region.
- A region is **recorded** only after the platform built it: its cluster (one per region, agreed in Cluster), coordination database,
  tenant databases (listed by Data.Placement), buckets (configured in Files). The record carries `Use`:
  - `Home`: tenants may live here;
  - `Standby`: target for failover only;
  - `BackupOnly`: off-site backups only;
  - `Closed`: no new tenants, no new data; existing tenants must move (05 §3).
- `OpenForNewTenants` lets the operator stop placing new tenants in a full region without closing it.
- On-prem: one implicit region (`on-prem`), no table (⚑TN14).

## 2. Choosing a new tenant's region (⚑TN5)

1. Pinned region in the registration (operator/sales) → used if the residency rule allows it and it is open.
2. Else the app's `IRegionPolicy` chooses among open `Home` regions allowed by the rule (default policy: the app's preference order,
   e.g. by customer country).
3. None → `NeedsRegion` (§4).

## 3. Residency (⚑TN6)

- Set at registration; changed only by `tenancy.residency.change` (inline approval, Audit). Narrowing it (e.g. `any` → `eu-*`) may force
  a region move and refuses while any data (replicas, backup destinations, standby) sits outside the new rule — the command lists them.
- **Checked by every library through `ITenantLocations`:**

| Library | Check |
|---|---|
| Data.Placement | home database, replicas, moves, failover targets in allowed regions |
| Files | the tenant's stores resolve only to buckets in allowed regions |
| Backup | destinations (incl. off-site) in allowed regions |
| Cluster | a cluster refuses work for tenants homed in another region (routing error, not a silent cross-region call) |
| Logging / metrics | carry tenant key and region only; no tenant data crosses regions through telemetry |

- The rule prevents **accidental** transfers. The legal basis for an international transfer (LGPD art. 33) is the app's/customer's
  record, not this library's.

## 4. "Do we need one more region?" (⚑TN7)

- **Detect:** a registration (or a residency change) that no open region satisfies stops in `NeedsRegion`; the registry emits a
  `tenancy.region.needed` event (Logging) and lists it in the admin view with the rule that failed (`eu-*`) and how many tenants wait.
- **Build:** platform/infrastructure work outside YFex — new cluster, databases, buckets, DNS.
- **Record:** the operator records the region (`IRegionCatalog.RecordAsync`) once it is ready; the registry re-runs waiting
  registrations automatically.
- **Capacity** ("this region is getting full") is not a registry concern: Data.Placement and Files publish capacity metrics; the operator
  adds databases or sets `OpenForNewTenants = false` and opens another region.
