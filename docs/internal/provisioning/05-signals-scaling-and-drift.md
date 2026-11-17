# 05 — Signals, who scales what, drift, scale-in safety

> Decisions ⚑PV12, ⚑PV13, ⚑PV20, ⚑PV21.

## 1. Inputs (all read-only)

| Signal | Source (owner) | Read how | Used for |
|---|---|---|---|
| **Capacity hints** "pool `fiscal` wants 6 nodes" | Cluster `cluster.capacity_hints` (⚑CL31, cluster 11 §3.4) | `YFex.Provisioning.Cluster`, read-only database role per region | recommendations to raise/lower bounds |
| **Busy hours** per pool (p50/p95 by hour of week) | Cluster load history (cluster 07 §3) | same bridge | scheduled capacity (§2.2) |
| Per-class backlog, oldest age, pressure | Cluster metrics `yfex.cluster.work.*`, `yfex.cluster.pressure` (cluster 11 §3.4) | the vendor's metrics backend through an app adapter (README §8) | KEDA triggers (not Provisioning's loop); recommendation evidence |
| Ready nodes per role | Cluster node list (⚑CL14) | same bridge | scale-in safety (§4) |
| `NeedsRegion` registrations | Tenancy.Registry (⚑TN7) | the orchestrator's registry query | "build region `eu-west`" recommendation (07 §1) |
| Databases near full, tenants per database | Data.Placement metrics (databases-and-backups 04) | app adapter | "add database server in `br-south`" recommendation |
| Spend | the vendor's billing export | outside; `IProvisioningCostEstimator` estimates only | budget checks (⚑PV17) |

Nodes never push to Provisioning; Provisioning pulls. Nothing on the data plane learns that Provisioning exists.

## 2. One owner per knob (⚑PV13)

### 2.1 The split

