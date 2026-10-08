# 06 — Approvals, temporary grants, impersonation and the actor

Four related ideas, kept separate on purpose:

| Idea | Question | Lifetime | Example |
|---|---|---|---|
| **Approval** (step-up, delegated authority) | "someone who holds P vouches for **this one operation**" | minutes, bound to an operation fingerprint | supervisor PIN for a 12 % discount |
| **Temporary grant** | "subject S holds P **for a while**" | hours, bound to a subject + scope | "João may approve discounts until 18:00" |
| **Impersonation** | "actor A acts **as** tenant/user T" | one request/session | support inside tenant *acme* |
| **Justification** | "P is held, but this access needs a **reason** on record" | one access | reveal a health record |

## 1. Approvals (generalizes Rules override tokens, fixes D9/D10, ⚑Z12)

### 1.1 Split of responsibilities

| Stays in YFex.Rules | Moves to YFex.Authorization | Moves to YFex.Cqrs |
|---|---|---|
| `ApprovalDirective(Permission, Violation)` (typed `Permission` from `.Public`), `ApprovalRequest`, decision fingerprint, the amount cap `OfflineApprovalCap(Permission, RawValue MaxRequested)` | `ApprovalToken` (was `OverrideToken`; its window is a core `ValidityPeriod`), `ApprovalTokenKind {Server, Device}`, `IApprovalTokenIssuer/Verifier`, `HmacApprovalTokens`, `DeviceApprovalTokenIssuer/Verifier`, `CompositeApprovalTokenVerifier`, `OfflineApprovalPolicy(Permission, TimeSpan MaxAge)`, approver re-check through `IPermissionDirectory`, `ApprovalIssued/ApprovalAccepted` events | `IDecisionBound` (from Rules; carries `IReadOnlyList<ApprovalToken>`), generic `IApprovalBound`, the `.RequireApproval(...)` verb, `Approvals.Commands.Approve` message + handler (replaces `RuleAdmin.Commands.ApproveDecision`) |

The canonical signed payload and both signature schemes stay **byte-identical**, so tokens issued before the move still verify
(acceptance test in 09). The only rename is `OverrideToken*` → `ApprovalToken*` (the item validation D35–D38 left to the user).

### 1.2 Rules flow — unchanged for the developer

```csharp
// ✍ Rule (unchanged except the permission is typed)
d.RequireApproval(SalesPermissions.DiscountOverride, path, "discount.above.limit");

// ✍ Command record (IDecisionBound now lives in YFex.Cqrs; same members)
public sealed partial record ApplyDiscount(…, RulesetVersion RulesetVersion, ulong DecisionFingerprint,
    IReadOnlyList<ApprovalToken>? Approvals) : ICommand<Receipt>, IDecisionBound;
```

### 1.3 New: approvals without the rules engine

```csharp
// ✍ Dev writes — aggregate configuration; the message implements IApprovalBound (one property)
b.Command<VoidSale, Receipt>()
    .RequirePermission(SalesPermissions.SaleVoid)
    .RequireApproval(SalesPermissions.SaleVoidApprove, bind: command => command.SaleId);   // fingerprint = type + bound value
```

```
 Result
 cashier sends VoidSale (no token) ─► verdict ApprovalRequired([sales.void.approve, fingerprint F])
                                  ◄─ Error ApprovalRequired (same wire type + same UI dialog as rule overrides)
 supervisor approves on the terminal ─► Approvals.Commands.Approve(sales.void.approve, F)  (server checks the supervisor holds it,
                                  ◄─ ApprovalToken (HMAC, 15 min)                          emits ApprovalIssued → audit)
 cashier resends VoidSale + token  ─► verifier: signature, expiry, permission, fingerprint ─► Allowed (ApprovalAccepted → audit)
 offline: device-signed token ─► accepted only within OfflineApprovalPolicy.MaxAge, approver re-checked with IPermissionDirectory
          at replay, always flagged for review (D34 semantics kept)
```

## 2. Temporary grants (⚑Z13)

```csharp
// ✍ Dev writes — an admin screen or a command handler
await grants.IssueAsync(new TemporaryGrant(
    Subject: new ActorIdentity("joao", ActorKind.User, "0=acme/1=loja7"),
    Permissions: [SalesPermissions.DiscountOverride],
    Scope: currentStore,                                     // grant applies here and beneath (ScopeIdentity.IsWithin)
    Validity: new ValidityPeriod(now, now.AddHours(2)),      // reusable core type (⚑Z13)
    Reason: "gerente ausente"), cancellationToken);
```

Real cases (pharmacy): the responsible pharmacist is on vacation → the substitute gets `pharmacy.controlled.dispense` (SNGPC) for 15
days; the manager left early → a senior cashier gets `sales.discount.override` until the end of the shift; a support technician gets
`settings.fiscal.edit` for 2 hours to fix the NFC-e configuration; the external accountant gets `reports.fiscal.read` for the audit
week. Without grants an admin adds a role and must remember to remove it (privilege creep).

