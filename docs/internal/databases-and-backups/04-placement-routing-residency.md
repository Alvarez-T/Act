# 04 — Placement, routing, residency

## 1. Unit of placement and sharding model (⚑DB2, ⚑DB6)

- A **tenant** (first segment of `TenancyPath`) lives in exactly **one home database**. Stores and branches follow their tenant, so
  per-tenant reports never join across servers.
- Many small tenants share a database (pool) and are isolated by YFex.Data row security, as today. A large tenant can be placed in a
  database tagged `dedicated` — same code, same schema.
- "Sharding" therefore means **spreading tenants across databases**, never one tenant across servers. If a tenant outgrows one
  server, the answer is a bigger server or splitting the product (archives, read replicas), not intra-tenant sharding.
- Global data (product catalogues shared by all tenants, vendor tables, the placement catalog itself) lives in a **global database**
  per deployment, read-mostly, replicated by the platform to each region.

## 2. The catalog (⚑DB3)

| Table | Columns (sketch) |
|---|---|
| `placement.databases` | id, region, role, dialect, credential_ref, primary_of, tags, capacity hints, updated_at |
| `placement.tenants` | tenant_id, home, version, state, moving_to, residency (allowed regions), updated_at |
| `placement.moves` | move id, tenant_id, from, to, state, started/updated, error |

- Every node caches the whole catalog (thousands of rows: small) keyed by tenant `TenancyPath`; refresh on Cluster notice
  `placement.changed` and every 60 s; **version** per row so a node can tell a stale entry.
- On-prem the **static catalog** answers every tenant with the one database; no table, no notice, no cost.
- Installed through YFex.Data `SchemaInstaller` (module `placement`).

## 3. Router (⚑DB4)

1. Tenant = `scope.Current.Root` (`TenancyPath`, depth 1), or a system scope → the global database.
2. Catalog lookup → `TenantPlacement`. Missing → `TenantNotPlacedException` (new tenants are placed explicitly, §4).
3. State `Paused` (tenant maintenance, last step of a move or restore) → `TenantPausedException` with retry-after; Web maps it to 503.
4. Home region (core `ITenantLocations`, from the tenant registry) ≠ `ITenantLocations.CurrentRegion` → `TenantInOtherRegionException`.
5. Freshness: `Write` → primary; a read with ambient `ReadFreshness.Replica(maxLag)` (set by Cqrs from the Aggregate
   Configuration, ⚑DB29) or `ITenantDatabase.Read(maxLag)` → a replica with lag ≤ maxLag, else primary (05 §2); otherwise primary.
6. Return the cached `YFexConnectionFactory` for that database (one per database, created on first use, credentials resolved
   through the credential reference, rotated without restart).

**Fencing writes against moves:** a write connection opened under placement version *v* sets a session value
(`yfex.placement_version`); after the switch the source database's tenant rows are made read-only (row-security policy flag), so a
node with a stale cache fails loudly instead of writing to the old home.

## 4. New tenants (⚑DB7)

Tenants are **registered by YFex.Tenancy.Registry** ([tenancy 05](../tenancy/05-lifecycle-and-provisioning.md)): the registry chooses
the **home region** (⚑TN5) and runs Placement's provisioning step `data.placement`, which calls
`IPlacementAdmin.PlaceAsync(tenant, region)`. Placement asks the app's `IPlacementPolicy` for a home among `Primary` databases **of that
region** (default policy: least tenants / most free capacity tag); the operator can pin. Region moves run Placement's `MoveAsync` step
(cross-region tenant move, 05 §1). Capacity metrics (`yfex.data.placement.tenants` per database) tell the operator when to add
databases; whether a **new region** is needed is the registry's `NeedsRegion` (⚑TN7).

## 5. Data residency (⚑DB8)

- `ResidencyRule` per tenant is **owned by the tenant registry** (⚑TN6) and read through core `ITenantLocations`. Default empty = no
  restriction (on-prem has one region).
- **Enforced** by the catalog on place, move, replica choice (a replica in a disallowed region is never used for that tenant), and by
  YFex.Backup for destinations of that tenant's sets and of whole-database sets containing it (destination carries a region tag).
- Residency is a contract/LGPD matter: LGPD art. 33 allows international transfer only under listed bases; the app records the basis,
  this plan only **prevents accidental** transfers. Logs and metrics never carry tenant data across regions beyond ids (08 §3).

## 6. Files per region (⚑DB27)

YFex.Files (request FI2, 09 §2) maps one **logical store name** to one bucket/container **per region**, plus a tenant prefix:

```csharp
// (sketch)
services.AddYFexFiles(f => f.Store("documents", s => s
    .PerRegion("br-south", b => b.S3("yfex-docs-br", region: "sa-east-1"))
    .PerRegion("eu-west",  b => b.S3("yfex-docs-eu", region: "eu-west-1"))
    .PrefixByTenant()));                                     // /farmaviva/... inside the bucket

await files.Store("documents").WriteAsync("nfe/2026/11/123.xml", stream, ct);   // app code names no region
```

- The region comes from `ITenantLocations` (current tenant's home); residency is checked there, so an EU-only tenant never resolves to a
  Brazilian bucket.
- Files contributes the `files.prefix` provisioning step (create the prefix, delete it at deletion, copy it on a region move).
- Backup stores its own sets through the same mechanism (a `backups` store per region, plus an off-site region allowed by residency).
