# 07 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules

- **No code before the user confirms ⚑TN1–⚑TN18.** Re-read current source of every library a phase touches.
- `YFex.Tenancy` and `YFex` (core) are separate Git roots; commit on `main` only when the user asks. Back up files of other repos to
  `C:\Temp\tenancy-backup\<phase>\` before touching them.
- Core additions follow the core rule (types only) and the stored-format rule: `RegionId` canonical bytes get a golden test.
- Build `--artifacts-path C:/Temp/tenancy-artifacts`; tests on Windows and Linux (`linuxKit`); AOT smoke 0 IL warnings.
- Keep a memory file (`yfex-tenancy-registry.md`) current.

## 2. Requests to other libraries

| # | Owner | Request | When |
|---|---|---|---|
| CO1 | YFex core | `TenancyPath.Root`, `IsTenant`; `RegionId`; `ResidencyRule`; `TenantLocation`; `ITenantLocations`; `ReadFreshness` | TN-P1 |
| CO2 | YFex core / Logging | `NodeIdentity.Region` filled from the node's `RegionId` text (keep the string parameter) | TN-P3 |
| DA1 | YFex.Data (Placement) | `data.placement` step with `MoveAsync`; read the home region from `ITenantLocations` | TN-P2 / databases DB-P5 |
| FI1 | YFex.Files | one logical store → one bucket **per region**; `files.prefix` step with `MoveAsync` | TN-P3 |
| CR1 | YFex.Cryptography | `crypto.ring` step: create the tenant ring; destroy it at deletion | TN-P2 / TN-P4 |
| BK1 | YFex.Backup | `backup.policies` step; destinations checked against residency | TN-P3 |
| LC1 | YFex.Licensing | `licensing.license` step in the vendor service | TN-P2 |
| AN1 | YFex.Authentication | `auth.admin` step (first admin + invitation); revoke all sessions on `Suspended`/`Offboarding` | TN-P2 / TN-P4 |
| CL1 | YFex.Cluster | `TenantRegistryChanged` notice; stop tenant workers when not `Active`; refuse tenants of other regions | TN-P3 |
| JO1 | YFex.Jobs | pause tenant-scoped jobs when not `Active` | TN-P4 |
| OR1 | YFex.Orchestration | none required; the runner uses `ProcessManager<TState>` when present | TN-P2 |
| WE1 | YFex.Web | map `TenantSuspended` / `TenantNotReady` / `TenantInOtherRegion` to HTTP answers | TN-P3 |
| AU1 | YFex.Audit | events `tenancy.*` | TN-P2 |

## 3. Phases

### TN-P0 — Decisions
User confirms ⚑TN1–⚑TN18; owners acknowledge requests.

### TN-P1 — Core types + single-region mode
- CO1; `YFex.Tenancy.Registry` contracts, single-region registry, `ITenantLocations` implementation, step runner (sequential), testing package.
- **Acceptance:** an on-prem host registers its one tenant from configuration, steps run once and are skipped on the next start;
  `TryGet` allocates 0 B; `RegionId` golden bytes; Windows + Linux; AOT 0 warnings.

### TN-P2 — SQL registry + resumable provisioning
- `YFex.Tenancy.Registry.Data`, `tenancy.provisioning`, retries, rollback, Orchestration engine (optional), permissions, audit events,
  `RegistryTenantStore<TTenant>`.
- **Acceptance:** a step failing twice then succeeding ends `Active`; a process killed during step 40 resumes at 40 on another node;
  rollback deprovisions in reverse order.

### TN-P3 — Regions, residency, `NeedsRegion`
- `tenancy.regions`, `IRegionPolicy`, residency checks, `NeedsRegion` + automatic continuation, notices, Web mapping.
- **Acceptance:** an `eu-*` tenant waits in `NeedsRegion`; recording `eu-west` continues it; Files and Placement refuse a `br-south`
  target for it.

### TN-P4 — Suspension, offboarding, deletion
- **Acceptance:** suspended tenant refused on every node within 60 s (seconds with notices); deletion destroys the ring and a prior
  export can no longer be opened; the key cannot be registered again.

### TN-P5 — Region move
- **Acceptance (with Placement DB-P6 and Files):** a tenant moves `br-south` → `br-south-2` with only short pauses; residency refuses
  a move to `eu-west` for a `br-*` tenant.

## 4. Implementation log

(empty — no phase started)