**`ValidityPeriod` (YFex core, ⚑Z13).** The grant itself is authorization data (permission + actor + scope) and stays in
Authorization; its *window* is a generic base type that the ecosystem re-implements ad hoc today (`EffectiveFrom` in Rules data and
descriptors, `IssuedAt/ExpiresAt` in `OverrideToken`, `OAuthToken` expiry, CallContext expiry, Persistence cache expiry, Jobs).
`ValidityPeriod(DateTimeOffset From, DateTimeOffset? Until)` with `Contains(now)`, `IsExpired(now)`, `Remaining(now)`, canonical
binary/JSON form, `ILogValue`. Used by `TemporaryGrant`, `ApprovalToken`, `PermissionSnapshot` now; Rules, consent validity, legal
holds and logging TTLs can adopt it later (no forced migration).

Rules enforced by `TemporaryGrantService`:

- the issuer needs `authorization.grants.issue`, and **cannot grant what it does not hold** in that scope (no escalation);
- `Validity` length ≤ `MaxGrantDuration` (default 24 h, app-configurable);
- issuing, revoking and expiry raise `AuthorizationSignals.PermissionsChanged(subject)` (a `TimeProvider` timer fires at `Validity.Until`),
  so cached contexts reload;
- every grant change is an authorization event (07 §5).

Storage: `ITemporaryGrantStore` (in-memory default; SQL in `YFex.Authorization.Data` later). `TemporaryGrantSource` contributes
active grants during resolution (03 §3.1).

**Not a temporary grant:** logging's "debug tenant acme for 30 min" (logging 06 §4). That override changes *logging thresholds for a
target*. It grants nothing to a principal. Logging keeps it, protected by `LoggingPermissions.VerboseEnable` and audited by
logging. Both designs share the same discipline (mandatory TTL, audited), but merging them would put a logging concept into
authorization.

## 3. Impersonation (fixes D11, ⚑Z14)

```csharp
// Before — YFex.Tenancy.Resolution default: raw claim, case-sensitive, roles ignored, HTTP only
actor.HasClaim("permission", "Tenant.Impersonate");

// After — Authorization implements Tenancy's gate with a real policy, evaluated on the REAL actor in its HOME scope
public sealed class PolicyTenantImpersonationGate(IAuthorizationContextFactory contexts) : ITenantImpersonationGate
{
    public bool CanImpersonate(ClaimsPrincipal? actor)
        => actor is not null && contexts.GetCached(actor).HasPermission(AuthorizationPermissions.TenantImpersonate);
}
```

- `AddYFexAuthorization` registers it, replacing `PermissionTenantImpersonationGate`. Old tokens still work: the claim value
  `Tenant.Impersonate` canonicalizes to the declared id `tenant.impersonate`.
- After a successful impersonation the adapter (Web middleware now, RPC `CallContext` in Phase 6) enters
  `ActorContext` with `Actor` = the user acting in the target tenant, `RealActor` = the same user with the home scope, and
  `Relation = TenantImpersonation`. The real actor is no longer lost (audit 02).
- **Effective permissions while impersonating** = the real actor's permissions resolved in the target scope **minus
  `ImpersonationRestrictions`** (default: `privacy.reveal.identifier`, `privacy.reveal.sensitive`, `privacy.detokenize`,
  `authorization.grants.issue`, issuing approvals; apps add their own). A removed permission denies with
  `DenialKind.RestrictedWhileImpersonating`. This implements privacy 08 §9: "impersonation does not grant reveal permissions".
- **User impersonation** ("support logs in as João") is designed but built with its feature: token exchange with an RFC 8693 `act`
  claim → `Actor` = João, `RealActor` = support, permissions = João's ∩ support's allowed mask, mandatory TTL, always audited.

## 4. The actor (`.Public`, fixes D19, ⚑Z3)

```csharp
// YFex.Authorization.Public (leaf: core + BCL only), 02 §5 — owner = the writer (Authorization adapters)
public static class ActorContext
{
    public static ActorScope? Current { get; }                                  // AsyncLocal, flows across await
    public static Activation Enter(ActorScope scope);                           // dispose to restore
}
```

- **Single writer:** only Authorization adapters call `Enter`: `AmbientPrincipalAccessor.SetCurrent` (desktop login),
  `CallContextMiddleware` (RPC), `YFexAuthorizationMiddleware` (HTTP), and nested `Delegation` scopes during approved
  operations. Libraries never set it themselves.
- **Readers** that must not depend on the Authorization runtime reference only the leaf `.Public` and read `ActorContext.Current`: YFex.Audit (`AuditActor` becomes
  `ActorIdentity` + relation; supersedes `YFex.Auditing.ActorContext` in audit 02), YFex.Logging (actor attributes), Cqrs per-user
  cache scope (`ICacheScopeContext.User : ClaimsPrincipal` → `ActorContext.Current?.Actor.Subject`).
- Everything that needs **permissions** keeps using `IPrincipalAccessor.Current` (`IAuthorizationContext`), which exposes the same
  `Actor`/`RealActor`/`Relation`.

## 5. Justification (step-up without another person)

A `Justification(kind)` requirement is satisfied when the call carries a reason (`AuthorizationJustification.Enter("atendimento
farmacêutico")` around the call, or a header on RPC/HTTP). The reason is attached to the operation for audit. Missing →
`JustificationRequired`, and the UI asks for one. First user: privacy reveal of `SensitivePersonal` data (07 §1).
