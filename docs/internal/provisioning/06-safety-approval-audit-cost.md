# 06 — Credentials, approval, audit, dry run, limits and cost

> Decisions ⚑PV11, ⚑PV14–⚑PV17. This file is the reason the library exists: the provider calls are easy, the rails are not.

## 1. Least privilege and credentials (⚑PV11)

| Who | Holds | Never holds |
|---|---|---|
| Data-plane nodes (all YFex app servers) | their database credential, their own secrets | any Kubernetes API write right, any cloud IAM right to create/delete resources, any Provisioning credential |
| Orchestrator app (control plane) | its Cqrs/admin identity; **read-only** Cluster database roles per region | Pulumi/cloud credentials (those live in the worker) |
| `.Kubernetes` provider (inside the orchestrator) | one workload identity per target, RBAC of 04 §1.4 (bounds only) | create/delete, secrets, `pods/exec` |
| Operator (PV-P4) | workload identity with create/patch/delete of pool child kinds in allowlisted namespaces | secrets, cluster-wide roles |
| Pulumi worker (PV-P5) | cloud identity limited to the vendor's resource groups / tags per region; **separate identity for deletes**, used only by Tier 2 runs | tenant data access |

Rules:

- **Workload identity first** (AKS Workload Identity, EKS Pod Identity / IRSA, GKE Workload Identity): short-lived tokens, nothing to
  leak from configuration. A static secret only where no identity exists, stored in Cryptography's `ISecretStore`
  (`SecretStores.cs:18`), referenced by name in the target (`CredentialReference`).
- **Namespace and resource allowlists** are checked by Provisioning **before** the provider call (defence in depth beside RBAC).
- **Network**: the control-plane namespace is the only one allowed to reach the Kubernetes API with these identities (network policy);
  data-plane pods use `automountServiceAccountToken: false` unless a library needs it.
- The architecture test in each app repository checks that no data-plane composition root references `YFex.Provisioning*`.

## 2. Approval (⚑PV14)

### 2.1 Tiers = default rules

The tiers below are the **default rule set in code** (⚑PV14, ⚑PV24). The approval actually required is the **verdict** of the rule set
over the plan's `ProvisioningSituation` (02 §7.1); the vendor may change it at runtime with YFex.Rules.Dynamic, but a rule can never
loosen the hard limits or the freeze, which are checked in code first.

| Tier | Examples | Default verdict |
|---|---|---|
| **0** | bounds within limits and under the auto ceiling; triggers; temporary bounds expiring; lowering inside limits | `Allowed` (auto-apply, or the planner with `provisioning.apply`) |
| **1** | new pool; raise above the auto ceiling; min → 0; node-pool change; limit change; adopt drift; target Shadow → Active; clear a global freeze | `NeedsApproval(provisioning.approve)`: one approver, not the planner |
| **2** | create/delete database, bucket, region; any delete of something holding data; cost delta above the target's threshold | `NeedsApproval` with two requests: two approvers, neither the planner |

The tier of a plan is the highest tier of its actions.

### 2.2 Binding

- `ApplyProvisioningPlan(PlanId, PlanDigest)` implements Cqrs **`IDecisionBound`** (`IDecisionBound.cs:12`): the rule decision over the
  plan is what the approvals bind, through the existing approval tokens (`ApprovalDigest`, `Approvals.cs:76`). Earlier alternative, kept
  for Tier-only setups: `RequireApproval(ProvisioningPermissions.Approve, bind: c => c.PlanDigest…)`
  (`CommandConfigurationBuilder.cs:175`, separation of duties on by default). The approval token's digest is computed by
  `ApprovalDigest` (`Approvals.cs:76`), so the token approves **this plan and nothing else**.
- A plan **expires** (default 1 h; Tier 2 default 24 h for change windows). Expired or re-planned → new digest → new approval.
- Tier 2 needs two tokens from two different approvers: request to Authorization/Cqrs for "N approvals of the same permission" (09 §2);
  until then, two permissions (`provisioning.approve`, `provisioning.approve.second`).
