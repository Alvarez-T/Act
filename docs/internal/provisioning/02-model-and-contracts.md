# 02 — Model and contracts

> All names are proposals (⚑PV1–⚑PV27 pending). Code is `// (sketch)`.

## 1. Targets

A **target** is one place a provider may act on. It is configuration of the orchestrator app, not data a command can invent.

```csharp
// (sketch)
public readonly record struct ProvisioningTargetId(string Value);          // "k8s:br-south/prod"; validated like LeaseKey segments

public sealed record ProvisioningTarget(
    ProvisioningTargetId Id,
    RegionId Region,                       // core type from the Tenancy plan (⚑TN3)
    string Provider,                       // "kubernetes", "pulumi"; resolved to a registered IProvisioningProvider
    ImmutableArray<string> Namespaces,     // allowlist; anything outside is refused before the provider is called
    TargetMode Mode,                       // Shadow | Active (new targets start Shadow, ⚑PV16)
    string CredentialReference);           // name of a workload identity or an ISecretStore entry, never the secret

public enum TargetMode : byte { Shadow = 1, Active = 2 }
```

## 2. Desired state (specs)

Specs are versioned; every change is a new version with the actor who made it.

```csharp
// (sketch)
public readonly record struct PoolId(string Value);                       // "fiscal", "general"

public sealed record PoolSpec(
    PoolId Pool,
    ProvisioningTargetId Target,
    ImmutableArray<WorkloadLabel> Labels,   // plain labels (⚑PV25); the .Cluster bridge maps them to Cluster roles → env YFEX_NODE_ROLES; empty = none
    ScaleBounds Bounds,                     // min / max replicas
    ImmutableArray<ScaleTrigger> Triggers,  // e.g. backlog of work class "fiscal" > 50 per pod
    ImmutableArray<TemporaryBounds> Temporary, // raised bounds with an expiry (Black Friday), ⚑PV13
    PoolTemplate? Template);                // image reference, resources, probes: only used by the operator (⚑PV8); null = pool exists already

public readonly record struct ScaleBounds(int Min, int Max);              // 0 ≤ Min ≤ Max; Max ≤ target limit
public sealed record TemporaryBounds(ScaleBounds Bounds, DateTimeOffset From, DateTimeOffset Until, string Reason);
public sealed record ScaleTrigger(string Metric, string? WorkClass, double TargetPerReplica);

public sealed record DatabaseSpec(string Name, ProvisioningTargetId Target, string Tier, bool PointInTimeRecovery,
    TenancyPath? DedicatedTo);              // set when created for one tenant; Placement records and moves (⚑PV18)

public sealed record RegionSpec(RegionId Region, ImmutableArray<ProvisioningTargetId> Targets,
    ImmutableArray<PoolSpec> Pools, ImmutableArray<DatabaseSpec> Databases, ImmutableArray<string> Buckets);

public sealed record DesiredState(long Version, ImmutableArray<PoolSpec> Pools, ImmutableArray<DatabaseSpec> Databases,
    ImmutableArray<RegionSpec> Regions);
```

