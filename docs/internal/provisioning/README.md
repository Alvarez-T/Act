# YFex.Provisioning — creating, scaling and removing server pools, databases and regions from the vendor's control plane — Design

> **Status:** DESIGN 2026-11-11. Docs only: no code, no repo. **Every decision ⚑PV1–⚑PV27 (§6) waits for the user.**
> **Scope (user, 2026-11-12): vendor cloud only, for now.** On-premises centrals and shop sites are out of scope; what was designed
> for them is kept in §9 for later, when really needed. Not tied to one cloud provider (user, 2026-11-12).
> **Origin:** 2026-11-11, while designing [Cluster 11](../cluster/11-work-classes-priority-and-control.md) (work classes, pools per node
> role, runtime policy from the vendor orchestrator). ⚑CL33 keeps **machine creation out of YFex.Cluster**: the nodes that do tenant work
> never hold credentials that create machines. The user asked **why YFex libraries cannot create machines and how to automate it**; this
> plan answers both (§2.1 and the rest of the folder).
> **Repo (proposed):** `C:\Dev\YFex\YFex.Provisioning` (sibling, template YFex.Time / YFex.Networking; own `provisioning.slnx`) (⚑PV1).
> **Audience:** the user first; then the sessions that implement the phases ([09](09-phases-and-acceptance.md)) and the owners of Cluster,
> Tenancy.Registry, Data.Placement, Hosting, Authorization and Audit.
> **Normative vs sketch:** every name here is a **proposal** until the user approves §6; code marked `// (sketch)` is illustration only.
> Names of existing YFex code were checked against source on 2026-11-11 ([01](01-current-state-and-inventory.md)).
> **Start small:** the first useful phase only changes the **bounds** of autoscalers that already exist (min/max replicas of a Kubernetes
> Deployment or KEDA `ScaledObject`), in **dry run** first. Operators, node pools, databases and regions come later (§6 ⚑PV22).

## 1. What it is, in one paragraph

Cluster makes several copies of a server behave as one, and tells the world how loaded each **pool** (nodes with a role, ⚑CL30) is.
Something still has to **create** those copies: decide that the `fiscal` pool needs 4 to 12 pods instead of 2 to 6 before Black
Friday, create a new pool for a big customer, add a Kubernetes node pool, create a dedicated database, and one day build a whole new
region. **YFex.Provisioning is that something, on the vendor's side only**: a small control-plane library that keeps a **desired state**
(pools, their roles and bounds, databases, regions), compares it with what the platform reports, produces a **plan**, checks it against
**limits**, gets the **approvals** the plan's risk needs, applies it through a **provider** (Kubernetes first), **audits** every step and
reports drift. It is composed by the vendor's own **orchestrator app**, never by the nodes that do tenant work.

## 2. What it is not

| Not | Who does it | Provisioning's part |
|---|---|---|
| **Coordinating nodes** (leases, single worker, drain, roles at runtime) | YFex.Cluster | starts nodes with their roles (pool spec → `YFEX_NODE_ROLES`); scale-in respects Cluster's drain (05 §4) |
| **Second-by-second autoscaling** (add a pod because the backlog grew) | the platform: KEDA / HPA / cloud scale sets | sets their **bounds** and triggers; never fights them over `replicas` (⚑PV13) |
| **Deploying a new app version** (images, rollouts, migrations) | the vendor's CI/CD (GitOps or pipeline) + Cluster rolling upgrades (⚑CL20) | pools reference a release chosen by CD; Provisioning does not roll out versions in v1 (⚑PV2) |
| **Choosing a tenant's region or database**, moving tenants | YFex.Tenancy.Registry (region, residency, ⚑TN5–⚑TN7), YFex.Data.Placement (database, moves, ⚑DB7, ⚑DB9) | creates the **capacity** they choose from (a database, a region) and records it through **their** admin commands (07) |
| **Runtime work policy** (class weights, shedding, roles at runtime) | YFex.Cluster `SetWorkPolicy` / `SetNodeRoles` (⚑CL32) | none; the orchestrator calls both libraries |
| **Backups, replication, failover** | the database/platform + YFex.Backup ([databases-and-backups](../databases-and-backups/README.md)) | creates a database with the platform's backup/PITR switched on; never restores |
| **On-premises centrals and shop sites** | the customer's IT; Hosting module profiles | **out of scope for now** (user 2026-11-12); deferred design in §9 |
| **Billing customers** | the vendor app + YFex.Licensing meters | reports resource use per pool/region; no `Money` in contracts (⚑PV17) |

