# 03 — API and examples (dev-side code first)

> All code is `// (sketch)`; names wait for ⚑PV1–⚑PV27. The orchestrator is the **vendor's own app**; nothing here runs on nodes that
> do tenant work.

## 1. The orchestrator's composition root

```csharp
// (sketch) — vendor-ops app, control-plane namespace, its own identity
builder.Services.AddYFexProvisioning(p =>
{
    p.UseSqlStore();                                          // YFex.Provisioning.Data, control-plane database
    p.Target("k8s:br-south/prod", region: "br-south", provider: "kubernetes",
             namespaces: ["yfex-prod"], credential: "workload-identity:provisioning-br-south");   // starts in Shadow mode
    p.Target("k8s:eu-west/prod", region: "eu-west", provider: "kubernetes",
             namespaces: ["yfex-prod"], credential: "workload-identity:provisioning-eu-west");

    p.Limits(global: l => l.MaxPods(400).MaxPodIncreasePer10Minutes(20).MaxPodDecreasePerHour(40));
    p.Limits("k8s:br-south/prod", l => l.MaxPods(120).MaxPodsPerPool(40).AutoApplyMaxCeiling(16));

    p.AutoApply(rule => rule.Tier0Only().From<ScheduledCapacity>());   // only bound changes from busy-hour schedules (⚑PV12)
});
builder.Services.AddYFexProvisioningKubernetes();             // provider "kubernetes" (own minimal client, ⚑PV7)
builder.Services.AddYFexProvisioningClusterSignals(c => c.ReadOnlyConnection("cluster-br-south-readonly"));
builder.Services.AddSingleton<IProvisioningCostEstimator, VendorPriceTable>();   // app code: cost units
```

Commands come through the app's Cqrs pipeline, so permissions, approvals and audit apply without extra code:

```csharp
// (sketch) — inside Provisioning's own Cqrs configuration
b.Command<ApplyProvisioningPlan, ProvisioningRunId>()
    .RequirePermission(ProvisioningPermissions.Apply)
    .RequireApproval(ProvisioningPermissions.Approve, bind: c => c.PlanDigest.ToString());  // existing overload, Func<TCommand, string>
```

`ApplyProvisioningPlan` is an `IDecisionBound` command (`IDecisionBound.cs:12`): the provisioning rule set decides whether the plan is
`Allowed` (applies with no token) or `NeedsApproval` (one or two approvals), so approval is conditional without a second command
(⚑PV14, ⚑PV24). The `RequireApproval` form above is the fallback for a setup without rules.

## 2. Black Friday: raise the fiscal pool for one week

Before (today): an operator runs `kubectl patch scaledobject fiscal …` from a laptop with cluster-admin rights, forgets to undo it, and
nobody can tell later who did it.

After:

```csharp
// (sketch) — operator in the orchestrator UI, or an orchestrator rule
await commands.SendAsync(new SetPoolSpec("br-south", "fiscal", spec => spec
    .Temporary(new ScaleBounds(Min: 6, Max: 24),
               from: Brt(2026, 11, 27, 18, 0), until: Brt(2026, 11, 30, 23, 59), reason: "Black Friday")));

ProvisioningPlan plan = await commands.SendAsync(new PlanProvisioning(Target: "k8s:br-south/prod"));
// plan.Actions: SetScaleBounds(fiscal, 4..12 → 6..24)   Tier 1 (24 > AutoApplyMaxCeiling 16)
// plan.Limits:  MaxPodsPerPool 40 ✓, MaxPods region 120 ✓ (worst case 24 + general 30 + reports 8 = 62)
// plan.Cost:    +38 % units/month while active
// dry run:      ScaledObject yfex-prod/fiscal accepted by API server (dryRun=All)

// approver (another person, separation of duties) approves in the UI → ApprovalToken bound to plan.Digest
await commands.SendAsync(new ApplyProvisioningPlan(plan.PlanId, plan.Digest) { Approvals = [token] });
```

At 2026-11-30 23:59 the temporary bounds expire; the next drift pass plans "6..24 → 4..12", which is Tier 0 (lowering inside limits)
and applies by itself. Together with Cluster's temporary work policy (⚑CL32) the vendor changes both priority and capacity for the event.

**Cons:** two commands where `kubectl` needs one line; the approval adds minutes. Tier 0 changes under the auto ceiling stay one command.

## 3. A `YFexPool` resource (operator, PV-P4)

