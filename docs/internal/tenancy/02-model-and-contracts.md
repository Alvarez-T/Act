# 02 — Model and contracts

All names are proposals; `// (sketch)`.

## 1. Core additions (⚑TN2, ⚑TN3)

```csharp
// (sketch) YFex/src/YFex/Tenancy/
public readonly partial record struct TenancyPath
{
    /// The tenant part (first level) of this path; None for None.
    public TenancyPath Root { get; }
    public bool IsTenant => Depth == 1;
}

public readonly record struct RegionId : ICanonicalValue<RegionId>, ILogValue<RegionId>, ISpanParsable<RegionId>
{   // lower-case letters, digits, '-'; ≤ 32 bytes; names declared by the operator (no enum: rules/infrastructure)
}

public sealed record ResidencyRule(ImmutableArray<string> AllowedPatterns)   // "br-*", "eu-west"; empty = any
{
    public static ResidencyRule Any { get; }
    public bool Allows(RegionId region);
}

public readonly record struct TenantLocation(TenancyPath Tenant, RegionId Home, ResidencyRule Residency, long Version, bool Active);

public interface ITenantLocations                 // read-only; Data, Files, Backup, Cluster depend on this only
{
    bool TryGet(TenancyPath tenant, out TenantLocation location);           // hot path: cached, no I/O
    ValueTask<TenantLocation?> GetAsync(TenancyPath tenant, CancellationToken ct = default);
    RegionId CurrentRegion { get; }                // the region this process serves
}

public enum ReadFreshness : byte { Default, Primary, Replica }              // + MaxLag carried beside it (databases-and-backups ⚑DB29)
```

Methods that need a tenant take `TenancyPath` and check `IsTenant` (or use `.Root`).

## 2. Registry (`YFex.Tenancy.Registry`)

```csharp
// (sketch)
public enum TenantState : byte { Registering, NeedsRegion, Provisioning, Active, Suspended, Moving, Offboarding, Deleted, Failed }

public sealed record TenantRecord(
    TenancyPath Tenant, string DisplayName, TenantState State,
    RegionId? Home, ResidencyRule Residency, long Version,
    DateTimeOffset CreatedAt, string? FailedStep, string? StateReason);

public enum RegionUse : byte { Home, Standby, BackupOnly, Closed }

public sealed record RegionRecord(RegionId Id, string DisplayName, RegionUse Use, bool OpenForNewTenants,
    string? Jurisdiction /* "BR", "EU" — operator text */, ImmutableArray<string> Tags);

public sealed record TenantRegistration(TenancyPath Tenant, string DisplayName, ResidencyRule Residency,
    RegionId? PinnedRegion = null, ImmutableDictionary<string, string>? StepInputs = null);  // e.g. product, admin e-mail

public interface ITenantRegistry
{
    ValueTask<TenantRecord> RegisterAsync(TenantRegistration registration, CancellationToken ct = default);
    ValueTask<TenantRecord?> FindAsync(TenancyPath tenant, CancellationToken ct = default);
    IAsyncEnumerable<TenantRecord> ListAsync(TenantQuery query, CancellationToken ct = default);   // admin, owners' scope lists
    ValueTask SuspendAsync(TenancyPath tenant, string reason, CancellationToken ct = default);
    ValueTask ResumeAsync(TenancyPath tenant, CancellationToken ct = default);
    ValueTask MoveRegionAsync(TenancyPath tenant, RegionId to, CancellationToken ct = default);
    ValueTask OffboardAsync(TenancyPath tenant, CancellationToken ct = default);
    ValueTask DeleteAsync(TenancyPath tenant, CancellationToken ct = default);
}

public interface IRegionCatalog
{
    ImmutableArray<RegionRecord> Regions { get; }
    ValueTask RecordAsync(RegionRecord region, CancellationToken ct = default);   // after the platform built it
}

public interface IRegionPolicy                    // app-supplied (⚑TN5)
{
    RegionId? Choose(TenantRegistration registration, ImmutableArray<RegionRecord> candidates);   // null → NeedsRegion
}
```

## 3. Provisioning step (⚑TN10)

```csharp
// (sketch) implemented by libraries (or their bridges), registered in DI
public interface ITenantProvisioningStep
{
    string Name { get; }                  // "data.placement", "files.prefix", "crypto.ring", "licensing.license", "auth.admin"
    int Order { get; }                    // registry runs ascending; deprovision descending
    ValueTask ProvisionAsync(TenantProvisioningContext context, CancellationToken ct);     // idempotent
    ValueTask DeprovisionAsync(TenantProvisioningContext context, CancellationToken ct);   // idempotent
    ValueTask MoveAsync(TenantProvisioningContext context, RegionId from, RegionId to, CancellationToken ct)
        => throw new NotSupportedException();     // libraries with regional data override it
}

public sealed record TenantProvisioningContext(TenantRecord Tenant, RegionId Region,
    ImmutableDictionary<string, string> Inputs, IProvisioningState State);   // State: per-step durable bag (resume)
```

Progress table `tenancy.provisioning` (tenant, step, phase, status, attempts, last error, updated_at). Retries through YFex.Resilience;
a step failing permanently leaves the tenant `Failed` with the step name; an operator command retries or rolls back (deprovision of the
steps already done).

## 4. Cache and the `ITenantStore` adapter (⚑TN11, ⚑TN17)

- `ITenantLocations` implementation keeps a frozen dictionary of all tenants' locations (thousands of rows); swapped atomically on
  refresh; 0 allocations per lookup.
- `RegistryTenantStore<TTenant>(ITenantRegistry, IAppTenantLoader<TTenant>)` → `ITenantStore<TTenant>`: returns `null` for
  `Deleted`, a tenant marked suspended for `Suspended` (the resolver refuses with `TenantSuspendedException`).
