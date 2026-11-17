# 01 — Current state and inventory

> Checked against source on **2026-11-11** (grep over every `C:\Dev\YFex\YFex*` repository, `*.cs` and `*.csproj`, `obj/` and `bin/`
> excluded). Current source wins over this file; re-check before each phase.

## 1. What exists for provisioning today

**Nothing.** No YFex repository creates, scales or removes machines, pools, databases or regions:

- no reference to Kubernetes (`k8s.`, `KubernetesClient`), KEDA (`ScaledObject`) or Pulumi in any `.cs` or `.csproj`;
- no `YFex.Provisioning`, `YFex.Cluster`, `YFex.Hosting`, `YFex.Backup` or `YFex.Files` folder under `C:\Dev\YFex` (all design only);
- the only "provisioning" code is **installation enrollment** (§3).

## 2. Existing code this plan builds on

| What | Where (file:line) | How Provisioning uses it |
|---|---|---|
| `TenancyPath` | `YFex/src/YFex/Tenancy/TenancyPath.cs:31` | not in Provisioning's contracts (no tenant placement, ⚑PV18); appears only where a dedicated database is requested **for** a tenant, passed through to Placement |
| `NodeIdentity` with `string? Region` | `YFex/src/YFex/Diagnostics/NodeIdentity.cs:29`, `:38` | Region is a free string today; Tenancy plans core `RegionId` (⚑TN3). Provisioning's targets use `RegionId` and set the node's region through the pool spec's environment (finding F1) |
| `InstallationRole` (`Central`, `SiteBroker`, `Terminal`) | `YFex/src/YFex/Installations/InstallationIdentity.cs:7` | cloud nodes are not installations (⚑CL4); on-premises recommendations (⚑PV10) are addressed to the `Central` installation |
| Cqrs commands | `YFex.Cqrs/src/YFex.Cqrs/ICommand.cs:12` (`ICommand<out TResult>`) | the orchestrator API (⚑PV3) |
| `RequireApproval(permission, bind, separationOfDuties = true)` | `YFex.Cqrs/src/YFex.Cqrs/Configuration/CommandConfigurationBuilder.cs:175` (string bind), `:179` (Guid), `:183` (long) | `ApplyProvisioningPlan` binds its approval to the plan digest (⚑PV14) |
| `ApprovalToken`, `ApprovalDigest` | `YFex.Authorization/src/YFex.Authorization.Public/Approvals.cs:47`, `:76`; canonical overload `Of<TValue>` `:108` | approvals for Tier 1/2 actions |
| `IApprovalPolicySource` | `YFex.Authorization/src/YFex.Authorization.Public/Approvals.cs:280` | lets the vendor tighten approval per action kind without code (open: whether the tier table is a policy source) |
| `Permission` | `YFex.Authorization/src/YFex.Authorization.Public/Permission.cs:19` | `ProvisioningPermissions` catalog |
| `IAuditTrail`, `AuditDescriptor<T>` | `YFex.Audit/src/YFex.Audit.Public/AuditTrailContracts.cs:80`, `:48` | every plan, approval, apply, drift (⚑PV15) |
| `ISecretStore` | `YFex.Cryptography/src/YFex.Cryptography/Secrets/SecretStores.cs:18` | static provider credentials where no workload identity exists (⚑PV11) |
| `ISigningKeyStore` | `YFex.Cryptography/src/YFex.Cryptography/Signing/SigningContracts.cs:35` | signing the on-premises Hosting profile / recommendation (⚑PV10), through the vendor ring Licensing already uses |
| `VendorKeys`, `VendorKeyRole` | `YFex.Licensing/src/YFex.Licensing/Vendor/VendorKeys.cs:17`, `Documents/VendorKeyList.cs:9` | the central already trusts vendor keys; the signed profile reuses that trust |
| `ITrustedClock` | `YFex/src/YFex/Time/ITrustedClock.cs:8` | plan expiry, temporary bounds expiry (control plane in the cloud: platform time is acceptable; trusted time where a decision is security-relevant) |
| `SchemaInstaller` | `YFex.Data/src/YFex.Data/Schema/SchemaInstaller.cs:34` | `provisioning.*` tables (⚑PV19) |
| `ProcessManager<TState>`, `RequestTimeout` | `YFex.Orchestration/src/YFex.Orchestration/ProcessManager/ProcessManager.cs:15`, `:41` | **runs** are process managers with durable timeouts (⚑PV19, changed 2026-11-12) |
| `ISagaStore`, `KeyValueSagaStore` | `YFex.Orchestration/src/YFex.Orchestration/Store/ISagaStore.cs:24`, `Store/KeyValueSagaStore.cs:28` | run state; shared store over YFex.Persistence.Data to confirm (OR1) |
| Cqrs references Validation and Rules | `YFex.Cqrs/src/YFex.Cqrs/YFex.Cqrs.csproj` (ProjectReferences) | Provisioning gets both through Cqrs: no `.Rules` / `.Validation` packages (⚑PV24, ⚑PV26) |
| `IDecisionBound : IApprovalBound` | `YFex.Cqrs/src/YFex.Cqrs/IDecisionBound.cs:12` | `ApplyProvisioningPlan` binds approvals to the rule decision (⚑PV14) |
| `ISituation<TSelf>`, `RuleSetDefinition<TSituation>` | `YFex.Rules/src/YFex.Rules/Situations/ISituation.cs:9`, `Definition/RuleSetDefinition.cs:12` | `ProvisioningSituation` + default rule set (⚑PV24) |
| `Verdict` (`Allowed`, `Warned`, `NeedsApproval`, `Denied`), `ApprovalRequest` | `YFex.Rules/src/YFex.Rules/Decisions/Verdict.cs:53`, `:40–46`, `:7` | the verdict decides auto-apply vs approvals |
| `DynamicRules<TSituation>` | `YFex.Rules/src/YFex.Rules.Dynamic/DynamicRules.cs:22` (depends on Expressions + Persistence) | optional runtime-editable rules |
| `[Validatable]`, `[Invariant]` | `YFex.Validation/src/YFex.Validation/Attributes.cs:8`, `:106` | spec validation (⚑PV26) |
| `Money` | `YFex/src/YFex/Money.cs:27` | **not** used: the infrastructure rule keeps `Money` out of contracts (`docs/agent/rules/infrastructure.md:7`); cost is in vendor-defined cost units (⚑PV17) |

