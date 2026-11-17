# 11 — Work classes, priority, node roles and runtime control

> **Origin:** user, 2026-11-11: "prioritise fiscal over other functionalities by group or category; in high traffic one kind of
> processing is prioritised, through any cluster, a specific cluster, or a cluster that does only one kind of work. **The domain
> project defines the work; the cluster only allows grouping and controlling.** It must be configurable **at runtime**, so my own
> vendor API/orchestrator can handle it." Decisions ⚑CL27–⚑CL33 (README §6), all pending.
> Names here are proposals; code blocks are `// (sketch)`.

## 1. Concepts in plain words

| Term | Meaning | Example |
|---|---|---|
| **Work class** | a label the **domain** puts on a piece of work so infrastructure can treat it as a group | `fiscal`, `notifications`, `reports`, `imports` |
| **Priority** | how important a class is when there is not enough capacity for everything | `fiscal` = Critical, `reports` = Low |
| **Weighted share** | each class gets a portion of a node's worker slots in proportion to its weight; nobody gets zero | fiscal 60 %, notifications 30 %, reports 10 % |
| **Starvation** | low-priority work never runs because high-priority work always exists; avoided by a minimum share | reports keep ≥ 5 % even on Black Friday |
| **Pressure** | how overloaded a node is right now, from its own signals | Normal → Elevated → High → Critical |
| **Load shedding** | deliberately refusing or postponing low-priority work under pressure so critical work survives | reports answer 503 + `Retry-After`; fiscal keeps running |
| **Node role** / **pool** | a label on a node saying which classes it may run; a pool is the set of nodes with a role | pool `fiscal` (4 pods) only runs fiscal |
| **Work policy** | the runtime settings for all of the above, versioned, changed by commands | "Black Friday policy until 2026-11-28 23:59" |
| **Control plane** | the part that creates, scales and configures the system | Kubernetes + the vendor orchestrator |
| **Data plane** | the part that does the work | the YFex server nodes |

## 2. Who defines what

| Concern | Owner | Not the owner |
|---|---|---|
| What the work is, its class and **default** priority | **domain project** (one line at registration) | Cluster knows no domain names |
| Class registry, priority shares in the worker runner, role filter in placement, per-class metrics, the policy store and its notice | **YFex.Cluster** | |
| Limits that can change at runtime; priority-aware admission and shedding | **YFex.Resilience** (requests RE3, RE4) | Cluster does not build a second limiter |
| Class on jobs; per-class shares in the scheduler | **YFex.Jobs** (JO6) | |
| A queue per class (more consumers for fiscal) | **YFex.Messaging / YFex.Orchestration** (ME6) | |
| Class on HTTP endpoints so shedding can answer 503 | **YFex.Web** (WE5) | |
| Roles of an on-premises machine (from the module profile) | **YFex.Hosting** (HO4) | |
| Creating pools, number of copies, autoscaling | **the platform** (Kubernetes, scale sets) or a future provisioning component (§8) | not Cluster, not the nodes |
| Changing the policy at runtime | **the vendor's API/orchestrator**, through Cluster's audited admin commands | |

## 3. The four mechanisms

### 3.1 Priority inside a node (⚑CL28)

The worker runner and the Jobs scheduler hand out **slots** per pass by class:

- Each class has a **weight** and a **minimum share** (default 5 %); shares are computed over the classes that have work right now
  (an idle class gives its share to the others).
- `Critical` classes are served first up to their share, then the rest by weight (**weighted fair queuing**, deficit round-robin).
- Per-tenant fairness (⚑CL11) applies **inside** a class: fiscal of tenant A does not starve fiscal of tenant B.
- A class may have a `MaxConcurrentPerNode` cap (a report class that must never take more than 2 slots).

### 3.2 Load shedding (⚑CL29)