- The run re-checks observed state before each step (02 §6); an approval never covers a world that changed under it.

### 2.3 Tenant requests (⚑PV23)

- Tenants have **no** `provisioning.*` permission, ever. Their request enters through the vendor's app, which sends the plan on the
  vendor's behalf with `RequestedFor = <tenant>` (audited with both: the vendor actor and the tenant it was for).
- A vendor auto-apply rule may approve it within the tenant's contract ceiling (05 §2.3, §5.3); above it, a vendor approver decides.
- A tenant's request can never raise the target's limits, clear a freeze or leave shadow mode.

## 3. Audit (⚑PV15)

| Event | Facts recorded |
|---|---|
| `provisioning.spec` | kind, name, old → new version, actor |
| `provisioning.plan` | plan id, digest, target(s), actions, tier, limit checks, cost estimate, observed snapshot hash |
| `provisioning.approve` | plan id, digest, approver, token key id |
| `provisioning.apply` / `.verify` | run id, step, action, provider request id, outcome, duration |
| `provisioning.refused` | plan id, reason (limit, freeze, shadow, stale, missing approval) |
| `provisioning.drift` | target, resource, kind, first seen |
| `provisioning.freeze` / `.limits` / `.mode` | old → new, actor, reason |

Recorded through `IAuditTrail.RecordAsync` (`AuditTrailContracts.cs:80`) with `AuditDescriptor<T>` per fact type. No secrets, no
connection strings (only references), no tenant personal data. Logging gets the same events at Information without details.

## 4. Dry run and shadow mode (⚑PV16)

- **Dry run** is a step of every apply: dry run → apply → verify. `PlanProvisioning` also dry-runs and shows the provider's answer
  (admission errors appear before approval, not after).
- **Shadow mode** per target: plans, recommendations, auto-apply rules and drift run normally, but the apply step is replaced by "would
  apply" and audited as such. New targets start in Shadow; leaving it is Tier 1.
- Providers without a server-side dry run (some cloud SDK calls) mark actions `DryRunNotSupported`, raising them to Tier 1 at least.

## 5. Limits, cost units, freeze (⚑PV17)

### 5.1 Limits

Checked at plan time and again before each step, for the **worst case** of the plan (all pools at their new Max):

| Limit | Scope | Example |
|---|---|---|
| `MaxPods`, `MaxPodsPerPool` | global, target | 400 global, 120 in `br-south`, 40 per pool |
| `MaxVirtualCpus`, `MaxMemoryGiB` | target | from pool requests × Max |
| `MaxNodePoolNodes` | target | 30 |
| `MaxDatabases`, `MaxBuckets`, regions | global, target | 12 databases in `br-south` |
| `MaxPodIncreasePer10Minutes`, `MaxPodDecreasePerHour` | target | +20 / −40 |
| `AutoApplyMaxCeiling` | target | Tier 0 may raise Max up to 16 per pool |

### 5.2 Cost units

- The infrastructure rule keeps `Money` (`YFex/src/YFex/Money.cs:27`) out of contracts (`infrastructure.md:7`). Provisioning knows
  **resource units**; cost is the vendor's business.
- Optional `IProvisioningCostEstimator` (app code, e.g. a price table) returns `CostEstimate(before, after, unitName)` as plain decimals
  in a unit the vendor names ("USD-est", "credits"). Provisioning only compares with `CostBudgetPerMonth` in the same unit: over budget
  → plan tier ≥ 1 with the estimate shown; above a hard budget → refused.
- Without an estimator, limits in resource units are the only cost control (and enough for PV-P2).

### 5.3 Freeze

- `FreezeProvisioning(scope, reason, until?)`: global, per target, per pool. Plans still compute (operators can prepare); applies,
  auto-apply rules and drift repair stop; running runs pause after the current step.
- Setting a freeze needs `provisioning.freeze` only (fast in an incident); clearing a **global** freeze is Tier 1.
- A kill switch outside the app: removing the provider identity's role binding stops everything even if the orchestrator is broken
  (documented in the runbook).
