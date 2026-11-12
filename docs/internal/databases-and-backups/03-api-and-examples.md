# 03 — API and examples (dev side)

All code is `// (sketch)`. Tenants are always `TenancyPath` (⚑DB30); registration, home region and residency are the
[tenant registry](../tenancy/03-api-and-examples.md)'s.

## 1. Composition roots per place

```csharp
// (sketch) on-premises central: one region, one database, nothing changes for app code
services.AddYFexTenancy(...)
        .AddTenantRegistry(r => r.SingleRegion("on-prem").Tenant(TenancyPath.Parse("farmaviva")));
services.AddYFexData(...)
        .AddPlacement(p => p.Static("main", dialect: "postgres", credentialRef: "db:main"));   // every tenant → "main"

// (sketch) vendor cloud, a node in br-south
services.AddYFexTenancy(...)
        .AddTenantRegistry(r => r.Sql(catalogCredentialRef: "db:catalog").CurrentRegion("br-south"));
services.AddYFexData(...)
        .AddPlacement(p => p
            .Sql(catalogCredentialRef: "db:catalog")          // placement.tenants / placement.databases, same global database
            .Policy<FillLeastUsedPolicy>()                    // IPlacementPolicy: database INSIDE the home region
            .Replicas(defaultRead: ReadFreshness.Primary, maxLagDefault: TimeSpan.FromSeconds(2)))
        .AddPlacementCluster();                               // bridge: notices + move worker
```

`AddPlacement` registers the `data.placement` provisioning step, so a new tenant gets its database when the registry provisions it.
A request for a tenant whose home is **in another region** fails fast with `TenantInOtherRegionException(region)`; the edge
(balancer/DNS, tenant subdomain) should have sent it to that region's cluster.

## 2. App code: nothing to pass (⚑DB4)

```csharp
// (sketch) 1. Commands and most handlers: unchanged — the injected factory is already the current tenant's database
public sealed class CreateSaleHandler(YFexConnectionFactory db) : ICommandHandler<CreateSaleCommand>
{
    public async ValueTask HandleAsync(CreateSaleCommand c, CancellationToken ct)
    {
        await using var conn = db.Create();                 // primary of farmaviva's home database, RowSecurity applied
        await conn.InsertSaleAsync(c.ToRow(), ct);
    }
}

// (sketch) 2. An explicit choice inside one method: ITenantDatabase, still no scope or router
public sealed class ClosingReport(ITenantDatabase db)
{
    public async Task<Report> BuildAsync(DateOnly day, CancellationToken ct)
    {
        await using var read = db.Read(maxLag: TimeSpan.FromSeconds(30)).Create();   // replica if fresh enough
        return await read.QueryClosingAsync(day, ct);
    }
}

// (sketch) 3. Infrastructure code working on ANOTHER tenant (backup, move, registry): the router, with a TenancyPath
var routed = await router.RouteAsync(tenant, DataIntent.Write, ct: ct);
```

## 3. Replica reads in the Aggregate Configuration (⚑DB29)

```csharp
// (sketch) YFex.Cqrs AggregateConfigurationBuilder — the existing place for permissions, validation, cache
public sealed class SaleConfig : IAggregateConfiguration<SaleAggregate>
{
    public void Configure(AggregateConfigurationBuilder<SaleAggregate> b)
    {
        b.Query<SalesSummaryQuery, SalesSummary>()
            .RequirePermission(SalesPermissions.View)
            .Cacheable()                                         // existing cache options (StaleAfter is about the CACHE)
            .ReadFromReplica(maxLag: TimeSpan.FromSeconds(5));   // new: about the DATABASE

        b.Query<StockForSaleQuery, int>()
            .ReadFromPrimary();                                  // stock before a sale: always fresh

        b.Command<CreateSaleCommand>();                          // commands: always primary, no option
    }
}
```

The Cqrs pipeline sets the ambient core `ReadFreshness` (+ max lag) while the query runs; the scoped `YFexConnectionFactory`
from §2 reads it — the handler stays the same and Cqrs never references Data. Queries without the option use the Placement default.
A `[ReadFromReplica]` attribute with a generator may come later as a shortcut.