- Each node computes a **pressure level** every second from its own signals: thread-pool queue length, CPU, request p99 latency
  (Logging's `yfex.operation.duration`), worker slots in use, oldest backlog age. Thresholds come from the policy.
- The policy maps levels to actions: `Elevated` → pause `Background` classes at their next safe point; `High` → also `Low`, and HTTP
  endpoints of those classes answer **503 + `Retry-After`**; `Critical` → also `Normal`. **`Critical` classes are never shed**
  (validation refuses such a policy).
- Paused workers keep their leases for a grace period (no handover storm), then release them so another, less loaded node can take
  them.
- Resilience owns the **admission gate** (RE4); Cluster supplies the pressure level and the class of the work.

### 3.3 Node roles and pools (⚑CL30)

- A node announces **roles** at start (from configuration, an environment variable in Kubernetes, or the Hosting module profile on
  premises). No roles = the node takes every class (the default; a one-node central needs nothing).
- The policy maps **class → allowed roles**; placement (⚑CL10) only ranks nodes whose roles match. The lease stays the truth.
- **Runtime override:** the vendor can add or remove a role on a node or on all nodes with a label (`cluster.node_roles`). The node
  hands over the classes it lost at a safe point, like a drain limited to those classes; it does not restart.
- Shapes this supports: one cluster where every node does everything (default); one cluster with pools (`fiscal`, `general`);
  a cluster that does **only** one kind of work (all nodes have the role `fiscal`, and the policy gives no other class a role there).

### 3.4 Per-class metrics and capacity hints (⚑CL31)

| Instrument | Dimensions | Use |
|---|---|---|
| `yfex.cluster.work.backlog` | class, role | KEDA/HPA scale the pool of that role |
| `yfex.cluster.work.oldest_age` | class, role | scale on lateness, alarms |
| `yfex.cluster.work.running` | class | dashboards |
| `yfex.cluster.work.shed` | class, level | how often shedding happened |
| `yfex.cluster.pressure` | node | alarm; scale-out trigger |

- Classes and roles are bounded (at most **32 classes, 16 roles** per cluster; registration refuses more) so metric cardinality stays
  small. No tenant dimension (per-tenant data stays in the load history, 07 §3).
- Backlogs come from owners (`IClusterWorkScopes` counts, Jobs due counts) and are reported by one node per cluster (07 §2 pattern).
- **Capacity hints** (later): Cluster can write "pool `fiscal` wants 6 nodes" to `cluster.capacity_hints` from backlog and history.
  It never acts on them; the platform or the provisioning component (§8) may.

## 4. Runtime control (⚑CL32)

### 4.1 Layers (later wins)

1. **Code defaults** — the domain's declaration (`fiscal` = Critical).
2. **App configuration** — appsettings / environment at start.
3. **Cluster policy** — versioned row in the shared database, changed at runtime.
4. **Scope overrides** — per tenant (a premium tenant's reports at Normal instead of Low).
5. **Temporary overrides** — with an expiry ("Black Friday until 2026-11-28 23:59 BRT"); they disappear by themselves.

### 4.2 Change flow

```
vendor orchestrator ──SetWorkPolicy (Cqrs command, permission cluster.operate, audited)──▶ any node
    validate (Validation rules) ─▶ write cluster.work_policy version+1 ─▶ notice cluster.policy.changed
every node ─▶ re-read the version (≤ 1 s by notice, ≤ 30 s by polling) ─▶ apply at the next pass / next request
```

- **Validation** before accept: weights > 0; minimum shares sum ≤ 100 %; no Critical class shed; every class has at least one role
  that some Ready node has (otherwise the command warns and needs `force`); unknown class names refused.
- **Preview**: `PreviewWorkPolicy` returns what would change (which classes pause at which level, which nodes lose roles).
- **Rollback**: `RevertWorkPolicy(version)` writes the old content as a new version (history kept; retention as Audit decides).
- Every change is an audit entry (who, from which orchestrator, old and new version).
- Nodes that cannot read the policy keep the **last good version** in memory; a node started with no database access uses layers 1–2.

### 4.3 Where the vendor orchestrator calls

| Place | How the policy arrives |
|---|---|
| Vendor cloud | the orchestrator calls the cluster's admin API (Cqrs over Messaging RPC or HTTP) per region cluster |
| On-premises central | the vendor cannot call in directly; the policy travels **signed** on the existing vendor → central channel (Licensing check-in or the Hosting module profile, ⚑CL32 open point), and the central applies it locally; the customer's operator may override within limits |
| Shop site | priority applies locally on each terminal (shares, shedding); no roles, no remote policy in v1 |

## 5. Code sketches

```csharp
// (sketch) — domain declares; Cluster only carries the label.
services.AddYFexCluster(cluster =>
{
    cluster.WorkClass("fiscal", c => c.Priority(WorkPriority.Critical).Weight(60).NeverShed());
    cluster.WorkClass("notifications", c => c.Priority(WorkPriority.High).Weight(30));
    cluster.WorkClass("reports", c => c.Priority(WorkPriority.Low).Weight(10).MaxConcurrentPerNode(2));

    cluster.WorkerPerScope<SefazSender, PendingFiscalScopes>("fiscal.sefaz.send", w => w.Class("fiscal"));
    cluster.Roles(builder.Configuration["YFEX_NODE_ROLES"]); // "fiscal,general"; empty = everything
});

// (sketch) — Jobs (JO6) and Web (WE5) use the same class names.
[Job, Cron("0 3 * * *"), WorkClass("reports")]
public sealed partial class MonthlySalesReport { /* … */ }

app.MapGet("/reports/sales", Handler).WithWorkClass("reports");
```

```csharp
// (sketch) — model
public enum WorkPriority : byte { Background = 1, Low = 2, Normal = 3, High = 4, Critical = 5 }
public readonly record struct WorkClassId(string Value);   // validated like LeaseKey segments
public readonly record struct NodeRole(string Value);

public sealed record WorkPolicy(
    long Version,
    ImmutableArray<WorkClassPolicy> Classes,
    ImmutableArray<SheddingStep> Shedding,      // pressure level → priorities paused
    PressureThresholds Thresholds,
    ImmutableArray<ScopeOverride> ScopeOverrides,
    DateTimeOffset? ExpiresAt);

public readonly record struct WorkClassPolicy(
    WorkClassId Class, WorkPriority Priority, int Weight, int MinSharePercent,
    int? MaxConcurrentPerNode, ImmutableArray<NodeRole> Roles);
```

```csharp
// (sketch) — vendor orchestrator
await commands.SendAsync(new SetWorkPolicy(
    Cluster: "br-south",
    Patch: WorkPolicyPatch.Weight("fiscal", 80).Shed(PressureLevel.Elevated, WorkPriority.Low),
    ExpiresAt: blackFridayEnd));
await commands.SendAsync(new SetNodeRoles(Cluster: "br-south", Label: "pool=general", Add: ["fiscal"]));
```

`WorkPriority` and `PressureLevel` are enums (no data per case; same reasoning as `ClusterNodeState`, ⚑CL16).

## 6. Scenarios

| Scenario | What happens |
|---|---|
| **Black Friday** in the cloud | orchestrator sets a temporary policy (fiscal weight 80, shed Low at Elevated); the fiscal backlog metric makes KEDA add fiscal pods; reports answer 503 for two hours; at expiry the normal policy returns by itself |
| **A tenant's 2-million-row import** | `imports` is Low; per-tenant fairness (⚑CL11) keeps it from blocking other tenants' imports; class shares keep it from blocking anyone's fiscal |
| **Fiscal-only cluster** | a separate deployment whose nodes all have role `fiscal`; the general cluster's policy gives fiscal no role there; the two clusters share nothing but the database plan's tenant directory |
| **Move capacity at runtime** | orchestrator adds role `fiscal` to the general pool for an evening; those nodes take fiscal tenants through placement; removing the role hands them back at a safe point |
| **On-premises, one server** | no roles; shares and shedding still protect fiscal on that one machine; the policy arrives signed from the vendor and the customer's admin can see it |
| **Shop terminal** | local shares only: an NFC-e send beats a stock-sync upload on a weak terminal |

## 7. Risks and their answers

| Risk | Answer |
|---|---|
| Starvation of low classes | minimum share; shedding only under measured pressure; shed counts are metrics |
| A bad policy takes the system down | validation, preview, expiry on temporary overrides, revert command, last good version in memory |
| Priority inversion (Low work holds a resource fiscal needs) | classes never share leases; docs tell owners not to hold a lock across a class boundary |
| Metric cardinality | at most 32 classes and 16 roles; no tenant dimension |
| Role change flapping | changes apply at safe points, at most N handovers per minute (⚑CL10 limit) |
| Remote policy as an attack path on premises | signed by the vendor, verified with the vendor key the central already trusts (Licensing), audited, bounded by local limits |

## 8. Creating machines (provisioning) — outside Cluster (⚑CL33)

The nodes doing tenant work must **never** hold credentials that can create or delete machines (least privilege: a bug or a breach in
the app must not be able to spin up 500 VMs). So provisioning is a **separate control-plane component**, owned by the vendor:

| Option | What it is | Fit |
|---|---|---|
| Change only the autoscaler's settings | the orchestrator calls the Kubernetes API to set min/max replicas of a Deployment or a KEDA `ScaledObject` | smallest; covers most needs |
| **Kubernetes operator** | a program that watches a custom resource (e.g. `YFexPool`: roles, min/max, class targets) and creates/updates Deployments | the standard way to automate Kubernetes |
| Infrastructure as code from .NET | Pulumi (has a .NET SDK) or Terraform runs triggered by the orchestrator: node pools, databases, regions | new regions, new customers' dedicated clusters |
| Cloud SDKs directly | Azure/AWS SDK calls from the orchestrator | vendor-specific; last resort |

Proposal: a later design plan for a provisioning library (name proposal **`YFex.Provisioning`**, provider seam: Kubernetes first, cloud
SDK/Pulumi later, on-premises = Hosting profiles), consuming Cluster's capacity hints and metrics. Not part of any Cluster phase.

**2026-11-11:** that plan is [provisioning](../provisioning/README.md) (⚑PV1–⚑PV27, all pending). It starts with the first option of the
table (bounds of existing KEDA/HPA autoscalers, dry run first), then the operator (`YFexPool`), then Pulumi for node pools, databases
and regions. Its requests to Cluster (read-only role on capacity hints and load history, role coverage query, roles and region from the
environment) are in [provisioning 09 §2](../provisioning/09-phases-and-acceptance.md).
