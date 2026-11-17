# YFex.Tenancy.Registry — registering tenants, their lifecycle, home region and residency — Design

> **Status:** DESIGN 2026-11-11. Docs only: no code. **Every decision ⚑TN1–⚑TN18 (§6) waits for the user.**
> **Origin:** 2026-11-11, while designing [databases-and-backups](../databases-and-backups/README.md). The user asked who **registers a
> new tenant**, chooses its **region**, and notices when **one more region** is needed — and whether region code belongs in its own
> library. Checking the source showed that **no YFex library creates tenants**: YFex.Tenancy resolves and looks them up only
> ([01](01-current-state-and-inventory.md)). The user chose to keep regions with the tenant (2026-11-11 discussion), in Tenancy, with
> a plan of their own.
> **Repo:** existing `C:\Dev\YFex\YFex.Tenancy` (new packages beside `YFex.Tenancy` and `YFex.Tenancy.Resolution`) + small additions to
> YFex core.
> **Earlier Tenancy plan:** [web-tenancy-consolidation-plan.md](../web-tenancy-consolidation-plan.md) (2026-09-24) moved scope types from
> Web into Tenancy; it is not changed by this plan.
> **Normative vs sketch:** every name is a **proposal** until the user approves §6; code marked `// (sketch)` is illustration. Names of
> existing code were checked against source on 2026-11-11 ([01](01-current-state-and-inventory.md)).

## 1. What it is, in one paragraph

Before a tenant can sign in, sell or be backed up, someone must **create** it: give it a key, decide **where its data lives**
(home region, allowed regions), create its database rows, file folders, key ring, license and first admin account, and later suspend,
offboard and delete it. Today each app would write that by hand, and nothing tells Data, Files, Backup or Cluster **where a tenant
lives**. **YFex.Tenancy.Registry** is the record of tenants and regions plus the lifecycle; each library contributes its own
**provisioning step**; and YFex core gets the few types every library needs to ask "where does this tenant live?" without depending
on the registry.

## 2. What it is not

| Not | Who does it | Registry's part |
|---|---|---|
| Resolving the tenant of a request (host, header, claim) | YFex.Tenancy.Resolution (`ITenantResolver`, strategies), YFex.Web | none; the app's `ITenantStore` may read registry records |
| Scope identity, ambient scope, overrides | YFex.Tenancy (`ScopeIdentity`, `IScopeContext`) | none |
| Choosing a **database** inside the region, moving tenants between databases | YFex.Data.Placement ([databases-and-backups 04](../databases-and-backups/04-placement-routing-residency.md)) | tells Placement the home region; runs its provisioning step |
| Choosing a **bucket** inside the region | YFex.Files (store per region) | same |
| **Building** a region (cluster, databases, buckets, DNS) | platform / infrastructure-as-code, operator | detects the need (`NeedsRegion`), records the region once it exists |
| Licenses, seats | YFex.Licensing (vendor service) | runs Licensing's provisioning step |
| Accounts, invitations | YFex.Authentication | runs its step (first admin invitation) |
| Customer data, billing, contracts | the app | the app's own step; app fields live in the app's tenant table |

## 3. Shape and packages

| Package | Repo | Contents | Depends on |
|---|---|---|---|
| core additions | YFex | `TenancyPath.Root` (tenant part of any path), `RegionId`, `ResidencyRule`, `TenantLocation`, **`ITenantLocations`** (tenant → home region + residency; read-only), `ReadFreshness` (for [databases-and-backups ⚑DB29](../databases-and-backups/README.md)) | — |
| `YFex.Tenancy.Registry` | YFex.Tenancy | `TenantRecord`, `TenantState`, `RegionRecord`, `RegionUse`, `ITenantRegistry` (register, suspend, resume, offboard, delete), `IRegionCatalog`, `IRegionPolicy` (app-supplied), `ITenantProvisioningStep` (contributed by libraries), the provisioning runner (resumable), single-region mode, `ITenantLocations` implementation with cache, Meter `YFex.Tenancy.Registry`, `AddYFexTenantRegistry` | YFex.Tenancy, Microsoft.Extensions.Hosting.Abstractions |
| `YFex.Tenancy.Registry.Data` (bridge) | YFex.Tenancy | SQL store (`tenancy.tenants`, `tenancy.regions`, `tenancy.provisioning`) over YFex.Data, schema through `SchemaInstaller` | the above + YFex.Data |
| `YFex.Tenancy.Registry.Testing` | YFex.Tenancy | in-memory registry, fake steps that fail on demand | the above |