- A **pool** is any scalable workload (a Deployment or the provider's equivalent); it need not be a Cluster pool. A pool with no
  labels, or no pools at all (Provisioning used only for databases or regions), is valid (⚑PV25).
- `WorkloadLabel` is Provisioning's own string value type. The core never references YFex.Cluster: only the `.Cluster` bridge turns
  labels into Cluster roles.
- Specs, limits and targets are `[Validatable]` (YFex.Validation, ⚑PV26): `[Invariant]` min ≤ max, max ≤ the target's
  `MaxPodsPerPool`, name formats. The orchestrator UI gets field errors from the generated validator before any plan is computed.

## 3. Observed state

```csharp
// (sketch)
public sealed record ObservedPool(PoolId Pool, ScaleBounds? Bounds, int Desired, int Ready,
    AutoscalerKind Autoscaler,              // None | Hpa | Keda — decides which field Provisioning may touch (⚑PV6, ⚑PV13)
    bool HasDisruptionBudget, string? ProviderVersion);

public enum AutoscalerKind : byte { None = 1, Hpa = 2, Keda = 3 }
```

Observed state is read from the provider for every plan and every drift check; it is stored only as a **snapshot inside the plan** (for
the audit and the digest), never as the truth (⚑PV19).

## 4. Plans and actions

```csharp
// (sketch) — closed set: a new action kind must break every switch (all-projects rule)
public closed record ProvisioningAction(ProvisioningTargetId Target, RiskTier Tier)
{
    public sealed record SetScaleBounds(ProvisioningTargetId Target, PoolId Pool, ScaleBounds From, ScaleBounds To)
        : ProvisioningAction(Target, TierOf(From, To));
    public sealed record SetReplicas(ProvisioningTargetId Target, PoolId Pool, int From, int To)        // only when no autoscaler
        : ProvisioningAction(Target, RiskTier.Tier0);
    public sealed record SetTriggers(ProvisioningTargetId Target, PoolId Pool, ImmutableArray<ScaleTrigger> To)
        : ProvisioningAction(Target, RiskTier.Tier0);
    public sealed record CreatePool(ProvisioningTargetId Target, PoolSpec Spec) : ProvisioningAction(Target, RiskTier.Tier1);
    public sealed record DeletePool(ProvisioningTargetId Target, PoolId Pool) : ProvisioningAction(Target, RiskTier.Tier1);
    public sealed record SetNodePool(ProvisioningTargetId Target, string NodePool, ScaleBounds To) : ProvisioningAction(Target, RiskTier.Tier1);
    public sealed record CreateDatabase(ProvisioningTargetId Target, DatabaseSpec Spec) : ProvisioningAction(Target, RiskTier.Tier2);
    public sealed record DeleteDatabase(ProvisioningTargetId Target, string Name) : ProvisioningAction(Target, RiskTier.Tier2);
    public sealed record CreateBucket(ProvisioningTargetId Target, string Bucket) : ProvisioningAction(Target, RiskTier.Tier2);
    public sealed record CreateRegion(RegionSpec Spec) : ProvisioningAction(Spec.Targets[0], RiskTier.Tier2);
}

public enum RiskTier : byte { Tier0 = 0, Tier1 = 1, Tier2 = 2 }

public sealed record ProvisioningPlan(
    Guid PlanId, Hash256 Digest,            // digest over canonical bytes of the actions + observed snapshot; approvals bind it (⚑PV14)
    long SpecVersion, ImmutableArray<ProvisioningAction> Actions, RiskTier Tier,  // highest tier of its actions
    ImmutableArray<LimitCheck> Limits, CostEstimate? Cost, DateTimeOffset ExpiresAt, ActorIdentity PlannedBy);
```

- `RiskTier` is the **default classification** of an action; what approval it needs is decided by the rule set (§7.1, ⚑PV24), whose
  default rules map Tier 0 → Allowed, Tier 1 → one approval, Tier 2 → two approvals. `TierOf(From, To)`: inside the target's limits and
  Max not above the auto ceiling → Tier 0; else Tier 1. Lowering Min to 0 is Tier 1 (a pool that can go dark).
- The digest uses canonical bytes (`ICanonicalValue<T>` rules) with a domain tag `yfex.provisioning.plan.v1`; a change of format gets a
  new tag and a golden test (infrastructure rule on stored formats).

## 5. Provider seam (⚑PV5)

```csharp
// (sketch) — public: Pulumi and Kubernetes packages implement it; an app may add its own (a private cloud)
public interface IProvisioningProvider
{
    string Name { get; }                                         // "kubernetes"
    ProviderCapabilities Capabilities { get; }
    ValueTask<ObservedTarget> ObserveAsync(ProvisioningTarget target, CancellationToken cancellationToken);
    ValueTask<DryRunResult> DryRunAsync(ProvisioningTarget target, ProvisioningAction action, CancellationToken cancellationToken);
    ValueTask<ApplyResult> ApplyAsync(ProvisioningTarget target, ProvisioningAction action, Guid stepId, CancellationToken cancellationToken);
    ValueTask<VerifyResult> VerifyAsync(ProvisioningTarget target, ProvisioningAction action, CancellationToken cancellationToken);
}

[Flags]
public enum ProviderCapabilities : uint
{
    None = 0, ScaleBounds = 1, Pools = 1 << 1, NodePools = 1 << 2, Databases = 1 << 3, Buckets = 1 << 4, Regions = 1 << 5,
}
```

- **Idempotent apply**: the run engine passes a step id (stored with the run, reused on retry); a retried step after a crash must find the change already
  made and report success (Kubernetes: server-side apply is idempotent; Pulumi: `up` converges).
- `ApplyResult` and `VerifyResult` are small unions (`Applied`, `AlreadyInPlace`, `Refused(reason)`, `Failed(retryable)`), cold path,
  so the C# `union` declaration is fine.
- `DryRunAsync` must not change anything; providers without a server-side dry run return `DryRunResult.NotSupported`, and actions of
  such providers are at least Tier 1.

## 6. Runs (⚑PV19)

```
Planned ──approve (if Tier ≥ 1)──▶ Approved ──start──▶ Running ──every step: dry run → apply → verify──▶ Succeeded
   │                                   │                  │
   └─ expired / superseded            └─ expired        ├─ step failed (retryable) → retry with Resilience policy
                                                          ├─ step failed (final) → Failed (remaining steps not run; report)
                                                          └─ freeze set → Paused (resumes when unfrozen and re-planned)
```

- **A run is a YFex.Orchestration process manager** (`ProcessManager<TState>`, `YFex.Orchestration/src/YFex.Orchestration/ProcessManager/ProcessManager.cs:15`),
  correlated by plan id: each step is dry run → apply → verify; waiting for pods to become Ready, plan expiry, the end of temporary
  bounds and long Pulumi `up` calls are **durable timeouts** (`RequestTimeout`, `ProcessManager.cs:41`); a final failure goes to
  Orchestration's dead-letter handling. State lives in `ISagaStore` (`Store/ISagaStore.cs:24`); with several orchestrator nodes it must
  be `KeyValueSagaStore` (`Store/KeyValueSagaStore.cs:28`) over a **shared** key-value store (YFex.Persistence.Data; checked in PV-P0).
- **Compensation never deletes infrastructure on its own.** A failed multi-step run (a region) compensates by **proposing** a clean-up
  plan, which goes through the rules and approvals like any other plan.
- One run per target at a time: the correlation key `provisioning.run:<target>`; with YFex.Cluster present, also a Cluster lease per
  target with **fenced** step writes (⚑CL5, ⚑CL6) through the `.Cluster` bridge.
- Before each step the engine re-observes: if the observed state no longer matches the plan's snapshot for that resource, the run stops
  as `Stale` and a new plan is needed (no blind apply over someone else's change).
- Rollback is not automatic: a failed run leaves a report with "re-plan to the previous spec version" as one command.

## 7. Limits, freeze, cost (⚑PV17)

```csharp
// (sketch)
public sealed record ProvisioningLimits(
    ProvisioningTargetId? Target,                      // null = global
    int MaxPods, int MaxPodsPerPool, int MaxNodePoolNodes, int MaxDatabases, int MaxBuckets,
    double MaxVirtualCpus, double MaxMemoryGiB,
    int MaxPodIncreasePer10Minutes, int MaxPodDecreasePerHour,
    int AutoApplyMaxCeiling,                           // Tier 0 may raise Max up to here; above → Tier 1
    decimal? CostBudgetPerMonth);                      // in the vendor's cost units; null = no budget check

public sealed record Freeze(ProvisioningTargetId? Target, PoolId? Pool, string Reason, ActorIdentity By, DateTimeOffset? Until);

public interface IProvisioningCostEstimator                // optional, app-supplied
{
    ValueTask<CostEstimate> EstimateAsync(ProvisioningPlan plan, CancellationToken cancellationToken);
}
public readonly record struct CostEstimate(decimal UnitsPerMonthBefore, decimal UnitsPerMonthAfter, string UnitName); // "USD-est", "credits"
```

Limits are the **last** check before apply and are re-checked at every step; changing a limit is itself a Tier 1 command.

### 7.1 Rules over the plan (⚑PV24)

Order of checks for every plan: **Validation** of specs (⚑PV26) → planner → **hard limits + freeze in code** → **rule set** → dry run.
Rules can tighten but never loosen what the code checks.

```csharp
// (sketch) — YFex.Rules: ISituation<T> (YFex.Rules/src/YFex.Rules/Situations/ISituation.cs:9),
// RuleSetDefinition<T> (Definition/RuleSetDefinition.cs:12), Verdict (Decisions/Verdict.cs:53)
public sealed partial record ProvisioningSituation(
    ProvisioningPlan Plan, ObservedTarget Observed, ProvisioningLimits Limits,
    TenancyPath? RequestedFor, string? ContractKind,            // tenant requests (⚑PV23)
    PlanSource Source, CostEstimate? Cost) : ISituation<ProvisioningSituation>;

// Default rules shipped in code (the former tiers); the vendor overrides them at runtime with YFex.Rules.Dynamic.
//   Tier 0 and Source in {ScheduledCapacity, CapacityHint}          → Allowed
//   Tier 1                                                           → NeedsApproval(provisioning.approve)
//   Tier 2 or cost delta above the target's threshold                → NeedsApproval(provisioning.approve, provisioning.approve.second)
//   RequestedFor set and pods of the tenant's workloads ≤ ceiling    → Allowed   (contract kind "enterprise")
//   Target in Shadow                                                 → Warned ("would apply")
```

`Verdict` cases `Allowed`, `Warned`, `NeedsApproval(ApprovalRequestList)`, `Denied` (`Verdict.cs:40–46`) carry Authorization
`Permission`s (`ApprovalRequest`, `Verdict.cs:7`). `ApplyProvisioningPlan` implements Cqrs `IDecisionBound`
(`YFex.Cqrs/src/YFex.Cqrs/IDecisionBound.cs:12`), so the approvals bind the rule decision over that plan. Rules may also **derive
values** (e.g. a recommended minimum for a busy hour); the planner turns them into a recommendation plan.

## 8. Signals (read side, ⚑PV12)

```csharp
// (sketch)
public interface IProvisioningSignals
{
    ValueTask<ImmutableArray<CapacityHint>> CapacityHintsAsync(RegionId region, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<LoadProfile>> BusyHoursAsync(RegionId region, PoolId pool, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<RegionNeed>> RegionNeedsAsync(CancellationToken cancellationToken);   // Tenancy NeedsRegion
}
public sealed record CapacityHint(PoolId Pool, int WantedNodes, string Reason, DateTimeOffset At);
```

Implementations: `YFex.Provisioning.Cluster` (hints + history from Cluster's tables, read-only role); the orchestrator app adds its
metrics backend and Tenancy/Placement queries.

## 9. Permissions and commands (⚑PV3)

| Permission | Allows |
|---|---|
| `provisioning.view` | specs, plans, runs, observed state, drift, limits |
| `provisioning.plan` | change specs (new version), compute plans, dry run |
| `provisioning.apply` | start an approved (or Tier 0) plan |
| `provisioning.approve` | approve Tier 1/2 plans (separation of duties: never your own plan) |
| `provisioning.freeze` | set and clear freezes (clearing a global freeze needs an approval) |
| `provisioning.limits` | change limits (Tier 1) and target modes (Shadow ↔ Active) |

| Command / query | Result |
|---|---|
| `SetPoolSpec`, `SetDatabaseSpec`, `SetRegionSpec` | new spec version |
| `PlanProvisioning(target or all)` | `ProvisioningPlan` (with dry-run results) |
| `ApplyProvisioningPlan(PlanId, PlanDigest)` | run id; configured with `.RequireApproval(...)` for Tier ≥ 1 |
| `FreezeProvisioning`, `UnfreezeProvisioning` | freeze state |
| `SetProvisioningLimits`, `SetTargetMode` | limits / mode |
| `AdoptDrift(resource)` | spec := observed (new version) |
| `GetPools`, `GetPlans`, `GetRuns`, `GetDrift`, `GetRecommendations` | read models |
