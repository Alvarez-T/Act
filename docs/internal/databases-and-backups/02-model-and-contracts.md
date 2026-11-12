# 02 — Model and contracts

All names are proposals. `// (sketch)` code is illustration.

## 1. Placement (`YFex.Data.Placement`)

```csharp
// (sketch)
public readonly record struct DatabaseId(string Value);          // "br-sp-03"; app/operator names
// RegionId, ResidencyRule, TenantLocation, ITenantLocations, ReadFreshness: core (tenancy 02 §1); not redefined here

public enum DatabaseRole : byte { Primary, Replica, Standby, Retired }   // library owns the full set

public sealed record DatabaseEntry(
    DatabaseId Id, RegionId Region, DatabaseRole Role,
    string Dialect,                 // "postgres", "sqlserver", "sqlite"
    string CredentialRef,           // a reference, never a secret (⚑DB5)
    DatabaseId? PrimaryOf,          // for replicas
    ImmutableArray<string> Tags);   // "dedicated", "capacity:high"

public enum PlacementState : byte { Active, Moving, Paused, Retired }

public sealed record TenantPlacement(
    TenancyPath Tenant, DatabaseId Home, long Version, PlacementState State,
    DatabaseId? MovingTo);                    // region + residency: ITenantLocations (tenant registry)

public enum DataIntent : byte { Write, Read, ReadStale }

public interface IPlacementCatalog
{
    ValueTask<TenantPlacement?> FindAsync(TenancyPath tenant, CancellationToken ct = default);
    long Version { get; }                                   // whole-catalog version (cache)
    DatabaseEntry Database(DatabaseId id);
}

public interface IDatabaseRouter
{
    // hot path: cached lookup, no I/O when the catalog is warm
    ValueTask<RoutedConnection> RouteAsync(TenancyPath scope, DataIntent intent,
        TimeSpan maxLag = default, CancellationToken ct = default);
}

public readonly record struct RoutedConnection(YFexConnectionFactory Factory, DatabaseId Database, long PlacementVersion);

public interface ITenantDatabase                            // app code: the CURRENT tenant, no scope passing (⚑DB4)
{
    YFexConnectionFactory Write();                          // primary
    YFexConnectionFactory Read(TimeSpan maxLag = default);  // replica if lag ≤ maxLag, else primary; default = primary
}
// The scoped YFexConnectionFactory registered by AddPlacement() routes to the current tenant and honours the ambient
// core ReadFreshness that the Cqrs pipeline sets from the Aggregate Configuration (⚑DB29).

public interface IPlacementPolicy                           // app-supplied (⚑DB7)
{
    DatabaseId ChooseHome(TenancyPath tenant, RegionId region, IReadOnlyList<DatabaseEntry> candidates);   // inside the home region
}
```

Engines: `StaticPlacementCatalog` (configuration; one or a few databases; on-prem default) and `SqlPlacementCatalog` (table
`placement.tenants` + `placement.databases` in the global catalog database). Admin writes go through `IPlacementAdmin`
(`PlaceAsync`, `StartMoveAsync`, `SetRoleAsync`) — commands with permissions (⚑DB25).

### 1.1 Move states (⚑DB9)

`Planned → Copying → CatchingUp → Paused (tenant maintenance) → Switched → Verifying → Done → SourceRetired`, or `Failed → RolledBack`
from any state before `Switched`. Each state is a row in `placement.moves` with timestamps; steps run as a Cluster single worker
keyed `placement.move:<tenant>` (bridge package), so a crashed node's move resumes on another.

## 2. Backup (`YFex.Backup`)

```csharp
// (sketch)
public readonly record struct BackupSetId(Guid Value);           // v7

public enum BackupKind : byte { FullDatabase, Incremental, TenantExport, Files, PlatformOwned }

public sealed record BackupScope(DatabaseId? Database, TenancyPath Tenant, bool IncludeFiles = true);
// Tenant = TenancyPath.None → whole database; otherwise one tenant (depth 1). IncludeFiles → the tenant's file prefixes (⚑DB28)

public sealed record BackupPolicy(
    string Name, BackupScope Scope, BackupKind Kind,
    string Schedule,                                  // cron, handed to Jobs
    ImmutableArray<string> Destinations,              // ≥ 2 in production (⚑DB17)
    RetentionRule Retention,
    string RecipientRing,                             // Cryptography ring name (⚑DB18)
    VerificationRule Verification);

public sealed record RetentionRule(int Daily, int Weekly, int Monthly, int Yearly);   // GFS (⚑DB20)

public enum BackupSetState : byte { Running, Written, Checked, Verified, Failed, Expired, Held }

public sealed record BackupManifest(              // travels with the set, signed (⚑DB23)
    int FormatVersion, BackupSetId Id, BackupKind Kind, BackupScope Scope,
    DateTimeOffset TakenAt, DateTimeOffset? ConsistentAt,   // ConsistentAt: the moment the data represents
    string SchemaVersions,                                  // SchemaInstaller module versions
    ImmutableArray<BackupPart> Parts, KeyId SealedTo, long ErasureLedgerPosition);

public sealed record BackupPart(string Name, long Length, Hash256 Sha256);

public interface IBackupSource          // YFex.Backup.Data implements per dialect / kind
{
    BackupKind Kind { get; }
    IAsyncEnumerable<BackupStream> ReadAsync(BackupScope scope, CancellationToken ct);
    ValueTask RestoreAsync(RestorePlan plan, IBackupReader reader, CancellationToken ct);
}

public interface IBackupDestination      // local folder built-in; Files bridge for the rest
{
    string Name { get; }
    ValueTask WriteAsync(BackupSetId set, string part, Stream content, CancellationToken ct);
    ValueTask<Stream> OpenAsync(BackupSetId set, string part, CancellationToken ct);
    ValueTask DeleteAsync(BackupSetId set, CancellationToken ct);  // refused while Held / immutable
    bool SupportsImmutability { get; }
}

public interface IBackupCatalog
{
    ValueTask RecordAsync(BackupManifest manifest, BackupSetState state, CancellationToken ct);
    IAsyncEnumerable<BackupSetSummary> ListAsync(BackupScope scope, CancellationToken ct);
}

public sealed record RestorePlan(BackupSetId Set, RestoreTarget Target, DateTimeOffset? PointInTime);
public enum RestoreTarget : byte { ScratchDatabase, NewDatabase, InPlaceTenant, InPlaceDatabase }
```

`ITenantDataSet` (per owner, in `YFex.Backup` contracts or `YFex.Data.Placement` — ⚑ see 09 §2) declares a tenant's tables that the
row-security catalog does not cover, with the key column and the order of import (parents first).

### 2.1 Format of a tenant export

Versioned container (format tag + golden test, rule "stored formats"): header (manifest), then per table: column list, rows in the
YFex.Data binary row encoding (or CSV-like for portability, ⚑ open in 06 §2), chunked, each chunk checksummed; the whole stream
compressed with the BCL (`BrotliStream`; no third-party Zstd), then sealed (AES-GCM chunks with the data key).
