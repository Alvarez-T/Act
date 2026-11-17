# 08 — Platforms, security, privacy, AOT

## 1. Platforms and hosts

| Concern | Vendor cloud (Linux containers) | Development (Windows / macOS) |
|---|---|---|
| Where it runs | the orchestrator's control-plane deployment; the Pulumi worker as its own deployment | `dotnet run` against **kind** in Docker (through `linuxKit` on Windows) |
| Identity | workload identity per target (AKS / EKS / GKE differ in setup, not in the token file the client reads) | kubeconfig with exec plugin (`kubelogin`, `aws eks get-token`, `gke-gcloud-auth-plugin`) — spike PV-P0 |
| Kubernetes versions | the kinds used (`apps/v1`, `autoscaling/v2`, `policy/v1`, `keda.sh/v1alpha1`) are stable across supported Kubernetes versions; KEDA version pinned per region | same in kind |
| Secrets (fallback) | `ISecretStore` with the container's root key (environment key) | `ISecretStore` with DPAPI/CNG (Windows), Keychain/file (macOS) |
| Service host | none of its own: the orchestrator app hosts it (`Add…`, infrastructure rule) | same |

No OS-specific package is needed: everything is HTTP to the Kubernetes API, the Pulumi CLI as a process, and YFex.Data. Unverified
behaviour (exec-plugin auth on each cloud, token refresh timing) is a **spike**, not an assumption.

## 2. Threats

| Threat | Answer |
|---|---|
| A compromised data-plane node creates or deletes infrastructure | it has no credential, no RBAC, no network path to the API with one (06 §1); architecture test keeps the package out |
| A compromised orchestrator account applies a large plan | permissions split (plan / apply / approve), separation of duties, Tier 1/2 approvals bound to the plan digest, limits, rate limits, audit |
| A bad signal or rule scales to the limit | auto-apply only Tier 0 under the ceiling; rate limits; limits; shadow mode for new rules; freeze |
| Approval reused for another change | digest binding; plan expiry; stale check before each step |
| Someone changes resources by hand | drift detection and audit (05 §3); manager conflicts are reported, never forced |
| Pulumi worker leaks cloud credentials | separate process and identity; no tenant data; deletes need a second identity; short-lived tokens |
| Pulumi state file leaks resource layout and outputs | state backend encrypted; outputs hold references, never secrets |
| Control-plane database tampered with (specs edited directly) | specs are versioned and audited; a spec version without an audit entry is refused by the planner (hash chain open: could ride Audit's chain) |
| Region-wide outage of the control plane | data plane keeps running (autoscalers keep working inside the last bounds); Provisioning is not on any request path |
| Supply chain (Pulumi plugins, cloud SDKs) | pinned versions; only in the worker image; not in the data plane |

## 3. AOT and third-party packages

| Package | AOT gate | Third-party |
|---|---|---|
| `YFex.Provisioning`, `.Data`, `.Cluster`, `.Testing` | 0 IL warnings, AotSmoke | none |
| `.Kubernetes`, `.Kubernetes.Operator` | 0 IL warnings with the **own client** (⚑PV7); with `KubernetesClient` only if its AOT variant passes the spike | none (own client) or `KubernetesClient` (Apache-2.0) behind this optional package |
| `.Pulumi` | **exempt**, documented: the Automation API drives the Pulumi engine as processes and is not AOT; runs only in the worker | Pulumi Automation API (Apache-2.0) + CLI in the image |
| `.Azure`, `.Aws` (only if ever needed) | per SDK; decided when needed | cloud SDKs |

Every third-party package needs the user's approval before it is added (infrastructure rule); none is added in PV-P0…PV-P3.

## 4. Privacy

- Provisioning handles infrastructure names (pools, regions, databases), never tenant personal data.
- A `DatabaseSpec.DedicatedTo` holds a `TenancyPath` (a tenant key, not personal data); log and audit it through its type's
  classification.
- Region data residency is enforced by the registry and Placement (⚑TN6, ⚑DB8); Provisioning additionally refuses to create a
  `DedicatedTo` database in a region outside the tenant's residency rule (read through core `ITenantLocations` when it exists).