The registry does **not** reference Data.Placement, Files, Licensing or Authentication: they reference the registry's
`ITenantProvisioningStep` contract (or a bridge does), so dependencies point **into** Tenancy. Changes are announced as
`TenantRegistryChanged` (Cluster notice when Cluster is present, see ⚑TN11).

## 4. The three places

| | **Vendor cloud** | **On-premises central** | **Shop site** |
|---|---|---|---|
| Tenants | thousands, self-service or sales-driven sign-up | **one** (the customer) | none registered locally; the site belongs to central's tenant |
| Regions | several (`br-south`, `eu-west`, …), one cluster each | **one** (`on-prem`), implicit | n/a |
| Registry | SQL in the **global catalog database**, cached per node in every region | **single-region mode**: the tenant comes from configuration/installation; no table needed (table optional) | none: the site reads its tenant from its installation |
| `NeedsRegion` | real: a customer may require a region not built yet | never | never |

## 5. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Tenant key** | the tenant's `TenancyPath` of depth 1; never a free string in APIs | `farmaviva` |
| **Region** (`RegionId`) | a named location with its own cluster, databases and buckets; names declared by the operator | `br-south` |
| **Home region** | where all of a tenant's primary data lives | FarmaViva → `br-south` |
| **Residency** (`ResidencyRule`) | regions where a tenant's data **may** be (primary, replicas, backups, failover) | `br-*` |
| **Region use** | what a region may be used for: `Home`, `Standby`, `BackupOnly`, `Closed` | `br-rio`: BackupOnly |
| **Provisioning step** | one library's part of creating (or deleting) a tenant, idempotent and resumable | Files: create `/farmaviva/` prefix |
| **Tenant state** | `Registering → Provisioning → Active ⇄ Suspended → Offboarding → Deleted`, plus `NeedsRegion`, `Failed` | |

## 6. Decisions to confirm (⚑TN)