```yaml
# (sketch) — written by the operator from provisioning.specs; operators read it, never edit it by hand (drift would be reported)
apiVersion: provisioning.<vendor-domain>/v1alpha1     # API group open (README §8)
kind: YFexPool
metadata: { name: fiscal, namespace: yfex-prod }
spec:
  region: br-south
  roles: [fiscal]                                   # → env YFEX_NODE_ROLES=fiscal (⚑CL30)
  release: central:2026.11.3                        # chosen by CI/CD, not by Provisioning (⚑PV2)
  replicas: { min: 4, max: 12 }
  scaleOn:
    - { metric: yfex_cluster_work_backlog, workClass: fiscal, perReplica: 50 }
    - { metric: yfex_cluster_work_oldest_age_seconds, workClass: fiscal, above: 30 }
  disruption: { maxUnavailable: 1 }
  terminationGracePeriodSeconds: 60                 # Cluster drain budget (⚑CL16)
status:
  observedGeneration: 9
  ready: 5
  conditions: [ { type: Reconciled, status: "True" } ]
```

The operator turns it into a Deployment, a KEDA `ScaledObject` (Prometheus trigger on the Cluster metrics), a PodDisruptionBudget and
the role environment. Its service account can touch only those kinds in allowlisted namespaces.

## 4. A dedicated database for a large tenant (PV-P5)

```csharp
// (sketch) — after the vendor sells "dedicated database" to a large chain
await commands.SendAsync(new SetDatabaseSpec("br-south", "br-sp-dedicated-farmaviva",
    tier: "memory-optimized-8", pointInTimeRecovery: true, dedicatedTo: TenancyPath.Parse("farmaviva")));
ProvisioningPlan plan = await commands.SendAsync(new PlanProvisioning("pulumi:br-south/prod"));   // CreateDatabase → Tier 2
// two approvals → run: Pulumi preview → up → verify connection → record
// last step (orchestrator, not Provisioning): Placement admin command records DatabaseId "br-sp-dedicated-farmaviva" (Dedicated)
// then Placement's MoveAsync moves the tenant (⚑DB9) under tenant maintenance mode (⚑CL21)
```

Provisioning never writes `placement.tenants`; it hands the new `DatabaseId` and its credential **reference** to the orchestrator,
which calls Placement (⚑PV18).

## 5. On-premises recommendation (PV-P7)

```csharp
// (sketch) — the vendor service sees, from the central's load history sent at check-in, 92 % CPU every Saturday 10:00–13:00
Recommendation r = await queries.SendAsync(new GetRecommendations(Installation: centralId));
// r: "Add a second server for failover and busy hours; move role 'reports' to it." — no action, no credential
// if the customer adds the server, the vendor edits the central's Hosting module profile (roles) → signed → delivered on the vendor channel
```

## 6. Tests

```csharp
// (sketch)
await using var h = new ProvisioningHarness();
h.Target("k8s:test", mode: TargetMode.Active).Observed(pool: "fiscal", autoscaler: AutoscalerKind.Keda, bounds: (4, 12), ready: 5);
h.Limits(l => l.MaxPodsPerPool(20).AutoApplyMaxCeiling(16));

var plan = await h.PlanAsync(spec => spec.Pool("fiscal").Bounds(4, 24));
plan.Tier.ShouldBe(RiskTier.Tier1);
(await h.ApplyAsync(plan)).ShouldBe(ApplyRefused.LimitExceeded("MaxPodsPerPool", 24, 20));

h.Provider.FailNext(ProviderFailure.Transient);
var ok = await h.ApplyAsync(await h.PlanAsync(spec => spec.Pool("fiscal").Bounds(4, 16)));
ok.Run.Steps[0].Attempts.ShouldBe(2);
h.Provider.Calls.ShouldContain(c => c is SetScaleBounds { To.Max: 16 });   // not SetReplicas: KEDA owns replicas (⚑PV13)
h.Audit.Entries.Select(e => e.Descriptor).ShouldBe(["provisioning.plan", "provisioning.apply", "provisioning.verify"]);
```

## 7. Cons of the whole approach

- A second database schema and a run engine for something `kubectl` and a pipeline can do. Paid back by approvals bound to plans,
  limits, audit and drift; for a single region with three pools the manual way is acceptable — hence PV-P2 is small.
- An own Kubernetes client (⚑PV7) must follow API changes; the dozen endpoints used are stable (`apps/v1`, `autoscaling/v2`,
  `keda.sh/v1alpha1`), but it is code to keep.
- Pulumi brings a CLI and a separate process; that is the price of node pools, databases and regions in C# with previews.