| Knob | Owner | Loop speed |
|---|---|---|
| `replicas` of a pool | KEDA / HPA (on Cluster's per-class metrics, CPU) | seconds |
| Pod → node placement, node autoscaling inside a node pool | Kubernetes scheduler + cluster autoscaler / Karpenter | minutes |
| **Bounds** and triggers of a pool; temporary bounds; scheduled minimums | **Provisioning** | minutes–hours, through plans |
| Pools, node pools, databases, buckets, regions | **Provisioning** | hours–days, approved |
| Roles of a running node, work policy | Cluster (`SetNodeRoles`, `SetWorkPolicy`, ⚑CL32) | seconds, through commands |

Provisioning refuses to plan `SetReplicas` for a pool whose observed state shows an autoscaler (`AutoscalerKind.Keda` / `Hpa`).

### 2.2 Scheduled capacity

From the busy-hour profile, Provisioning proposes **temporary minimums**: "Saturdays 09:30–13:30 BRT, `general` min 8 instead of 3",
so pods are warm before the peak instead of reacting to it. Two ways to apply:

| Way | Pros | Cons |
|---|---|---|
| **Temporary bounds in the spec** (recommended) — the run engine changes `minReplicaCount` at the start and back at the end | auditable, limits apply, visible in the orchestrator | two plans per window (both Tier 0 under the auto ceiling) |
| KEDA **cron trigger** written into the `ScaledObject` | KEDA does the timing itself | schedule hidden inside a resource; harder to review; a missed removal keeps capacity forever |

Schedules use IANA zone ids and computed UTC (all-projects rule).

### 2.3 Recommendations and auto-apply rules

- Every signal-driven proposal is a **recommendation**: a plan in state `Proposed` with its evidence (hint, history window).
- An **auto-apply rule** is a **YFex.Rules** rule over `ProvisioningSituation` (⚑PV24, 02 §7.1): a plan starts without a person when
  the rule set's verdict is `Allowed`, the target is `Active` (not Shadow), no freeze, and the hard limits pass (checked in code first).
- Rate limits (⚑PV17) cap what rules can do in a window; a rule that keeps hitting them is reported.
- What the default rules look at:

| Situation field | Example rule |
|---|---|
| `Source` | `ScheduledCapacity`, `CapacityHint` → Allowed for Tier 0; `TenantRequest` → see contract |
| tier of the plan | Tier 1 → NeedsApproval(`provisioning.approve`); Tier 2 → two approvals |
| ceilings | pool max ≤ 16 → Allowed; above → NeedsApproval |
| `RequestedFor` + `ContractKind` | contract kind `enterprise`: tenant's dedicated pool ≤ 6 pods → Allowed (⚑PV23) |
| time (IANA zone) | Black Friday week: Tier 1 raises on `fiscal` → Allowed up to max 24 |

  Default rules ship in code. The vendor changes or adds rules **at runtime** with YFex.Rules.Dynamic (JSON specs validated against the
  situation); publishing a rule version is an audited command with `provisioning.limits`. Rules may also **derive values** (a
  recommended minimum per busy hour) that become recommendations.

## 3. Drift (⚑PV20)

- A drift pass every 5 minutes per target (configurable; a YFex.Jobs `[Interval]` job, ⚑PV27) observes and compares with the current spec version.
- Kinds: **changed field** (max 20, spec 12), **missing resource** (pool deleted by hand), **unknown resource** (a pool with the
  `yfex.pool` label but no spec), **manager conflict** (another field manager owns a field Provisioning should own).
- Output: metric `yfex.provisioning.drift` (target, kind), an audit entry the first time a drift is seen, a row in the orchestrator's
  drift list.
- Resolution: **adopt** (spec := observed, new version, Tier 1 because it may exceed limits) or **re-apply** (a normal plan). Auto
  re-apply only for Tier 0 fields under a rule. During a freeze drift is reported, never repaired.

## 4. Scale-in safety (⚑PV21)

Before any plan lowers `Max` below current ready replicas, lowers `Min` to 0, or deletes a pool:

1. **Role coverage**: every work class whose roles map to this pool must still have at least one Ready node with that role in the
   region after the change (read Cluster's node list and policy); otherwise the plan is refused with the classes named.
2. **Disruption budget**: the pool must have a PodDisruptionBudget (`maxUnavailable` ≥ 1); missing → Tier 1 and a warning.
3. **Drain time**: the pool's `terminationGracePeriodSeconds` must cover Cluster's drain budget (⚑CL16); shorter → warning (leases
   would expire instead of being handed over: work stops ~35 s).
4. **Rate**: at most `MaxPodDecreasePerHour` pods removed per hour per target.
5. **Later**: before a scale-in, set `controller.kubernetes.io/pod-deletion-cost` from Cluster's lease counts so Kubernetes removes the
   pods holding the fewest leases (Cluster README §8 open point). Needs `patch` on pods: added to RBAC only in that phase.

Deleting a pool additionally requires its roles to have been moved (Cluster `SetNodeRoles`) and its backlog to be zero for a period.

## 5. Pools, shared tenants and tenant-dedicated capacity (⚑PV23)

### 5.1 Pools are optional

| Shape | Pool specs | Cluster roles |
|---|---|---|
| **One shared pool** (default) | one `PoolSpec` (`general`, bounds only) | none: every node runs every class and every tenant |
| Pools per work class | `fiscal`, `general`, … | class → role (⚑CL30) |
| Shared pools + a tenant's own pool | the shared ones + `dedicated-<tenant>` | the tenant's scopes go only to nodes with its role (request CL5) |

Nothing forces a split. The vendor adds pools when a measured need appears (fiscal latency on Black Friday, a chain that wants
isolation in its contract).

### 5.2 Many tenants on the same pools

Shared pools are the normal case: thousands of tenants on the same nodes. Provisioning scales a shared pool on **totals** (per class
backlog, pressure), never per tenant. Fairness between tenants on the same nodes is Cluster's (⚑CL11 per-tenant limits, ⚑CL28 shares
inside a class). The per-tenant load history (cluster 07 §3) shows **who** makes the peaks; that is evidence for a vendor decision
("offer FarmaViva a dedicated pool"), not an automatic split.

### 5.3 Who can grow capacity

| Who | How | Automatic? |
|---|---|---|
| **The vendor** (owner of the ecosystem) | spec changes, plans in the orchestrator | yes, through auto-apply rules (Tier 0, or the rule's ceiling); otherwise approval |
| **Signals** (Cluster backlog, busy hours) | recommendations | yes for Tier 0 under a rule |
| **A tenant** | **only through the vendor's app** (buys a plan upgrade, "dedicated capacity", an extra region). The app turns it into a plan with `RequestedFor = tenant`. The tenant never calls Provisioning and never holds a provisioning permission | yes **if** a vendor rule allows it for that tenant or contract kind ("dedicated pool up to 6 pods for contract kind `enterprise`"); otherwise a vendor approver |

Tenant-requested capacity counts against the target's limits like everything else; a tenant's contract adds a **per-tenant ceiling**
(pods of its dedicated pool, its dedicated databases) on top.

### 5.4 Tenant-dedicated pool, end to end

1. Vendor app: tenant FarmaViva bought dedicated capacity → `SetPoolSpec("br-south", "dedicated-farmaviva", roles: ["tenant-farmaviva"], 2..6)` with `RequestedFor = farmaviva`.
2. Plan → `CreatePool` (Tier 1) → the rule for contract kind `enterprise` allows it → applied, audited.
3. Orchestrator → Cluster: pin scope `farmaviva` to role `tenant-farmaviva` (request CL5); FarmaViva's workers move at safe points.
4. Optional: dedicated database through Placement (03 §4).
5. Contract ends → reverse order: unpin, pool bounds to 0, `DeletePool` (Tier 1).

## 6. When something is down

Recovery does **not** need the application servers to hold provisioning rights:

| What fails | Who recovers | Provisioning's part |
|---|---|---|
| A pod / process crashes | Kubernetes restarts or recreates it (Deployment keeps the replica count); Cluster hands its leases to other nodes (≤ ~35 s, ⚑CL7) | none |
| A VM / Kubernetes node dies | Kubernetes reschedules its pods; the cluster autoscaler or managed node pool replaces the VM | none (bounds already allow it) |
| A pool cannot reach its minimum (quota, zone outage) | — | drift + alert ("fiscal: 2 ready, min 4"); a plan to raise node-pool size or move to another zone needs approval |
| The whole region | the operator, with the platform (failover is manual, ⚑DB11) | can keep **standby capacity** as a spec and build/scale the standby region through a plan |
| Provisioning / the orchestrator itself | nothing urgent: the data plane keeps running inside the last bounds and KEDA keeps scaling | restart; runs resume (⚑PV19) |

The platform's own controllers already hold the rights to *replace* what dies, inside limits set beforehand. Provisioning changes those
limits; the application servers never do.