Prefix **⚑TN** (checked 2026-11-11: unused; ⚑T is Time).

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **TN1** | Where does registration live? | New packages **`YFex.Tenancy.Registry`** + **`YFex.Tenancy.Registry.Data`** in the YFex.Tenancy repo | `YFex.Tenancy.Admin` (sounds like UI/commands, which belong to apps); `YFex.Tenancy.Vendor` (wrong: on-prem also registers its one tenant); a new repo `YFex.Regions` (two catalogs — tenants and regions — that must agree; regions are useless without tenants) | §3 |
| **TN2** | Tenant key type | **`TenancyPath`** of depth 1 everywhere (already in core with canonical bytes, JSON, logging; used by Cluster, Actors, Jobs `JobKey`); add **`TenancyPath.Root`** to core | a new core `TenantId` struct (type-safe against passing a store path, but a second scope key and conversions in every library) | 02 §1 |
| **TN3** | What goes to core? | Only what several libraries must name: `TenancyPath.Root`, `RegionId`, `ResidencyRule`, `TenantLocation`, `ITenantLocations`, `ReadFreshness`. No engine | contracts in `YFex.Tenancy.Registry` (Data, Files, Backup, Cluster would all reference the registry) | 02 §1 |
| **TN4** | Region catalog | Registry owns **`tenancy.regions`** (id, use, display name, jurisdiction tag, open for new tenants yes/no, capacity notes); regions are **recorded** after the platform built them | regions in configuration per node (each node can disagree); regions in Data.Placement (Files and Backup would depend on Data) | 04 §1 |
| **TN5** | Choosing a new tenant's home region | App-supplied **`IRegionPolicy`** (default: first open `Home` region allowed by the residency rule, ordered by the app's preference); operator can pin | always one default region (residency violations); ask in UI every time (sales mistakes) | 04 §2 |
| **TN6** | Residency rule | `ResidencyRule` = allowed region names or patterns (`br-*`), set at registration, changed only by a command with permission + audit; **every** library checks it through `ITenantLocations` (placement, replicas, buckets, backups, failover) | each library its own setting (they disagree) | 04 §3 |
| **TN7** | "Do we need one more region?" | If no open region satisfies the rule, registration stops in **`NeedsRegion`** and raises an operator finding (Logging event + admin list); after the operator records the new region, the registration **continues by itself**. Capacity warnings (databases/buckets near full) come from Placement/Files metrics, not from the registry | fail the sign-up (customer lost); place it anyway in another region (illegal transfer) | 04 §4 |
| **TN8** | Changing a tenant's home region | A **region move** command: registry state `Moving`, runs each library's move step (Data.Placement move, Files copy, Backup policy change), then switches the home region. Only to a region the residency rule allows | not supported (customer contracts change; regions get closed) | 05 §3 |
| **TN9** | Lifecycle states | `Registering → Provisioning → Active ⇄ Suspended → Offboarding → Deleted`, plus `NeedsRegion` and `Failed` (with the failed step); library owns this set (enum) | app-defined states (every library would need to understand them) | 05 §1 |
| **TN10** | Provisioning steps | **`ITenantProvisioningStep`** contributed by each library (`Order`, `ProvisionAsync`, `DeprovisionAsync`, `MoveAsync?`), idempotent; the runner stores progress per step in `tenancy.provisioning` and **resumes** after a crash; built as a YFex.Orchestration process manager when Orchestration is present, else a simple sequential runner | one big app method (not resumable; every app copies it); a generic workflow engine in Tenancy (Orchestration already exists) | 05 §2 |
| **TN11** | Cache and change notices | Every node caches tenant locations (small); refresh on `TenantRegistryChanged` (Cluster notice if present) and every 60 s; reads never hit the database on the hot path | read the table per request (load and latency) | 02 §3 |
| **TN12** | Suspension | `Suspended` is read by Tenancy.Resolution adapters (requests refused with a clear error), Cluster (tenant workers stop), Jobs (tenant jobs paused); data kept | delete on non-payment (irreversible) | 05 §1 |
| **TN13** | Offboarding and deletion | `Offboarding`: portability export offered (Backup), access closed, retention clock starts; `Deleted`: each step's `DeprovisionAsync` (rows deleted, prefix deleted, **tenant key ring destroyed** → backups unreadable), tombstone kept (key never reused) | soft delete forever (LGPD minimisation); reuse keys (old backups/audit point to a new customer) | 05 §4 |
| **TN14** | Single-region mode (on-prem) | `AddYFexTenantRegistry(r => r.SingleRegion("on-prem").Tenant(path))`: one region, one tenant, from configuration or the installation; no table; steps still run once at install | the full SQL registry on every central (a table nobody edits) | 06 §1 |
| **TN15** | Where is the registry stored in the cloud? | The **global catalog database** (same as Data.Placement's catalog, ⚑DB3), replicated read-only to each region; writes only in the global region | per region (a tenant must be found before you know its region) | 04 §1 |
| **TN16** | Permissions and audit | `tenancy.view`, `tenancy.register`, `tenancy.suspend`, `tenancy.offboard`, `tenancy.residency.change` (inline approval), `tenancy.region.manage`; every change is an Audit event | app-specific checks | 06 §2 |
| **TN17** | Relation to `ITenantStore` | Unchanged contract. The registry offers an **adapter** (`RegistryTenantStore<TTenant>`) that loads the app's tenant type and fills state/region; apps keep their own fields | the registry replaces the app's tenant table (app fields leak into infrastructure) | 02 §4 |
| **TN18** | Phase order ("start small") | TN-P1 core types + single-region mode + registry contracts (on-prem useful at once); TN-P2 SQL registry + provisioning runner; TN-P3 regions, residency, `NeedsRegion`; TN-P4 suspension/offboarding/deletion; TN-P5 region move | multi-region first (nothing works on-prem until it is done) | 07 §3 |

## 7. Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | what Tenancy and core have today (file:line); gaps |
| 02 | [02-model-and-contracts.md](02-model-and-contracts.md) | core additions, registry types, provisioning step, cache, `ITenantStore` adapter |
| 03 | [03-api-and-examples.md](03-api-and-examples.md) | dev-side code: composition per place, registering a tenant, a library's step, reading the location |
| 04 | [04-regions-and-residency.md](04-regions-and-residency.md) | region catalog, choosing a region, residency, `NeedsRegion` |
| 05 | [05-lifecycle-and-provisioning.md](05-lifecycle-and-provisioning.md) | states, the runner, region move, offboarding and deletion |
| 06 | [06-places-platforms-security-aot.md](06-places-platforms-security-aot.md) | three places, per-OS, threats, LGPD, AOT |
| 07 | [07-phases-and-acceptance.md](07-phases-and-acceptance.md) | working rules, requests to other libraries, phases TN-P0…TN-P5, acceptance, empty log |

Related: [databases-and-backups](../databases-and-backups/README.md) (placement inside a region, backups),
[cluster](../cluster/README.md) (one cluster per region, notices), [files](../files/README.md), [licensing 05](../licensing/05-vendor-check-in-and-issuing.md),
[installations](../installations/README.md).

## 8. Open questions

- Should a tenant's **stores/branches** (deeper `TenancyPath` levels) also be registered here, or stay app data? Proposed: app data;
  the registry knows tenants only.
- **Self-service sign-up** (public form → registration) needs anti-abuse (rate limits, e-mail proof): app or Authentication?
- **Tenant renames** (display name) are app data; the key never changes. Confirm.