### 2.1 Why YFex libraries (and the nodes) cannot create machines

| Reason | What would go wrong |
|---|---|
| **Least privilege** | every node runs tenant code, parses tenant input and talks to the internet. A credential that can create VMs on every node means one bug, one leaked environment variable or one compromised dependency can create 500 VMs (cost attack, crypto mining) or **delete** the database servers. |
| **Blast radius** | a node is short-lived and many; the provisioning credential must be in **one** place, small, audited, and switched off with one flag (freeze, ⚑PV17). |
| **Chicken and egg** | a node cannot rebuild the cluster it runs in, a dead region, or the coordination database it needs to start. The control plane must live outside what it creates. |
| **Two controllers fighting** | if nodes scaled their own pool and KEDA also did, replicas would flap. One owner per knob (⚑PV13). |
| **Different lifecycle and people** | tenant work changes every second and is driven by customers; infrastructure changes a few times a day, costs money, and is approved by the vendor's operators. |
| **On-premises** | the customer owns the machines; the vendor has no right and no credential to create servers there (⚑PV10). |

So the rule (⚑CL33, ⚑PV11): **libraries that run on nodes publish signals; one vendor-side component reads them and changes infrastructure.**

## 3. Shape and packages

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Provisioning` | model (`ProvisioningTarget`, `PoolSpec` (a "pool" = any scalable workload, optional labels), `DatabaseSpec`, `RegionSpec`, `DesiredState`, `ObservedState`, `ProvisioningPlan`, `ProvisioningAction`, `ProvisioningLimits`), **provider seam** `IProvisioningProvider` + `ProviderCapabilities`, the planner (diff), hard-limit check + freeze, **`ProvisioningSituation` + default rule set** on YFex.Rules (⚑PV24), `[Validatable]` specs on YFex.Validation (⚑PV26), **runs as YFex.Orchestration process managers** (⚑PV19), periodic passes as YFex.Jobs (⚑PV27), dry run / shadow mode, `IProvisioningSignals` (read side), `IProvisioningCostEstimator` (optional, app-supplied), Cqrs commands and queries (`ApplyProvisioningPlan` is `IDecisionBound`), `ProvisioningPermissions`, audit descriptors, Meter `YFex.Provisioning`, `AddYFexProvisioning`. **No reference to YFex.Cluster** (⚑PV25) | YFex core, YFex.Cqrs (brings YFex.Validation and YFex.Rules), YFex.Orchestration, YFex.Jobs, YFex.Authorization.Public, YFex.Audit.Public, `Microsoft.Extensions.Hosting.Abstractions` |
| `YFex.Provisioning.Data` | SQL store over YFex.Data (`provisioning.specs`, `.plans`, `.runs`, `.limits`, `.freezes`), schema through `SchemaInstaller` | the above + YFex.Data |
| `YFex.Provisioning.Kubernetes` | provider: bounds on Deployments and KEDA `ScaledObject`s (scale subresource, server-side apply, `dryRun=All`), observation, PodDisruptionBudget check; **own minimal API client** over `HttpClient` + System.Text.Json source generation (⚑PV7) | the above |
| `YFex.Provisioning.Kubernetes.Operator` (later, PV-P4) | a hosted reconciler for the `YFexPool` custom resource (⚑PV8): creates/updates Deployment, `ScaledObject`, PDB, Service, env roles | the above |
| `YFex.Provisioning.Cluster` (bridge, optional) | the only YFex.Cluster-aware piece: maps workload labels to Cluster roles; `IProvisioningSignals` over Cluster's **capacity hints** and **load history** tables with a **read-only** database role; role-coverage and tenant-pinning calls; drain-aware scale-in hints | the above + YFex.Cluster (+ YFex.Data) |
| `YFex.Provisioning.Rules.Dynamic` (bridge, optional; only if the app cannot wire it alone) | runtime-editable provisioning rules through YFex.Rules.Dynamic (JSON specs validated against `ProvisioningSituation`) | the above + YFex.Rules.Dynamic (Expressions, Persistence) |
| `YFex.Provisioning.Pulumi` (later, PV-P5) | provider for node pools, managed databases, buckets and regions through the Pulumi Automation API, **in a separate worker process** (⚑PV9) | the above + Pulumi Automation API (third-party, Apache-2.0, needs the Pulumi CLI) |
| `YFex.Provisioning.Testing` | `ProvisioningHarness`: fake provider (records calls, fails on demand, simulates slow creates and drift), fake clock, fake signals | the above + YFex.Time.Testing |
| tests, AotSmoke, Benchmarks | `.Tests` (Windows + Linux), Kubernetes suite against **kind** in Docker through `linuxKit`, `.AotSmoke` (0 warnings) for every package except `.Pulumi` | |

**No `.Public` package** (⚑PV1): only the orchestrator app and the operator reference it. **No OS packages**: the control plane runs as
a Linux container in the vendor cloud (Windows for development). **No core types**: `RegionId` comes from the Tenancy plan (⚑TN3);
Provisioning adds nothing to core.

## 4. Where it runs (main design: [07](07-regions-and-places.md))

**Vendor cloud only** (user, 2026-11-12). Provisioning creates and sizes pools, node pools, databases and regions through providers
(Kubernetes first, later Pulumi or a cloud SDK), with credentials that exist only in the vendor's control plane. Any cloud provider;
the provider seam (⚑PV5) stays.

On-premises centrals and shop sites: **out of scope for now**. The customer owns those machines and the vendor has no credential there;
the earlier idea (recommendations + signed Hosting module profiles) is kept in §9 so it can be picked up when really needed.

**Pools are optional.** One pool with no roles (every node does every class) is the default and needs no pool spec beyond its bounds;
separate pools (`fiscal`, `general`, a tenant's own) are added only when the vendor wants them (⚑PV23).

## 5. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Control plane** | the vendor-side programs that create and configure the system; **data plane** = the nodes doing tenant work | the vendor orchestrator + Provisioning |
| **Vendor orchestrator** | the vendor's **own app** (not a YFex library) that composes Provisioning, Cluster admin, Tenancy.Registry admin and Licensing.Vendor and exposes them to the vendor's operators. Not YFex.Orchestration (sagas) and not YFex.Automation's `AutomationOrchestrator` | `vendor-ops` service |
| **Provider** | the code that talks to one platform: Kubernetes, Pulumi, a cloud SDK | `KubernetesProvider` |
| **Target** | one place a provider acts on: provider + region + account/context + namespace allowlist | `k8s:br-south/prod` |
| **Pool** | a set of identical nodes with the same Cluster roles (⚑CL30), one Deployment in Kubernetes | pool `fiscal` in `br-south`: roles `fiscal`, 4–12 pods |
| **Spec** / **desired state** | what the vendor wants to exist, versioned | `PoolSpec(fiscal, min 4, max 12)` version 9 |
| **Observed state** | what the provider reports now; never stored as truth, always re-read | Deployment `fiscal`: 5 ready, KEDA bounds 2–6 |
| **Plan** | the ordered list of actions that turns observed into desired, with a **digest** | "raise `fiscal` max 6 → 12 (Tier 0)" |
| **Action** | one idempotent change a provider can apply and verify | `SetScaleBounds(fiscal, 4, 12)` |
| **Risk tier** | how much approval an action needs: Tier 0 automatic, Tier 1 one approver, Tier 2 two approvers | create database = Tier 2 |
| **Dry run** | the plan is computed and sent to the provider's own dry-run, nothing changes | `kubectl … --dry-run=server` equivalent |
| **Shadow mode** | a target where every plan is recorded but never applied (for trying signals and policies) | a new region's first week |
| **Drift** | observed differs from desired without a plan (someone ran `kubectl scale`) | max is 20, spec says 12 |
| **Limits** | hard caps in resource units and change rates; plans beyond them are refused or need approval | ≤ 40 pods per region, ≤ +8 pods per 10 min |
| **Freeze** | a switch that stops every apply (global, per target, per pool); plans still compute | incident in progress |
| **Capacity hint** | Cluster's "pool `fiscal` wants 6 nodes" (⚑CL31), read-only input | from backlog + history |
| **Recommendation** | a plan proposed from signals, waiting for a person or an auto-apply rule | "Black Friday: min 4 from 18:00" |

Pharmacy words (FarmaViva, SNGPC, fiscal) appear **only in examples**. The library knows targets, pools, roles, databases, regions,
limits and plans; work-class names are opaque labels from Cluster.

## 6. Decisions to confirm (⚑PV)

Prefix **⚑PV** (checked 2026-11-11 across all `docs/` trees: unused; ⚑P is Privacy, ⚑PR unused but too close to "PR").

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **PV1** | Where does it live, which packages, which name? | Sibling repo **`C:\Dev\YFex\YFex.Provisioning`** with the packages of §3; no `.Public`, no OS packages, no core types. Keep the name **`YFex.Provisioning`** (the ops term the vendor's operators know) and always qualify the two existing meanings: **installation provisioning** (`InstallationProvisioning`, enrollment) and **tenant provisioning** (`ITenantProvisioningStep`, Tenancy.Registry) | **`YFex.Fleet`** (no clash with the two existing meanings; a less standard word); inside YFex.Cluster (breaks ⚑CL33: nodes would carry the code and the temptation to carry the credential); inside the vendor app only (no tests, no reuse for a second product) | §3, 01 §3 |
| **PV2** | Boundaries | Provisioning changes **capacity**: scalable workloads ("pools") and their bounds, node pools, databases, buckets, regions. It does **not** coordinate nodes, autoscale second by second, roll out versions, place tenants, set work policy or back up (§2). **Reusable by any project**: nothing in the core names YFex servers, tenants or Cluster (⚑PV25) | also roll out versions (duplicates CI/CD and Cluster's rolling-upgrade gate ⚑CL20); also place tenants (a third placement path beside Registry and Placement) | §2 |
| **PV3** | Who calls it? | Only the **vendor orchestrator app**, through **Cqrs commands and queries** (`PlanProvisioning`, `ApplyProvisioningPlan`, `FreezeProvisioning`, `GetPools`, …) with permissions `provisioning.view / plan / apply / approve / freeze / limits`. Three triggers: an operator in the orchestrator UI, an orchestrator rule (schedule, signal), and later the `YFexPool` resource (⚑PV8). Nodes never call it | an HTTP API of its own (bypasses the Cqrs pipeline, approvals and audit that already exist); nodes calling it to ask for capacity (puts a provisioning client and credential path on the data plane) | 03 §1, 06 §2 |
| **PV4** | Model | **Declarative**: versioned specs (desired state) → planner diffs against observed state read live from the provider → **plan** of idempotent actions with a digest → limits → approval by tier → apply → verify → record. Every change, even "set max to 12", goes through a plan | imperative calls ("scale fiscal to 12") straight to the provider (no dry run, no diff, no drift detection, approvals per call site) | 02 §2–§4 |
| **PV5** | Provider seam | Public `IProvisioningProvider` (`ObserveAsync`, `PlanAsync`, `DryRunAsync`, `ApplyAsync`, `VerifyAsync`) + `ProviderCapabilities` flags (`ScaleBounds`, `Pools`, `NodePools`, `Databases`, `Buckets`, `Regions`); the planner refuses specs a target's provider cannot do. Order: **Kubernetes bounds → Kubernetes operator → Pulumi → cloud SDK only where Pulumi lacks** | one provider per cloud from the start (Azure + AWS SDKs: two large dependencies before the first need); Pulumi first (a heavy engine and CLI for "set max to 12") | 02 §5, 04 |
| **PV6** | Kubernetes, first step | Change only **bounds and triggers** of what exists: `minReplicaCount` / `maxReplicaCount` / trigger thresholds of a KEDA `ScaledObject`, or `minReplicas` / `maxReplicas` of an HPA; `replicas` through the **scale subresource** only for Deployments with **no** autoscaler. Server-side apply with field manager `yfex-provisioning`, `dryRun=All` for dry runs | create Deployments from step 1 (that is the operator's job, ⚑PV8; needs image, env, probes, secrets in the spec) | 04 §1 |
| **PV7** | Kubernetes API client | **Own minimal client** in `.Kubernetes`: the dozen endpoints needed (get/patch scale, server-side apply on `ScaledObject`/HPA/Deployment/PDB, list pods, watch a custom resource), `HttpClient` + STJ source generation, in-cluster service-account token or kubeconfig exec plugin → AOT-clean, no third-party package. **Spike** first: token refresh, watch reconnects, exec-plugin auth on AKS/EKS/GKE | the official **KubernetesClient** NuGet (Apache-2.0, complete, maintained; but large, reflection-based JSON, the AOT variant `KubernetesClient.Aot` covers fewer APIs; a third-party package in the core path, rule: avoid) | 04 §1.3, 08 §3 |
| **PV8** | Operator and custom resource (step 2) | A **`YFexPool`** custom resource (pool, region, roles, release reference, min/max, KEDA triggers by work class, resources, PDB) reconciled by `YFex.Provisioning.Kubernetes.Operator`, a hosted service in the orchestrator's control-plane deployment with its own service account. The CRD's API group is the vendor's domain (open). Specs in `provisioning.specs` are written into the resource; the resource's `status` reports back | **GitOps** (orchestrator commits to a repo, Argo CD/Flux apply): strong audit through git, but minutes of delay and a second state store; Helm only (no runtime control, needs a pipeline run per change) | 04 §2 |
| **PV9** | Beyond Kubernetes (node pools, databases, buckets, regions) | **Pulumi Automation API (.NET)** behind `YFex.Provisioning.Pulumi`, run in a **separate provisioning worker** (its own container and identity, Pulumi CLI inside, state in the vendor's backend), driven by the run engine. Exempt from the AOT gate (documented). **Cloud SDKs** only for gaps | **Terraform/OpenTofu** called as a process (HCL instead of C#; licence split Terraform BSL / OpenTofu MPL); **cloud SDKs** directly (one provider per cloud, no preview/diff for free); **manual** (fine until the second region: a runbook) | 04 §3 |
| **PV10** | On-premises | **Deferred (user 2026-11-12: cloud only for now).** Earlier recommendation kept in §9: no machine creation; recommendations + signed Hosting module profile | — | §9 |
| **PV11** | Least privilege and credentials | Nodes hold **no** provisioning credential (⚑CL33). Provisioning holds **one credential per target**, scoped (Kubernetes RBAC: `patch` on named resources in allowlisted namespaces; cloud role limited to a resource group / tags), **short-lived through workload identity** (AKS Workload Identity, EKS Pod Identity/IRSA, GKE Workload Identity); a static secret only where no identity exists, kept in Cryptography's `ISecretStore`. Reads of Cluster tables use a **read-only** database role. Deletes need a separate credential used only by Tier 2 runs | one cloud-admin credential for everything (one leak = everything); credentials in configuration files | 06 §1 |
| **PV12** | Signals | Read-only inputs through `IProvisioningSignals`: Cluster **capacity hints** and **load history** (⚑CL31, 07 §3), per-class metrics from the vendor's metrics backend (app-supplied adapter in v1), Tenancy `NeedsRegion` (⚑TN7), Placement capacity (databases near full). Signals produce **recommendations**; v1 auto-applies only Tier 0 bound changes that an **auto-apply rule** allows | Provisioning as the autoscaler itself (re-implements KEDA badly; slower loop); no signals (operators guess) | 05 |
| **PV13** | Who owns which knob | **One owner per knob**: KEDA/HPA own `replicas`; Provisioning owns **bounds, triggers, schedules** and everything slower (pools, node pools, databases, regions); Cluster owns roles at runtime. Scheduled capacity (busy hours from load history) becomes a **raised `minReplicaCount` with an expiry**, or a KEDA cron trigger | Provisioning sets `replicas` directly (fights KEDA; flapping); KEDA cron triggers written by hand (operators forget to remove Black Friday) | 05 §2 |
| **PV14** | Approval | Approval comes from the **rule verdict** (⚑PV24): `Allowed` → apply automatically; `NeedsApproval(ApprovalRequestList)` → those approvals (one request per approver = two-person); `Denied` → refused. The former tiers become the **default rules in code**: Tier 0 (bounds within limits, reversible) → Allowed; Tier 1 (new workload, raised limits, scale to zero, node pool) → one approver; Tier 2 (create/delete database, bucket, region; deletes; cost above a threshold) → two approvers. `ApplyProvisioningPlan` is a Cqrs **`IDecisionBound`** command, so approvals bind the rule decision over **exactly that plan** (digest); a re-plan changes it. Plans expire (default 1 h) | approvals outside YFex (chat sign-off; no binding to what is applied); hard-coded tiers (the vendor cannot change who approves what without a deploy) | 06 §2 |
| **PV15** | Audit | Every plan, approval, apply step, verify result, refusal, freeze, limit change and detected drift is an **Audit entry** (`IAuditTrail.RecordAsync` with Provisioning descriptors): actor, target, plan digest, actions, provider request ids, outcome. **No secrets, no tenant personal data** (pools and regions only) | logs only (not tamper-evident; an insider could hide a 500-VM change) | 06 §3 |
| **PV16** | Dry run and shadow mode | Every plan can be **dry-run** (provider's own server-side dry run: Kubernetes `dryRun=All`, Pulumi `preview`) and every apply runs one first. A **target** can be in **shadow mode**: plans and recommendations are recorded, never applied. **New targets start in shadow mode** | dry run as an option per call (forgotten); no shadow mode (the first auto-rule is tried on production) | 06 §4 |
| **PV17** | Cost and limits | **Hard limits in resource units** the library owns: pods per pool/region/total, vCPU and memory per region, node-pool nodes, databases, buckets, regions; **change-rate limits** (≤ +N pods per 10 min, ≤ −M per hour); **freeze** switch (global/target/pool). Optional **cost estimate** through app-supplied `IProvisioningCostEstimator` returning **cost units** the vendor defines (a decimal per period), with a budget per target: over budget → Tier 1+ approval. **No `Money` in contracts** (infrastructure rule) | budgets in `Money` (breaks the domain-neutral rule; currency conversion in a control plane); no limits (one bad signal or rule creates 500 pods) | 06 §5 |
| **PV18** | Multi-region | **One target set per region** (one cluster per region, agreed for Cluster); `RegionSpec` lists what a region needs (Kubernetes cluster, coordination database, tenant databases, buckets, DNS, pools). A **new region** is a Tier 2 run that ends by **recording** the region in Tenancy.Registry (`OpenForNewTenants = false`); opening it is a separate human command. A **dedicated database** for a big tenant is created here and recorded through Placement's admin command; Placement then moves the tenant (⚑DB9). Provisioning **never** chooses a tenant's region or database | Provisioning writes the registry and placement tables directly (two owners of the same rows); regions created by hand forever (OK for 2–3 regions; a runbook covers it) | 07 §1–§2 |
| **PV19** | State and run engine (user question 2026-11-12: "Sagas / Orchestration?") | Specs, plans, limits, freezes in **`provisioning.*` tables** (control-plane database). A **run is a YFex.Orchestration process manager** (`ProcessManager<TState>`): its steps (dry run → apply → verify), **durable timeouts** (wait for pods Ready, plan expiry, temporary-bounds end, Pulumi `up` of 10+ min), dead-lettering and the step ledger come from Orchestration instead of a second run engine. State in `ISagaStore` (`KeyValueSagaStore`) over **YFex.Persistence.Data** so several orchestrator nodes share it (to verify in PV-P0). **Compensation never deletes infrastructure by itself**: a failed region run's compensation **proposes a clean-up plan** (Tier 2), it does not run deletes. One run per target (a Cluster lease when present, else the saga's correlation key) | an own small run engine (a second saga engine in the ecosystem); state only in the provider (no history, no approvals) | 02 §6 |
| **PV20** | Drift | Every N minutes (default 5) observe each target and compare with the spec. Drift is **reported** (metric, audit, orchestrator list). Auto-repair only for Tier 0 fields with a rule; otherwise a person chooses "adopt" (spec := observed, a new spec version) or "re-apply" | always re-apply (fights an operator fixing an incident by hand); ignore (spec and reality silently diverge) | 05 §3 |
| **PV21** | Scale-in safety | Lowering bounds or deleting a pool **never** removes the last Ready node of a role that a work class needs (asks Cluster's node list read-only), checks a **PodDisruptionBudget** exists, and relies on Cluster's **drain** (⚑CL16) through the pod's termination grace; later sets the `pod-deletion-cost` hint from lease counts (Cluster README §8). Deleting a pool = Tier 1, and only after its roles moved | blind scale-in (fiscal stops for 35 s while leases expire); keep pods forever (cost) | 05 §4 |
| **PV22** | Phase order ("start small") | **PV-P0** spikes (own Kubernetes client, KEDA bounds, kind in Docker); **PV-P1** contracts + planner + limits + freeze + dry run/shadow + audit + fake provider; **PV-P2** Kubernetes bounds provider + approvals + Cqrs API; **PV-P3** signals → recommendations, auto-apply rules for Tier 0, drift; **PV-P4** `YFexPool` operator; **PV-P5** Pulumi worker: node pools, databases, buckets; **PV-P6** new-region run (PV-P7 on-premises: deferred, §9) | operator or Pulumi first (more power before the safety rails are proven) | 09 |
| **PV23** | Pools and tenant-dedicated capacity (user questions 2026-11-12) | **Pools are optional**: default one shared pool, no roles; more pools only when the vendor declares them. **Tenants never hold provisioning rights.** A tenant can only *ask* through the vendor's app (a plan upgrade, a "dedicated capacity" product); the request becomes a **plan** with `RequestedFor = tenant`. The **vendor** decides with **auto-apply rules per tenant or per contract** ("tenant X may get a dedicated pool of up to 6 pods automatically") or approves by hand. A **tenant-dedicated pool** also needs Cluster to send that tenant's work only to nodes with the pool's role (pinning a scope to a role; today Cluster pins a tenant to a *node* by configuration only, cluster 04 §1) | tenants call Provisioning directly (a tenant's bug or breach reaches infrastructure; cost attacks); no per-tenant capacity (a big chain can only be served by growing the shared pool) | 05 §5, 06 §2.3 |
| **PV24** | Rules (user 2026-11-12: reuse YFex.Rules from the start) | **YFex.Rules from the start**, in the main package (Cqrs already references it, so no `.Rules` package): a `ProvisioningSituation` (`ISituation<T>`: plan, observed state, signals, tenant/contract, limits) evaluated by a `RuleSetDefinition<ProvisioningSituation>`. Rules **decide** (Allowed / Warned / NeedsApproval / Denied, ⚑PV14) and may **plan** (derived values such as "Saturday min 8 from 09:30" become recommendations the planner turns into plans). Default rules ship in code; the vendor overrides them **at runtime** with YFex.Rules.Dynamic (optional). **Hard limits and freeze are checked in code before the rules** and rules may only tighten them | a typed policy of our own (duplicates what Rules already does, incl. approvals); rules allowed to loosen limits (a bad dynamic rule could open 500 pods) | 05 §2.3 |
| **PV25** | Reuse and pools (user 2026-11-12: "pool is not required; reused across projects") | The core scales **workloads**: any Deployment (or provider equivalent) with optional plain **labels**; zero workloads is valid (an app may use Provisioning only for databases or regions). **The core does not reference YFex.Cluster**; Cluster roles, signals, role coverage and tenant pinning live only in the `.Cluster` bridge | `PoolSpec.Roles` typed with Cluster's `NodeRole` (every user of Provisioning would need Cluster) | 02 §2 |
| **PV26** | Validation of specs | Specs, limits and targets are **`[Validatable]`** with `[Invariant]`s (min ≤ max, max ≤ target limit, names, ranges), generated validators from YFex.Validation (already through Cqrs). Validation = is the spec well formed; Rules = is the plan allowed | hand-written checks in the planner (no field errors for the orchestrator UI, duplicated per command) | 02 §2 |
| **PV27** | Periodic passes | Drift pass, signal read and recommendation pass run as **YFex.Jobs** `[Interval]` jobs in the orchestrator app (one node through Jobs' Cluster integration when present) | a `BackgroundService` per pass (no history, no Jobs observability, all orchestrator nodes run it) | 05 §3 |

## 7. Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | what exists today (file:line) and what is only designed; the word "provisioning" already in use; findings |
| 02 | [02-model-and-contracts.md](02-model-and-contracts.md) | targets, specs, observed state, plans, actions, risk tiers, limits, provider seam, runs, permissions |
| 03 | [03-api-and-examples.md](03-api-and-examples.md) | **dev-side code first**: the orchestrator's composition root, raising a pool for Black Friday, approving a plan, a `YFexPool` resource, a dedicated database, tests, cons |
| 04 | [04-providers.md](04-providers.md) | Kubernetes bounds (KEDA, HPA, scale subresource), the operator, Pulumi worker, cloud SDKs, Hosting profiles |
| 05 | [05-signals-scaling-and-drift.md](05-signals-scaling-and-drift.md) | inputs from Cluster/Tenancy/Placement, who owns which knob, scheduled capacity, drift, scale-in safety |
| 06 | [06-safety-approval-audit-cost.md](06-safety-approval-audit-cost.md) | credentials, approval tiers, audit, dry run and shadow mode, limits, cost units, freeze |
| 07 | [07-regions-and-places.md](07-regions-and-places.md) | one target set per region, new region, dedicated database, tenant placement boundaries, on-premises, shop site |
| 08 | [08-platforms-security-aot.md](08-platforms-security-aot.md) | per-platform differences, threats, privacy, AOT and third-party packages |
| 09 | [09-phases-and-acceptance.md](09-phases-and-acceptance.md) | working rules, requests to other libraries, phases PV-P0…PV-P7, acceptance, empty log |

Related: [cluster 11 §8](../cluster/11-work-classes-priority-and-control.md) (⚑CL33 origin), [cluster 07](../cluster/07-scaling-and-storage.md)
(signals, load history), [cluster README](../cluster/README.md) (⚑CL30 roles, ⚑CL31 metrics, ⚑CL32 runtime policy),
[databases-and-backups](../databases-and-backups/README.md) (⚑DB7 new tenant's database, ⚑DB9 moves, ⚑DB11 region failover),
[tenancy](../tenancy/README.md) (⚑TN5–⚑TN7 regions, `NeedsRegion`), [hosting](../hosting/README.md) (module profiles, HO4),
[authorization-consolidation](../authorization-consolidation/README.md) (approvals), [audit](../audit/README.md), [licensing](../licensing/README.md) (vendor channel).

## 8. Open questions (not blocking the decisions)

- **CRD API group and the vendor's domain** for `YFexPool` (⚑PV8): needs the vendor's DNS name; nothing in the repos names it.
- **Metrics backend**: Prometheus, Azure Monitor or another? v1 leaves the query adapter to the orchestrator app (⚑PV12). A
  `YFex.Provisioning.Prometheus` bridge only if a second app needs it.
- **Where does the orchestrator app live** (repo, product) and who builds its UI? Out of this plan; Provisioning ships commands and
  queries only.
- **Several clouds** (Azure for `br-south`, AWS for `eu-west`)? The provider seam allows it; nothing is planned until a contract asks.
- **Customer-dedicated clusters** (a large chain wants its own cluster in the vendor cloud): a `RegionSpec`-like "cell" spec reusing the
  region run? Decide when the first such contract appears.
- **GitOps later**: if the vendor adopts Argo CD/Flux, the operator could write to git instead of the API (⚑PV8 alternative); the run
  engine and approvals stay the same.
- **Cost data**: the cloud's billing export is the truth; `IProvisioningCostEstimator` is an estimate. Reconciling the two is the
  vendor app's job.

## 9. Deferred: on-premises centrals and shop sites (kept for later)

> **User, 2026-11-12:** "for now this library can be more cloud specific and not on site … let it written somewhere for later, when
> really needed." Nothing below is planned or phased. Pick it up as a new decision when an on-premises customer needs it.

| Place | What would happen | Earlier design |
|---|---|---|
| On-premises central | **Never create machines** (the customer owns them; the vendor has no credential there). Provisioning would compute **recommendations** from the central's Cluster load history ("add a second server", "move role `reports`") and the vendor would edit the central's **Hosting module profile** (roles, modules), delivered **signed** on the vendor → central channel and verified by the central | ex-⚑PV10; [04 §5](04-providers.md); [07 §4](07-regions-and-places.md); 03 §5 |
| Shop site | nothing: store PCs and handhelds belong to the customer | — |
| A customer running the central in **their own cloud** | still "on-premises" for this plan; the vendor might ship the operator + `YFexPool` CRD for the customer to run with their own approvals | [07 §4](07-regions-and-places.md) |

Requests that only served this part are parked in [09 §2](09-phases-and-acceptance.md) (HO1, LC1, CL4) and the phase as PV-P7 (deferred).
Known gap to solve first: a one-node central on Cluster's in-memory engine keeps **no load history** (09 §3.1 G2).
