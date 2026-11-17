# 01 — Current state and inventory

Checked against source on 2026-11-11. Paths relative to `C:\Dev\YFex`.

## 1. Summary

**No YFex library creates, suspends or deletes tenants, and none knows regions.** YFex.Tenancy has two projects —
`YFex.Tenancy` (scope identity, ambient scope, overrides, isolation) and `YFex.Tenancy.Resolution` (resolving the request's tenant,
looking a tenant up) — and both are read-only with respect to tenants. The repo has only `AGENTS.md`; no design folder existed before
this one.

## 2. What exists

| Symbol | Where | Use in this plan |
|---|---|---|
| `TenancyPath` (core; `Depth` `:54`, `IsWithin` `:71`, `Parse` `:101`, canonical bytes `:201`) | `YFex/src/YFex/Tenancy/TenancyPath.cs:31` | the tenant key (⚑TN2); `Root` to be added |
| `TenancyPathExtensions.ToScopeIdentity` | `YFex.Tenancy/src/YFex.Tenancy/TenancyPathExtensions.cs:7` | conversion stays |
| `ScopeIdentity` | `YFex.Tenancy/src/YFex.Tenancy/ScopeIdentity.cs:9` | unchanged |
| `IScopeContext` | `YFex.Tenancy/src/YFex.Tenancy/IScopeContext.cs:8` | unchanged |
| `ITenantInfo` (`Id` string `:14`, `Identifier` `:21`, `Name` `:24`; no state, no region) | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantInfo.cs:11` | app's tenant type; registry adapter fills state/region (⚑TN17) |
| `ITenantStore<TTenant>` (`TryGetAsync`, `TryGetByIdAsync`; **no create, no list**) | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantStore.cs:9` | unchanged; `RegistryTenantStore<TTenant>` adapter |
| `ITenantResolver<TTenant>` | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantResolver.cs:10` | refuses `Suspended` tenants through the adapter |
| `ITenantScopeProjector.TenantIdOf` (string) | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantScopeProjector.cs:20` | string tenant ids remain at this boundary; registry APIs use `TenancyPath` |
| core `NodeIdentity` `Region` parameter (`string?`, "region or site") | `YFex/src/YFex/Diagnostics/NodeIdentity.cs:38` | should take its value from the node's `RegionId` text (request CO2) |
| Project references: `YFex.Tenancy` → core only; `.Resolution` → `YFex.Tenancy` | `YFex.Tenancy/src/YFex.Tenancy/YFex.Tenancy.csproj:13`, `YFex.Tenancy/src/YFex.Tenancy.Resolution/YFex.Tenancy.Resolution.csproj:13` | the registry adds a package; the base stays light |
| Orchestration `ProcessManager<TState>` | `YFex.Orchestration/src/YFex.Orchestration/ProcessManager/ProcessManager.cs:15` | optional engine for the provisioning runner (⚑TN10) |
| Installations central registry records (`InstallationScopeSnapshot` with `Tenant` string) | `YFex.Installations/src/YFex.Installations.Central/Registry.cs:232` | another place that names tenants as strings |

## 3. Findings

| # | Finding | Effect |
|---|---|---|
| F1 | Tenant ids are strings at every boundary (`ITenantInfo.Id`, `TenantIdOf`, Installations `Tenant`) | a store path or a display name can be passed by mistake; new APIs use `TenancyPath` (⚑TN2) |
| F2 | `TenancyPath` has no "tenant part" accessor | every library would split the text itself |
| F3 | `NodeIdentity.Region` is free text | regions in logs and in placement could disagree |
| F4 | No tenant state anywhere | a suspended or deleted tenant can still be resolved |