## 3. The word "provisioning" is already taken twice

| Meaning | Where | Name used there |
|---|---|---|
| **Installation provisioning**: central's answer to an enrollment, carrying certificates and trust | `YFex.Installations/src/YFex.Installations/Enrollment/EnrollmentContracts.cs:109` (`InstallationProvisioning`), `:273` (`ProvisioningResponse`); `YFex.Installations.Central/CentralEnrollmentService.cs:574` (`ProvisionAsync`) | code, built |
| **Tenant provisioning**: each library's step when a tenant is created (rows, folders, key ring, license) | [tenancy 05](../tenancy/05-lifecycle-and-provisioning.md), `ITenantProvisioningStep`, state `Provisioning` ([tenancy 02](../tenancy/02-model-and-contracts.md)) | design |
| **Infrastructure provisioning** (this plan) | — | proposed `YFex.Provisioning` |

⚑PV1 keeps the name and asks every document and type to qualify: types here never start with `Tenant…` or `Installation…`, and the
two other meanings are always written "installation provisioning" / "tenant provisioning". The alternative is the name `YFex.Fleet`.

## 4. Designed elsewhere, not built (inputs this plan relies on)

| Input | Design | State |
|---|---|---|
| Node roles, pools, `YFEX_NODE_ROLES` | [cluster 11 §3.3](../cluster/11-work-classes-priority-and-control.md) (⚑CL30) | pending |
| Per-class metrics, `cluster.capacity_hints` | cluster 11 §3.4 (⚑CL31) | pending |
| Per-tenant hourly load history | [cluster 07 §3](../cluster/07-scaling-and-storage.md) | design |
| Drain on stop, node states | [cluster 06](../cluster/06-health-drain-upgrades-operations.md) (⚑CL16) | under discussion |
| Runtime roles / work policy commands (`SetNodeRoles`, `SetWorkPolicy`) | cluster 11 §4 (⚑CL32) | pending |
| `RegionId`, `RegionRecord`, `RegionUse`, `NeedsRegion`, `OpenForNewTenants` | [tenancy 02](../tenancy/02-model-and-contracts.md), [04](../tenancy/04-regions-and-residency.md) (⚑TN3, ⚑TN7) | pending |
| `DatabaseId`, `IPlacementPolicy`, `Dedicated` databases, moves | [databases-and-backups 04](../databases-and-backups/04-placement-routing-residency.md), [05](../databases-and-backups/05-moves-replicas-failover.md) (⚑DB6, ⚑DB7, ⚑DB9) | decided 2026-11-11; DB-P0 done 2026-11-12 (Backup skeleton); Placement from DB-P3/DB-P5, not built |
| Hosting module profile with roles (HO4) | [hosting](../hosting/README.md), [cluster 10 §2](../cluster/10-phases-and-acceptance.md) | proposed |
| `YFex.Licensing.Vendor` (vendor service, check-in) | [licensing README §3](../licensing/README.md) | design; only `YFex.Licensing` and `.Testing` exist in `YFex.Licensing/src` |

## 5. Findings while checking

| # | Finding | Proposed owner |
|---|---|---|
| F1 | `NodeIdentity.Region` is a free `string?` (`NodeIdentity.cs:38`) while Tenancy plans a core `RegionId`. A node started by a pool spec should report the same region id the registry uses | Tenancy plan (⚑TN3) / Logging (owns the record); Provisioning only sets the value |
| F2 | Three "orchestrators": the vendor orchestrator app (Cluster 11 wording), YFex.Orchestration (sagas), `AutomationOrchestrator` (`YFex.Automation/src/YFex.Automation.Core/Orchestration/AutomationOrchestrator.cs:24`). This folder always writes **vendor orchestrator app** | docs only |
| F3 | Cluster 11 §8 lists "change only the autoscaler's settings" as the smallest option; this plan makes it PV-P2 and keeps Cluster unchanged | — |
