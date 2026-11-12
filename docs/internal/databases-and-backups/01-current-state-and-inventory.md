# 01 — Current state and inventory

Checked against source on 2026-11-11 (grep over `src/` of each repo, `obj/` and tests excluded). Paths are relative to `C:\Dev\YFex`.

## 1. Summary

**Nothing in YFex chooses a database per tenant, and nothing backs up or restores.** A search for `Backup`, `Restore`, `Replica`,
`Failover`, `Shard`, `Placement` type names over Data, Tenancy, Persistence, Offline, Installations, Jobs, Audit, Privacy, Cryptography
and core found **no matches** (the only `Snapshot` types are schema/state snapshots, unrelated). Connections are built by the app with
one connection factory per database; row security isolates tenants inside one database.

## 2. What exists and what this plan builds on

| Library | Symbol | Where | Use in this plan |
|---|---|---|---|
| YFex.Data | `YFexConnectionFactory` (caller-supplied ADO.NET factory + dialect + optional `RowSecurity`) | `YFex.Data/src/YFex.Data/Connection/YFexConnectionFactory.cs:10`, `RowSecurity` property `:30`, `Create()` `:35` | the router returns one of these per database (⚑DB4); no change needed to the type |
| YFex.Data | `YFexConnection` | `YFex.Data/src/YFex.Data/Connection/YFexConnection.cs:15` | sources/imports run on it |
| YFex.Data | `IDataSource` (connect, introspect, sample, extraction cursor) | `YFex.Data/src/YFex.Data/Providers/IDataSource.cs:9` | **not** used for backup: a mining/extraction boundary, not transactional; the tenant export uses `YFexConnection` directly |
| YFex.Data | row security: `IRowSecured` `Security/IRowSecured.cs:11`, `RowSecurityPolicy` `Security/RowSecurityPolicy.cs:35`, `RowSecurityKey` `:9`, `RowSecurityCatalog` `Security/RowSecurityCatalog.cs:10` (`Default` `:13`), `RowSecuritySession.ApplyAsync` `Security/RowSecuritySession.cs:20` | `YFex.Data/src/YFex.Data/` | the **tenant export** finds a tenant's tables and key columns through the row-security catalog (⚑DB14) |
| YFex.Data | `SchemaInstaller` (`ApplySchemaAsync` `:77`, `GetSchemaStatusAsync` `:130`), migration lock `SchemaSqlGenerator.AcquireMigrationLock` used at `SchemaInstaller.cs:513`, `SchemaMigrationLockScope` `Schema/ISchemaDialect.cs:37` | `YFex.Data/src/YFex.Data/Schema/` | catalog tables (`placement.*`, `backup.*`) installed through it; a restore checks the **schema version** of the set against the target (06 §4) |
| YFex.Tenancy | `ITenantStore<TTenant>` (`TryGetAsync`, `TryGetByIdAsync`; no enumeration) | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantStore.cs:9` | placement is keyed by the tenant **id**; Tenancy is not changed |
| YFex.Tenancy | `ITenantInfo` (`Id` `:14`, `Identifier` `:21`, `Name` `:24`; **no connection string**) | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantInfo.cs:11` | confirms routing is not in Tenancy today |
| YFex.Tenancy | `ScopeIdentity` | `YFex.Tenancy/src/YFex.Tenancy/ScopeIdentity.cs:9` | |
| core | `TenancyPath` | `YFex/src/YFex/Tenancy/TenancyPath.cs:31` | router input; tenant = first segment (⚑DB2) |
| YFex.Cryptography | `RecipientKeyRing` (sealing) | `YFex.Cryptography/src/YFex.Cryptography/Sealing/RecipientKeyRing.cs:30` | seal each backup set's data key (⚑DB18) |
| YFex.Cryptography | `ScopedKeyRings` (`GetAsync(family, scope)` `:129`, `CreateDedicatedRingAsync` `:165`) | `YFex.Cryptography/src/YFex.Cryptography/Keys/ScopedKeyRings.cs:95` | per-tenant key ring for tenant exports |
| YFex.Cryptography | `KeyRing` | `YFex.Cryptography/src/YFex.Cryptography/Keys/KeyRing.cs:32` | |
| YFex.Jobs | `JobAttribute` `:16`, `CronAttribute` `:58` | `YFex.Jobs/src/YFex.Jobs.Abstractions/Attributes/JobAttributes.cs` | backup policies become jobs (⚑DB22) |
| YFex.Jobs | `IJobStore` `:20`, `TryAcquireLeaseAsync` `:47` (no cross-server implementation — Cluster finding F2) | `YFex.Jobs/src/YFex.Jobs.Abstractions/Store/IJobStore.cs` | why backups wait for `[Singleton]` on Cluster leases |
| YFex.Audit | `ChangeWriter.Erased` `:152`, `AuditValueEncoding.WriteErased` `:53` | `YFex.Audit/src/YFex.Audit.Public/` | erasure marker; crypto-shredding design in [audit 08 §3](../audit/08-privacy-retention-and-access.md) (⚑A22) |
| YFex.Offline | central stores: `IOfflineReconciliationStore` `Reconciliation/OfflineFindings.cs:192`, `ISiteSettingsStore` `Sites.cs:45`, `IOfflinePublicationStore` `OfflinePublications.cs:43`, `IDeliveryReceiptStore` `DeliveryReceipts.cs:59` | `YFex.Offline/src/YFex.Offline.Central/` | central-side tables are backed up as part of the central database; site broker upload path (⚑DB24) is new |
| YFex.Files | design only (`IFileStore`, capabilities) — no code | [files README](../files/README.md) | `YFex.Backup.Files` waits for Files F-P1 |
| YFex.Cqrs | `AggregateConfigurationBuilder<TAggregate>` (`Query<TQuery,TResult>()` `:19`, `Command<TCommand>()` `:29`) | `YFex.Cqrs/src/YFex.Cqrs/Configuration/AggregateConfigurationBuilder.cs:9` | where queries declare replica reads (⚑DB29) |
| YFex.Cqrs | `QueryConfigurationBuilder.StaleAfter(TimeSpan)` (cache staleness), `Cacheable()` `:168`, `ScopedByTenant()` `:202` | `YFex.Cqrs/src/YFex.Cqrs/Configuration/QueryConfigurationBuilder.cs:189` | **name taken** by the cache: the replica option is `ReadFromReplica`, not `Stale…` |
| core | `NodeIdentity` `Region` (`string?`) | `YFex/src/YFex/Diagnostics/NodeIdentity.cs:38` | aligns with core `RegionId` ([tenancy 01](../tenancy/01-current-state-and-inventory.md)) |
| (none) | **no `TenantId` type** in core, Tenancy or Actors; tenants are strings or `TenancyPath` | — | tenant key = `TenancyPath` (⚑DB30) |
| (none) | **no tenant registration** anywhere | — | [YFex.Tenancy.Registry](../tenancy/README.md) |
| YFex.Cluster | design only (leases, maintenance ⚑CL21, notices) — no code | [cluster README](../cluster/README.md) | moves, one-tenant restore, catalog notices |
| YFex.Hosting | design only | [hosting README](../hosting/README.md) | on-prem and site hosts run backup jobs |

## 3. Findings while checking

| # | Finding | Effect | Owner / where handled |
|---|---|---|---|
| F1 | No routing seam: each app builds `YFexConnectionFactory` by hand from a connection string | adding a second database means editing every composition root | ⚑DB4 router |
| F2 | Not every tenant-owned table is necessarily row-secured (lookup tables, global catalogs, Jobs/Audit/Notifications tables keyed by scope columns of their own) | a tenant export from the row-security catalog alone would miss tables | `ITenantDataSet` declarations per owner ([09 §2](09-phases-and-acceptance.md)) |
| F3 | Owners keep scope in different shapes (`TenancyPath` text, tenant id string, `ScopeIdentity`) | export/delete/move must know each owner's key | each owner declares its tenant key column (requests) |
| F4 | No owner records "this data was erased" outside Audit | a restore resurrects erased data | erasure ledger (⚑DB21), Privacy request PR1 |
| F5 | Fencing and sequences assume time moves forward: restoring a database rewinds Cluster lease tokens, Audit chain heads, Jobs next-fire, outbox states | duplicated sends, broken chains, reused tokens after a restore | **restore epoch** (06 §4) + requests to Cluster/Audit/Jobs/Notifications |
