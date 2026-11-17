# 09 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules (every phase)

- **No code before the user confirms ⚑PV1–⚑PV27** (README §6). Each phase starts by re-reading the current source of the libraries it
  touches (current source wins).
- `C:\Dev\YFex` is not a git repository at the root; each `YFex.*` sibling is its own Git root. Commit on `main` of the repo you
  changed, only when the user asks. Before touching an existing file in another repository, copy it to
  `C:\Temp\provisioning-backup\<phase>\` with the same relative path.
- Never edit another library's code to land an adoption: the requests in §2 are done **by their owners**.
- RoslynMcp tools for C# exploration; `dry_run_compile` before non-trivial edits; the `yfex-csharp` skill.
- **No third-party package without asking** (the Kubernetes client is decided by the PV-P0 spike; Pulumi only in PV-P5).
- Build with `--artifacts-path C:/Temp/provisioning-artifacts`; AOT smoke at 0 IL warnings for every package except `.Pulumi`;
  tests on Windows and Linux (`linuxKit`); the Kubernetes suite against **kind** in Docker through `linuxKit`. **Never** run tests
  against a real cloud account or a shared cluster.
- Record test counts before/after in §5 and keep a memory file (`yfex-provisioning.md`) current.

## 2. Requests to other libraries (owners decide the shape)

| # | Owner | Request | Why | When |
|---|---|---|---|---|
| CQ1 | YFex.Cqrs | confirm that an `IDecisionBound` command whose rule verdict is `Allowed` needs no approval token, and that a `NeedsApproval` with two requests needs two distinct approvers (conditional approval through Rules, ⚑PV14 and ⚑PV24; replaces the earlier "predicate on `RequireApproval`" request) | Tier 0 plans apply without a token; two-person Tier 2 | PV-P1 |
| AZ1 | YFex.Authorization | **N distinct approvers** for one permission on one digest | Tier 2 two-person approval (06 §2.2) | PV-P5 |
| AZ2 | YFex.Authorization | confirm whether an `IApprovalPolicySource` may raise tiers per action kind | vendor tightens approval without code | PV-P2 |
| CL1 | YFex.Cluster | `cluster.capacity_hints` and load history readable with a **read-only role**; per-class backlog metric names stable (⚑CL31). **Also schedule the capacity hints in a Cluster phase** (gap G1, §3.1) | Provisioning's signals bridge | PV-P3 |
| CL2 | YFex.Cluster | `YFEX_NODE_ROLES` (or the agreed variable) and node `Region` from the environment at start | pools set roles and region through env (⚑CL30, finding F1) | PV-P4 |
| CL3 | YFex.Cluster | **design and schedule** a read-only "role coverage" query: for a region, Ready nodes per role and classes per role (gap G3, §3.1) | scale-in safety (05 §4) | PV-P3 |
| CL4 | YFex.Cluster | a load summary for **one-node centrals on the in-memory engine** (today history is disabled there, cluster 07 §3), or a documented rule that centrals wanting recommendations use the SQL engine (gap G2, §3.1) | on-premises recommendations (⚑PV10) | **parked** (PV-P7 deferred) |
| CL5 | YFex.Cluster | pin a **scope (tenant) to a role**, so a tenant-dedicated pool runs only that tenant's work (today a tenant can be pinned to a *node* by configuration only, cluster 04 §1) | tenant-dedicated pools (⚑PV23, 05 §5.4) | PV-P4 |
| TN1 | YFex.Tenancy.Registry | a query of registrations in `NeedsRegion` with their residency rule; a command to record a region closed (`OpenForNewTenants = false`) | region recommendations and the region run (07 §3) | PV-P6 |
| DB1 | YFex.Data.Placement | an admin command to record a new `DatabaseId` (Dedicated or pool) with a credential **reference**; metrics for "database near full" | dedicated database flow (03 §4) | PV-P5 |
| HO1 | YFex.Hosting | HO4: roles in the module profile; signed profile delivery (shared open point with ⚑CL32) | on-premises recommendations become profiles | **parked** (PV-P7 deferred) |
| LC1 | YFex.Licensing | carry the central's load summary on check-in (or name another channel) | on-premises signals | **parked** (PV-P7 deferred) |
| AU1 | YFex.Audit | nothing new; confirm descriptor naming `provisioning.*` | audit events (06 §3) | PV-P1 |
| OR1 | YFex.Orchestration | confirm `KeyValueSagaStore` over a **shared** key-value store (YFex.Persistence.Data) is safe with several orchestrator nodes (concurrency on the saga version); durable timeouts of hours (Pulumi, temporary bounds) | runs as process managers (⚑PV19) | PV-P0 check, PV-P1 |
| JB1 | YFex.Jobs | none new: drift, signal and recommendation passes as `[Interval]` jobs (⚑PV27) | periodic passes | PV-P3 |

## 3. Dependencies

Checked 2026-11-12 against the other designs' status lines and phase files.

| Phase | Needs from others | Blocking? | Workaround |
|---|---|---|---|
| PV-P0 | kind in the WSL Docker (`linuxKit`) | no; the spike verifies kind itself | — |
| PV-P1 | core, Cqrs (with Validation and Rules), Orchestration, Jobs, Authorization.Public, Audit.Public, Data, Persistence.Data, Actors (all built; OR1 to confirm); core `RegionId` (Tenancy ⚑TN3, **pending**, TN-P1) | soft | a temporary `RegionId` inside Provisioning, replaced when core has it |
| PV-P2 | kind + KEDA | no | pools scale on CPU until Cluster metrics exist |
| PV-P3 | Cluster **CL-P4** (load history `cluster.load_hourly`, per-class metrics, roles from configuration), which follows CL-P1…P3; ⚑CL10–12, CL15–17 under discussion, ⚑CL27–CL32 pending; gaps G1, G3 | **hard** for signals and scale-in safety | split: drift, limits, rules and scheduled bounds entered by hand can ship before Cluster; signals and role-coverage checks wait |
| PV-P4 | Cluster lease for the reconciler (CL-P1/P2); drain ⚑CL16 (CL-P3); roles env (CL-P4, optional); the CRD API group (user) | soft with one orchestrator node; **hard** for safe scale-in | one orchestrator node; scale-in Tier 1 until drain exists |
| PV-P5 | approval of the Pulumi package (user); a vendor **sandbox cloud account** (user); Authorization AZ1; Placement record command DB1 — ⚑DB decided 2026-11-11, `YFex.Data.Placement` from **DB-P3**, several databases from **DB-P5**, not built | **hard** on the account and package | two approval permissions instead of AZ1; the orchestrator records the database manually until DB-P5 |
| PV-P6 | Tenancy **TN-P3** (regions, `NeedsRegion`; ⚑TN1–TN18 pending); Cluster CL-P2 (schema for the new region's coordination database); YFex.Files per-region stores (⚑DB27; Files ⚑F2–F22 pending, no repo) | **hard** | regions built by runbook until then (⚑PV9 alternative) |
| PV-P7 (**deferred**, user 2026-11-12) | Hosting **H1** + HO4 (no code; ⚑H8, H11–H22 pending); signed delivery channel (⚑CL32 open point); Licensing LC-P3/P4 check-in (`YFex.Licensing.Vendor` not built); gap G2 | **hard** | recommendations by vendor support from data the customer sends by hand |

### 3.1 Gaps found in other designs (2026-11-12)

| # | Gap | Where | Effect on Provisioning | Request |
|---|---|---|---|---|
| G1 | **Capacity hints are not in any Cluster phase.** Cluster 11 §3.4 says "later"; CL-P0…CL-P9 never schedule `cluster.capacity_hints` | [cluster 10 §4](../cluster/10-phases-and-acceptance.md), [cluster 11 §3.4](../cluster/11-work-classes-priority-and-control.md) | PV-P3 can only use load history and metrics until a phase adds them | CL1 |
| G2 | **A one-node on-premises central has no load history**: the in-memory engine disables it ([cluster 07 §3](../cluster/07-scaling-and-storage.md)) | cluster 07 | PV-P7's recommendations have no input for the most common central | CL4 |
| G3 | **The "role coverage" query is not designed in Cluster** (Ready nodes per role, classes per role for a region); only this plan's request names it | cluster 02 / 11 | PV-P3/P4 scale-in safety (05 §4) has nothing to call | CL3 |

## 4. Phases

### PV-P0 — Spikes (no product code)

- Own minimal Kubernetes client vs `KubernetesClient(.Aot)`: patch a `ScaledObject` with server-side apply and `dryRun=All`, read the
  scale subresource, watch a custom resource for 10 minutes with reconnects, exec-plugin auth with a kubeconfig, AOT publish linux-x64.
- KEDA in kind: bounds change takes effect; HPA generated by KEDA reflects them; field-manager conflict with Helm is detectable.
- Saga store (OR1): two processes sharing `KeyValueSagaStore` over YFex.Persistence.Data on Postgres; a step committed by one is not
  repeated by the other; a durable timeout of 1 h fires once after a restart.
- **Acceptance:** a short note in this folder (`spike-p0.md`) with numbers (AOT warnings, binary size, lines of client code) and the
  ⚑PV7 recommendation confirmed or changed.

### PV-P1 — Contracts, planner, rails (no provider)

- Model (02) with `[Validatable]` specs, planner, hard limits, freeze, shadow mode, `ProvisioningSituation` + default rule set (YFex.Rules),
  runs as Orchestration process managers, permissions catalog, audit descriptors, Cqrs
  commands/queries, `YFex.Provisioning.Testing` with the fake provider; `YFex.Provisioning.Data` store.
- **Acceptance:** with the fake provider: a plan above a limit is refused and audited; a Tier 1 plan without an approval is refused, and
  with an approval for another digest is refused; a plan applied after expiry is refused; a run killed after step 1 resumes at step 2
  without repeating step 1; a freeze set mid-run pauses after the current step; in Shadow nothing reaches the provider and audit says
  "would apply"; plan digest golden test.

### PV-P2 — Kubernetes bounds provider + orchestrator API

- `.Kubernetes` provider (04 §1) with the client chosen in PV-P0; RBAC sample; `SetScaleBounds`, `SetTriggers`, `SetReplicas` (no
  autoscaler only), observation, dry run, verify; approvals bound to the digest through Cqrs.
- **Acceptance (kind + KEDA):** raising `fiscal` 4..12 → 4..16 (Tier 0) applies and verifies; 4..24 needs one approver from another
  person; `SetReplicas` on a KEDA pool is refused at plan time; a hand edit of `maxReplicaCount` shows as drift within one pass; a
  change outside the namespace allowlist never reaches the API server (audit shows the refusal); the provider identity cannot create a
  Deployment (RBAC test); AOT smoke 0 warnings.

### PV-P3 — Signals, recommendations, auto-apply, drift, scale-in safety

- `YFex.Provisioning.Cluster` signals; recommendations with evidence; auto-apply rules (Tier 0 only); scheduled temporary bounds;
  drift pass; scale-in checks (05 §4).
- **Acceptance:** a busy-hour profile produces a temporary minimum that starts and ends on time across a DST change (IANA zone); a
  capacity hint above the auto ceiling becomes a recommendation, not an apply; lowering bounds that would leave work class `fiscal` with
  no Ready node is refused naming the class; rate limits stop a rule after N changes; drift adopt creates a new spec version.

### PV-P4 — `YFexPool` operator

- CRD `v1alpha1`, reconciler, owner references, status, PDB, role/region env, Cluster lease for the reconciler.
- **Acceptance (kind):** creating a pool spec (Tier 1, approved) creates Deployment + `ScaledObject` + PDB; deleting it deletes the
  children; a hand edit of a child is reverted only when the rule allows, else reported; two orchestrator nodes never reconcile the same
  namespace at once.

### PV-P5 — Pulumi worker: node pools, databases, buckets

- Worker image, Automation API driver, programs for node pool / managed Postgres / bucket in **one** cloud (the vendor's first),
  separate delete identity, two-person approval (AZ1).
- **Acceptance (vendor sandbox account, never production):** `CreateDatabase` previews, needs two approvers, creates with PITR on,
  returns a credential reference, and the orchestrator records it in Placement; a delete without the delete identity fails; an
  interrupted `up` resumes.

### PV-P6 — New region run

- `RegionSpec`, the ordered run of 07 §3, Registry record (closed), shadow period.
- **Acceptance (sandbox):** a full region in the sandbox account from one approved plan; the region appears closed in the registry;
  opening it is a separate command; destroying the sandbox region is a Tier 2 plan.

### PV-P7 — On-premises recommendations and Hosting profiles (DEFERRED)

> **User, 2026-11-12:** cloud only for now. Kept for later; see README §9. Not part of the current phase order.

- Recommendations from check-in summaries; profile edits as Tier 1 plans; signed delivery (HO1, LC1).
- **Acceptance:** a central with a synthetic busy Saturday gets a "second server" recommendation; a profile change is verified by the
  central's Hosting and applied at a safe point; a profile with a bad signature is refused and reported.

## 5. Implementation log

(empty — no phase started)
