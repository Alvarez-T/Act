# 03 — API and examples (dev side)

All code is `// (sketch)`.

## 1. Composition per place

```csharp
// (sketch) on-premises central: one region, one tenant, from the installation
services.AddYFexTenancy(...)
        .AddTenantRegistry(r => r.SingleRegion("on-prem").Tenant(TenancyPath.Parse("farmaviva")));

// (sketch) vendor cloud, a node in br-south
services.AddYFexTenancy(...)
        .AddTenantRegistry(r => r
            .Sql(catalogCredentialRef: "db:catalog")      // YFex.Tenancy.Registry.Data, global catalog database
            .CurrentRegion("br-south")
            .RegionPolicy<CountryRegionPolicy>()           // app's IRegionPolicy
            .UseOrchestration());                          // provisioning as a process manager (optional)
```

Libraries add their steps with their own `Add…` (e.g. `AddPlacement(...)` registers `data.placement`; `AddYFexFiles(...)` registers
`files.prefix`); the app adds its own (`app.customer`).

## 2. Registering a tenant (vendor sign-up or sales back-office)

```csharp
// (sketch) command handler; permission tenancy.register
var record = await registry.RegisterAsync(new TenantRegistration(
    Tenant: TenancyPath.Parse("farmaviva"),
    DisplayName: "FarmaViva Ltda",
    Residency: new ResidencyRule(["br-*"]),
    StepInputs: ImmutableDictionary<string, string>.Empty
        .Add("licensing.product", "yfex-farma")
        .Add("auth.admin-email", command.AdminEmail)), ct);

// record.State: Provisioning → (runner) → Active
// or NeedsRegion when no open region matches "br-*": the operator sees it in the admin list
```

Steps run in order (sketch):

| Order | Step | Owner | Does |
|---|---|---|---|
| 10 | `tenancy.record` | Registry | record + home region chosen |
| 20 | `crypto.ring` | Cryptography | tenant key ring (backups, crypto-shredding) |
| 30 | `data.placement` | Data.Placement | home database chosen in the region; tenant schema rows |
| 40 | `files.prefix` | Files | `/farmaviva/` in the region's stores |
| 50 | `backup.policies` | Backup | region's default policies for the tenant |
| 60 | `licensing.license` | Licensing (vendor) | license issued |
| 70 | `auth.admin` | Authentication | first admin account + invitation |
| 100 | `app.customer` | the app | the app's own tenant table, plan, billing |

## 3. A library's step

```csharp
// (sketch) in YFex.Data.Placement
internal sealed class PlacementProvisioningStep(IPlacementAdmin placement) : ITenantProvisioningStep
{
    public string Name => "data.placement";
    public int Order => 30;

    public async ValueTask ProvisionAsync(TenantProvisioningContext c, CancellationToken ct)
    {
        if (await placement.FindAsync(c.Tenant.Tenant, ct) is not null) return;   // idempotent
        await placement.PlaceAsync(c.Tenant.Tenant, c.Region, ct);
    }

    public ValueTask DeprovisionAsync(TenantProvisioningContext c, CancellationToken ct)
        => placement.RemoveAsync(c.Tenant.Tenant, ct);

    public ValueTask MoveAsync(TenantProvisioningContext c, RegionId from, RegionId to, CancellationToken ct)
        => placement.MoveToRegionAsync(c.Tenant.Tenant, to, ct);
}
```

## 4. Reading where a tenant lives (any library)

```csharp
// (sketch) Files picking the bucket for the current tenant
if (!locations.TryGet(scope.Current.Root, out var where))
    throw new TenantNotRegisteredException(scope.Current.Root);
var store = regionalStores.For("documents", where.Home);        // bucket of that region
```

## 5. Tests

```csharp
// (sketch)
var reg = new InMemoryTenantRegistry(regions: [Region("br-south"), Region("eu-west")]);
reg.AddStep(new FailingStep("files.prefix", failTimes: 2));
var r = await reg.RegisterAsync(new(TenancyPath.Parse("t1"), "T1", new(["br-*"])));
await reg.RunPendingAsync();                                    // retries; resumes from step 40
(await reg.FindAsync(r.Tenant))!.State.ShouldBe(TenantState.Active);
```
