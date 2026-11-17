# 06 — Places, platforms, security, privacy, AOT

## 1. Scenarios per place (⚑TN14)

| Place | Scenario | What happens |
|---|---|---|
| Vendor cloud | sales registers a Brazilian pharmacy | policy picks `br-south`; steps run; `Active` in seconds |
| Vendor cloud | a customer requires EU-only data and no EU region exists | `NeedsRegion`; operator sees "1 tenant waits for `eu-*`"; platform builds `eu-west`; operator records it; registration continues |
| Vendor cloud | customer stops paying | `Suspended`: sign-in refused with a clear message, jobs paused, data kept; `Resume` restores everything |
| Vendor cloud | `br-south` is full | operator sets `OpenForNewTenants = false`; new tenants go to `br-south-2` |
| Vendor cloud | customer leaves | `Offboarding` → export → after the period `Deleted`, ring destroyed |
| On-prem central | install | single-region mode; the installer runs the steps once for the one tenant (`AddTenantRegistry(r => r.SingleRegion(...))`) |
| On-prem central | customer moves from on-prem to the vendor cloud | not a region move: a **migration** = tenant export (Backup) imported into a cloud registration (later design) |
| Shop site | — | no registry; the site's tenant is its installation's tenant |

## 2. Security (⚑TN16)

| Threat | Mitigation |
|---|---|
| Anyone registers tenants (abuse, resource exhaustion) | `tenancy.register` permission; public sign-up is an app flow with Authentication proofs + Resilience rate limits in front of the registry |
| Moving a tenant's data abroad | residency change and region move need approval; every library checks residency |
| Reusing a deleted tenant's key | tombstones; key reuse refused |
| Suspended tenant keeps working on another node | state cached with version; `TenantRegistryChanged` notice; worst case one refresh period (60 s) |
| Registry write path compromised | writes only in the global region with a separate credential; every change audited |

## 3. Privacy (LGPD)

- The registry holds **no personal data** of customers' customers; tenant display names and admin e-mail inputs are company data, the
  admin e-mail passes through to Authentication and is not stored in `tenancy.provisioning` state (inputs marked sensitive are dropped
  after the step).
- Residency (art. 33) and deletion (art. 16, minimisation) as above; the deletion step order makes the **key-ring destruction** the
  last irreversible act.

## 4. Per-OS and AOT

- No OS-specific code: the registry is a library over YFex.Data; single-region mode reads configuration. Runs the same on Windows,
  Linux, macOS (dev).
- AOT/trim clean, `IsAotCompatible`, AotSmoke 0 warnings; JSON (step state, admin views) with System.Text.Json source generation;
  steps registered through DI (no reflection scanning).
- Hot path: `ITenantLocations.TryGet` — frozen dictionary lookup, 0 B allocated (benchmark).
