# 04 — Providers

> Decisions ⚑PV5–⚑PV10. Order: Kubernetes bounds → Kubernetes operator → Pulumi worker → cloud SDKs for gaps; on-premises = Hosting
> profiles, no machine creation.

## 1. Kubernetes bounds (PV-P2, ⚑PV6)

### 1.1 What it touches

| Resource | Field(s) | When |
|---|---|---|
| KEDA `ScaledObject` (`keda.sh/v1alpha1`) | `spec.minReplicaCount`, `spec.maxReplicaCount`, trigger thresholds, `spec.advanced.horizontalPodAutoscalerConfig.behavior` | the pool is scaled by KEDA (the expected case: scaling on Cluster's per-class backlog) |
| HPA (`autoscaling/v2`) | `spec.minReplicas`, `spec.maxReplicas` | the pool is scaled by a plain HPA (CPU) |
| Deployment `…/scale` subresource | `spec.replicas` | **only** when no autoscaler targets the Deployment (⚑PV13) |
| PodDisruptionBudget (`policy/v1`) | read only in PV-P2 | scale-in safety check (⚑PV21) |
| Pods | list, read labels/annotations | observation; later the `controller.kubernetes.io/pod-deletion-cost` annotation (⚑PV21) |

### 1.2 How

- **Server-side apply** (`PATCH` with `application/apply-patch+yaml`, `fieldManager=yfex-provisioning`): Provisioning owns only the
  fields it sets; a conflict with another manager (Helm, an operator, a person) is reported, never forced, unless the plan says
  `adopt` (⚑PV20).
- **Dry run**: the same request with `?dryRun=All`; the API server runs admission and validation and returns the result without
  persisting.
- **Verify**: re-read the object; for bounds, check that the HPA KEDA generated reflects them (`status`); for replicas, wait until
  `readyReplicas` reaches the target or a timeout (reported, not failed: pods may wait for nodes).
- **Observation** joins Deployment + HPA/ScaledObject + PDB per pool by a label (`yfex.pool=<pool>`), set by the operator later or by
  the vendor's Helm chart now.

### 1.3 Client (⚑PV7)

| | Own minimal client (recommended) | Official `KubernetesClient` |
|---|---|---|
| Size | ~12 endpoints, typed models only for the fields used | full API surface |
| AOT / trimming | STJ source generation, 0 warnings expected (spike) | reflection-based; `KubernetesClient.Aot` exists with a reduced surface (spike) |
| Auth | in-cluster service-account token (projected, refreshed from file), kubeconfig with exec plugin (`kubelogin`, `aws eks get-token`, `gke-gcloud-auth-plugin`) | all of these, tested by the project |
| Watch | needed only by the operator (PV-P4): chunked JSON lines with `resourceVersion` resume | built in |
| Upkeep | YFex follows API changes (rare for the kinds used) | follows Kubernetes releases |

Spike PV-P0 decides with numbers: AOT warnings, binary size, and a working watch against kind.

### 1.4 RBAC of the provider identity (per target)

```yaml
# (sketch) — bounds only; no create/delete, no secrets, no pods/exec
rules:
  - { apiGroups: ["keda.sh"], resources: ["scaledobjects"], verbs: ["get", "list", "patch"] }
  - { apiGroups: ["autoscaling"], resources: ["horizontalpodautoscalers"], verbs: ["get", "list", "patch"] }
  - { apiGroups: ["apps"], resources: ["deployments", "deployments/scale"], verbs: ["get", "list", "patch"] }
  - { apiGroups: ["policy"], resources: ["poddisruptionbudgets"], verbs: ["get", "list"] }
  - { apiGroups: [""], resources: ["pods"], verbs: ["get", "list"] }
# bound by a RoleBinding in each allowlisted namespace only (never a ClusterRoleBinding)
```

## 2. Kubernetes operator (PV-P4, ⚑PV8)

- **Custom resource** `YFexPool` (03 §3): pool, region, roles, release, min/max, triggers by work class, disruption, grace period.
- **Reconciler**: a hosted service (`AddYFexProvisioningOperator`) in the control-plane deployment; watches `YFexPool` objects in
  allowlisted namespaces; for each, server-side applies Deployment + `ScaledObject` + PDB + Service with owner references (deleting the
  pool deletes its children); writes `status` (observed generation, ready, conditions).
- **Who writes the resource?** The run engine (a `CreatePool` / `SetScaleBounds` action becomes "apply `YFexPool`"), so approvals,
  limits and audit stay in one place. A person editing the resource directly is **drift** (⚑PV20).
- **Leader election**: the operator runs one reconciler per namespace through a Cluster lease when the orchestrator has several nodes
  (⚑CL8), not through Kubernetes `Lease` objects (same reasoning as Cluster README §8: one lease concept).
- **CRD versioning**: `v1alpha1` until PV-P4 acceptance; conversion only when a second version exists (stored format rule).
- **RBAC**: create/patch/delete for the child kinds in allowlisted namespaces; still no secrets (image pull secrets and app secrets are
  referenced by name, created by the vendor's secret tooling).

## 3. Pulumi worker (PV-P5, ⚑PV9)

| Topic | Design |
|---|---|
| What | node pools (AKS/EKS/GKE), managed Postgres servers and databases (with PITR on), buckets (with versioning/object lock where the store has it), DNS records, later whole regions (⚑PV18) |
| How | Pulumi **Automation API** for .NET (`LocalWorkspace`, stacks per target: `preview` = dry run, `up` = apply, `refresh` = observe) inside a **separate worker** (container image with the Pulumi CLI and cloud plugins) |
| Why separate | the Automation API launches the Pulumi engine and language host as processes; not AOT, heavy, and it needs the **strongest** credentials — isolating it keeps them out of the orchestrator's process |
| Programs | C# Pulumi programs per resource kind, owned by the vendor, versioned in the vendor's repo; `YFex.Provisioning.Pulumi` only runs them with typed inputs from specs |
| State | Pulumi state backend chosen by the vendor (cloud bucket with encryption or Pulumi Cloud); never in tenant databases |
| Credentials | the worker's workload identity; a separate identity for Tier 2 deletes (⚑PV11) |
| Mapping | `CreateDatabase`, `CreateBucket`, `SetNodePool`, `CreateRegion` → stack config + `up`; results (resource ids, connection **references**) returned to the run |

Alternatives kept open in ⚑PV9: Terraform/OpenTofu as a process (same isolation, HCL instead of C#), cloud SDKs (no preview), manual
runbooks (fine up to the second region).

## 4. Cloud SDKs (gaps only)

Used only for operations neither Kubernetes nor Pulumi covers well (e.g. reading a managed database's storage growth for capacity
signals). Each SDK lives in its own optional package (`YFex.Provisioning.Azure`, `YFex.Provisioning.Aws`), added when a phase needs it.
None is planned.

## 5. On-premises: Hosting profiles (DEFERRED — user 2026-11-12: cloud only for now; README §9)

| Step | Who |
|---|---|
| Central sends its Cluster load history and node list summary at check-in (existing vendor channel, Licensing) | central |
| Provisioning computes a **recommendation** (add a server, move a role, more memory) — never an action | vendor |
| Vendor support talks to the customer; the customer's IT adds a machine and installs the host | customer |
| Vendor edits the central's **Hosting module profile** (roles per host, modules) → signed with the vendor ring → delivered on the vendor channel (HO4; same open point as ⚑CL32's signed policy) | vendor → central |
| Central's hosts apply roles at the next safe point (Cluster runtime roles, ⚑CL30) | central |

No vendor credential exists at the customer; the profile is data the central verifies and may refuse (local limits).
