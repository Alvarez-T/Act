# 07 — Regions, tenant placement boundaries and the three places

> Decisions ⚑PV10, ⚑PV18. Agreed elsewhere: **one cluster per region**, each with its own coordination database (Cluster README §8);
> region and residency are owned by **YFex.Tenancy.Registry** (⚑TN), database placement by **YFex.Data.Placement** (⚑DB).

## 1. One target set per region

```
                         vendor control plane (global)
            ┌───────────────────────────────────────────────────────┐
            │ orchestrator app ── YFex.Provisioning ── provisioning.* │
            │        │ reads (read-only)       │ acts through        │
            └────────┼─────────────────────────┼─────────────────────┘
                     │                         │
      ┌──────────────┴──────────┐   ┌──────────┴──────────────┐
      │ region br-south         │   │ region eu-west          │
      │ k8s:br-south/prod       │   │ k8s:eu-west/prod        │
      │ pulumi:br-south/prod    │   │ pulumi:eu-west/prod     │
      │ cluster DB (coordination)│  │ cluster DB              │
      │ pools fiscal, general…  │   │ pools general…          │
      │ tenant DBs br-sp-01…07  │   │ tenant DBs eu-01…       │
      └─────────────────────────┘   └─────────────────────────┘
```

- Each region has its own targets (provider + account/context), credentials, limits and freeze; a bug or a freeze in one region does
  not touch another.
- The control plane itself must survive a region loss: its database is the vendor's global control-plane database (⚑PV19), placed
  where the vendor decides (open: same global catalog database as the tenant registry, ⚑TN15, or a separate one).
- A pool spec belongs to exactly one target; "the same pool in every region" is a template the orchestrator expands, not a
  multi-region object.

## 2. Who does what with tenants and regions

| Question | Owner | Provisioning's part |
|---|---|---|
| Which region does a new tenant live in? | Tenancy.Registry `IRegionPolicy` (⚑TN5) | none |
| No open region matches the residency rule | Registry → `NeedsRegion` (⚑TN7) | reads it as a signal → **recommendation** "build `eu-west`" |
| Which database inside the region? | Data.Placement `IPlacementPolicy` (⚑DB7) | none; may recommend "add a database server" when all are near full |
| A big tenant needs a dedicated database | sales/operator decision; Placement `Dedicated` (⚑DB6) | **creates** the database (Tier 2), returns its `DatabaseId` and credential **reference** |
| Move the tenant there | Placement `MoveAsync` + Cluster tenant maintenance (⚑DB9, ⚑CL21) | none |
| Region failover | the operator, with the platform (⚑DB11, manual) | may keep **standby capacity** (pools at min 0–1 in the standby region) as a spec; never promotes or flips roles |
| Record a region / a database in the catalogs | Registry `IRegionCatalog`; Placement admin | the **orchestrator** calls their admin commands after Provisioning's run succeeded; Provisioning never writes their tables |

## 3. Building a new region (PV-P6)

A `RegionSpec` (02 §2) becomes one Tier 2 run with ordered, resumable steps:

| # | Step | Provider | Verify |
|---|---|---|---|
| 1 | Network, Kubernetes cluster, node pools | Pulumi | API reachable with the target identity |
| 2 | Coordination database (Cluster) + its schema | Pulumi + YFex.Data `SchemaInstaller` run as a job in the new cluster | `cluster.*` tables exist |
| 3 | First tenant database server(s), PITR on | Pulumi | connection with the reference credential |
| 4 | Buckets per logical store (Files ⚑DB27) with versioning / object lock | Pulumi | write-read-delete probe |
| 5 | KEDA install, control-plane RBAC, data-plane namespaces | Pulumi / Helm by the vendor's pipeline | RBAC self-check (`SelfSubjectRulesReview`) |
| 6 | Pools (`YFexPool`s) at minimum size with the current release | Kubernetes operator | nodes Ready in Cluster's node list |
| 7 | Smoke: a synthetic tenant in the new region end to end | orchestrator | the smoke passes |
| 8 | **Record** the region in the registry with `OpenForNewTenants = false` | orchestrator → Registry command | region visible, closed |
| 9 | **Open** for new tenants | a **person**, separate command (not part of the run) | — |

The new target stays in **Shadow** mode for auto-apply rules until step 9 plus a configurable period.

## 4. The three places (on-premises and site DEFERRED — user 2026-11-12: cloud only for now; README §9)

| | Vendor cloud | On-premises central | Shop site |
|---|---|---|---|
| Machines | created by Provisioning through providers | the customer's | the customer's |
| Provisioning output | plans and runs | **recommendations** + signed **Hosting module profile** (roles, modules) (04 §5) | none |
| Signals | Cluster tables and metrics per region | load history and node summary sent at check-in (vendor channel; which channel: same open point as ⚑CL32) | none |
| Approval and audit | full ([06](06-safety-approval-audit-cost.md)) | recommendations are audited at the vendor; the profile change is a Tier 1 plan at the vendor and verified (signature) at the central | — |
| Credentials | per target, workload identity | **none** at the customer | none |

A customer who runs the central in **their own cloud** (their AKS) is still on-premises for this plan: the vendor has no credential
there. The vendor may ship the operator and `YFexPool` CRD for the customer to run themselves (open; the operator would then be the
customer's control plane, with their approvals).