## 4. Backup policies (database + files, ⚑DB28)

```csharp
// (sketch) on-premises central with SQL Server
services.AddYFexBackup(b => b
    .Destination(new LocalFolderDestination(@"D:\YFexBackups"))
    .Destination("offsite", files => files.S3("customer-bucket"))          // YFex.Backup.Files
    .Policy("nightly", p => p
        .FullDatabase("main").WithFiles("documents")                        // one set: database first, then files
        .Cron("0 2 * * *")
        .To("local", "offsite")
        .Retain(daily: 14, weekly: 8, monthly: 12, yearly: 5)
        .SealTo("backup")
        .Verify(drill: "weekly")))
  .AddBackupJobs();                                                         // [Singleton] jobs (⚑DB22)

// (sketch) vendor cloud: whole database and buckets are the platform's; we record, drill, and export tenants
services.AddYFexBackup(b => b
    .Destination("archive", files => files.Store("backups"))               // regional store: bucket of THIS region (⚑DB27)
    .Policy("platform-pitr", p => p.PlatformOwned("br-sp-*").ExpectFreshWithin(TimeSpan.FromHours(26)).Verify(drill: "weekly"))
    .Policy("platform-files", p => p.PlatformOwnedFiles("documents").ExpectVersioning().ExpectReplicationTo("br-rio"))
    .Policy("tenant-weekly", p => p.TenantExports(TenantSelection.All).WithFiles("documents").Cron("0 4 * * 0")
        .To("archive").Retain(weekly: 8, monthly: 6).SealToTenantRing("tenant-backup")));
```

## 5. Restore one tenant (cloud, "as at yesterday 14:32")

```csharp
// (sketch) admin command handler; permission backup.restore + inline approval
TenancyPath tenant = command.Tenant;                                         // chosen in the admin UI from the registry, never typed
var scratch = await restores.PointInTimeToScratchAsync(database: "br-sp-03", at: command.At, ct);   // platform PITR → scratch db
var export  = await backup.ExportTenantAsync(scratch, tenant, ct);           // rows from scratch + files as at that time (versions)
await using (await cluster.Maintenance.PauseScopeAsync(tenant, reason: "restore", ct))             // ⚑CL21
{
    await backup.RestoreTenantAsync(export, RestoreTarget.InPlaceTenant, ct); // safety copy → delete → import rows + files
}                                                                             // → erasure ledger → restore epoch
await restores.DropScratchAsync(scratch, ct);
```

## 6. Owner declaring tenant data (request to owners)

```csharp
// (sketch) Notifications declares its outbox for exports/moves — in its module configuration, like Aggregate Configuration
backup.TenantData("notifications.outbox", t => t
    .Table("notifications", "outbox").TenantColumn("tenant_path")
    .Order(50).OnRestore(RestoreRule.Review));
```

`OnRestore` tells the restore what to do with rows that represent **side effects in flight** (outbox, Jobs runs): drop, keep, or mark
for review — never resend blindly (06 §4). A `[TenantDataSet]` attribute + generator may come later as a shortcut.

## 7. Tests

```csharp
// (sketch)
await using var h = new BackupHarness();                 // in-memory destination, fake clock, SQLite source, in-memory file store
var set = await h.RunPolicyAsync("nightly");
h.CorruptByte(set, part: 0, offset: 1234);
(await h.VerifyAsync(set)).ShouldBe(VerifyResult.ChecksumMismatch);
```

## 8. Cons of this API

- One more indirection on every connection (a dictionary lookup on a cached catalog; measured in the benchmarks phase).
- Apps with hand-written cross-tenant reports must route per tenant and merge (no cross-database joins).
- Tenant exports are as complete as owners' declarations: a forgotten table is a silent gap → the drill compares row counts of every
  table with a tenant column against the source (06 §5).
- The ambient `ReadFreshness` is invisible in the handler; the Aggregate Configuration is the one place to look (and the admin
  diagnostics list each query's setting).
