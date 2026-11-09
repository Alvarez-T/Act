# 06 — Feature gating (Authorization, Capabilities, code without an actor)

## 1. Why a new Authorization hook (⚑LC14)

Today (YFex.Authorization, Phase 4a) the effective `PermissionSet` of a caller is built by `IPermissionSource`s that **add**
permissions (`ContributeAsync(PermissionResolution, PermissionSet.Builder, …)`: role maps, claims, scoped claims). Nothing can
**remove** a permission afterwards, and a denial can only say `MissingPermission`, `MissingRole`, `PolicyDenied`, etc.

A licensing `IPermissionSource` cannot work: sources run in any order and only add. Checking the license in every handler would be
scattered and forgotten. The proposal is a small, **generic** hook owned by the Authorization session (it knows nothing about
licenses; a future feature-flag library could use it too):

```csharp
// YFex.Authorization (runtime) — request to the Authorization session (10 §2)
public interface IPermissionAvailability
{
    /// Called once per context build, AFTER every IPermissionSource. Removes permissions this deployment may not use now.
    /// Synchronous and I/O-free: implementations keep precomputed masks.
    void Restrict(in PermissionAvailabilityRequest request, PermissionSet.Builder permissions);

    /// Why a permission is unavailable: a short code for the denial (licensing: the module id). False = it is available.
    bool TryExplain(in PermissionAvailabilityRequest request, Permission permission, [NotNullWhen(true)] out string? code);
}

public readonly record struct PermissionAvailabilityRequest(ScopeIdentity Scope, InstallationId? Installation);

// YFex.Authorization.Public — one new enum member (wire-compatible: older readers treat an unknown kind as a denial)
public enum DenialKind { …, JustificationRequired = 7, Unavailable = 8 }
```

- **Effective permissions = granted ∩ available.** Every existing gate then respects the license without any change: the one message
  policy (`IMessageAuthorization`, full and quick check), `[RequirePermission]` on view-model commands, `VisibleWith` menus, the HTTP
  middleware, RPC `CallContext`, offline permission snapshots.
- **The denial says why.** When a required permission was removed by availability, the gate returns
  `Denial(DenialKind.Unavailable, Missing: delivery.dispatch, Code: "delivery")` — even if the user also lacks it, because no role
  change can fix it. The app's error presenter shows "not in your plan" (01 §4).
- **Service actors are masked too.** A marketplace webhook running as `ActorKind.Service` (YFex.Actors) with every permission still loses
  `delivery.*` when the tenant's delivery module ended. Server automations need no extra checks.
- **Never masked** (enforced by licensing, documented by the hook): licensing's own permissions, keep-lists and — per state — base
  (04 §3 table).

## 2. Where the mask is applied

| Place | Request | Data the licensing implementation uses |
|---|---|---|
| installation (client UI, local dispatch, offline) | its scope + `LocalInstallation.Current.Id` | its own `ILicense` (License + its seat state) |
| central, an RPC call from an installation | `CallContext` scope + **installation** (`YFex.Installations.Rpc`, ⚑I11) | the tenant's License + that installation's activation entry |
| central, a call without an installation (browser back office, a not-enrolled client) | scope, `Installation = null` | the tenant's License only (no seat) |
| central, background/service actor | scope of the work item | the tenant's License |

The client-side snapshot pushed by the server (`UseServerPermissions()`) is **already masked** by central; the installation masks
again with its own `ILicense`. Intersection twice is harmless and covers both cases: offline (only the local mask is current) and a
patched client (the server mask still holds for every server call).

## 3. Caches, versions and cost

- Licensing precomputes, per product, the `PermissionSet` mask of each (license state × seat state × module periods) combination when
  a License or ActivationList changes — a few bitsets. `Restrict` is an AND-NOT over the builder's words: budget **≤ 50 ns for 1 024
  declared permissions**, 0 bytes allocated. `HasPermission` stays 0.7 ns (it reads the already-masked set).
- When availability changes (a new License, a state transition at 00:00 of the expiry day, an activation), licensing calls
  `IAuthorizationSignals.PermissionsChanged(scope)`, so `AuthorizationContextCache` entries for that scope are rebuilt, RPC pushes a new
  snapshot, and open screens re-evaluate `VisibleWith` and command `CanExecute`.

## 4. Capabilities: nothing to change

YFex.Capabilities filters search hits and the AI agent's actions by the caller's permissions. Masked permissions disappear from the
universal search ("despachar entrega" finds nothing on an "essencial" store) and the agent's `ActionGate` returns `Denied` for them.
Showing them greyed out as an upsell ("not in your plan") is an open question for the Capabilities owner (README §8); the denial
already carries the module id it would need.

## 5. Code without an actor

```csharp
public enum ModuleAvailability : byte
{
    Available = 1,
    NotLicensed = 2,          // the License does not list the module
    ModulePeriodEnded = 3,    // add-on period over
    LicenseExpired = 4,       // after the grace (keep-list still available)
    SeatNotActivated = 5,     // this installation holds no seat
    NoLicense = 6,            // LicenseMissing / LicenseRejected
}
```

| Need | Use |
|---|---|
| a background service of a module (marketplace poller, SNGPC sender) | `await license.WaitUntilAvailableAsync(module, ct)` / `license.IsAvailable(module)` |
| central work for a tenant (webhook, scheduled export) | `ICentralLicenses.For(tenant).IsAvailable(module)` — usually unnecessary because service actors are masked (§1) |
| a numeric limit (marketplaces, branches in a report, stored documents) | `license.GetLimit(limitId)`; the **app** enforces it where the thing is created (01 §4) |
| a diagnostic or support screen | `license.GetAvailability(module)` → the reason above |

**Do not** check the license in UI code only, and **do not** sprinkle `IsAvailable` in handlers of messages that already require a
permission: the mask already covers them, on the server too.

## 6. Example: what changes when FarmaViva moves from "essencial" to "completo"

1. The vendor issues `ChangeLicense` → License Sequence 7 with `loyalty`, `delivery`, `controlled-medicines` added.
2. SaaS: the next push to central applies it; on-premises: the next check-in (or "sincronizar agora").
3. Installations accept the bundle → masks recomputed → `PermissionsChanged` → the delivery menu appears on CAIXA-01 within seconds,
   the universal search starts finding "fidelidade", the broker's `DeliveryOrdersPoller` leaves `WaitUntilAvailableAsync`.
4. Nobody restarts anything; no app update is involved.
