# 02 — Target model: what YFex.Authorization owns

## 1. One sentence

**YFex.Authorization answers "may *this caller* do/see *this thing* now, and if not, why and what would make it possible".**
Every other library *declares* what it needs (in its configuration or on its types) and *asks*. No library decides it alone.

## 2. Home (⚑Z1)

The analysis confirms the existing repo **`C:\Dev\YFex\YFex.Authorization`** as the home: every consumer already references it, it
has the generator and the host adapters, and it sits above YFex.Tenancy (grants per scope) and below Cqrs/NavigatR/Messaging.

One layering problem remains. Three things below the Authorization runtime need to *name* authorization concepts:

- YFex.Rules (layer 1) names approval permissions (`ApprovalDirective`, table `PublishPermission`, dynamic specs).
- YFex.Tenancy.Resolution owns the impersonation gate, but the Authorization runtime references Tenancy.
- YFex.Audit and YFex.Logging must run without the Authorization runtime but need the current **actor** and real actor.

Fix (⚑Z1–Z3, confirmed): **no authorization contract in YFex core.** The existing **`YFex.Authorization.Public`** is kept a strict
**leaf** (references only YFex core + BCL; no Tenancy, no DI — a test enforces it) and is the one assembly those libraries may
reference. YFex/CLAUDE.md's rule "Rules must NOT reference Authorization" becomes "Rules may reference `YFex.Authorization.Public`
only". YFex core gains just one generic type, `ValidityPeriod` (⚑Z13).

```
YFex core   + ValidityPeriod (generic From/Until window, ⚑Z13) — nothing authorization-specific
│
└── YFex.Authorization.Public   LEAF (core + BCL only): PermissionKey, Permission, Role, Claim, ClaimType, PolicyName, AccessToken,
    │                           PermissionSet, Requirement, CompiledPolicy, AuthorizationVerdict, IAuthorizationContext,
    │                           IPrincipalAccessor, IPolicyEvaluator, IMessageAuthorization, IPermissionDirectory,
    │                           PermissionSnapshot, IPermissionVersions, ApprovalToken, TemporaryGrant,
    │                           ActorIdentity/ActorKind/ActorRelation/ActorScope/ActorContext, attributes,
    │                           IAuthorizationConfiguration/AuthorizationBuilder
    │   ▲ referenced by: YFex.Rules (typed Permission), YFex.Tenancy.Resolution (actor), YFex.Audit / YFex.Logging (actor),
    │     YFex.Cqrs, YFex.NavigatR, YFex.Capabilities, YFex.Search
    ├── YFex.Authorization      runtime (→ .Public + YFex.Tenancy): registry, evaluator, permission sources, context cache,
    │                           signals, approvals (HMAC/ECDsa), temporary grants, impersonation gate, events via core probes
    ├── YFex.Authorization.Generator   catalogs (partial Permission properties + Ids), [RequirePermission] tables (types AND
    │                                  methods), analyzers AUTH001–AUTH004, AUTH006, AUTH007
    ├── YFex.Authorization.AspNetCore  inbound HTTP (per-request cached context, 401/403 + reason)
    ├── YFex.Authorization.Http        outbound user-token propagation (unchanged)
    ├── YFex.Authorization.Testing     NEW (⚑Z18): TestCaller builder, FakePermissionDirectory, verdict assertions
    ├── YFex.Authorization.Benchmarks  NEW (⚑Z18): budgets of 03 §8
    └── YFex.Authorization.Data        LATER (⚑Z18): SQL store for role assignments + temporary grants (with the role-admin feature)
```

Consumers keep thin adapters (§4). No cycle is introduced: `.Public` is a leaf; Tenancy/Rules/Audit/Logging never reference the runtime project.

## 3. Ownership table

| Concern | Owner after consolidation | Before |
|---|---|---|
| Permission identity (`PermissionKey`, `Permission`) | `.Public` (leaf; also referenced by Rules) | `.Public` |
| Catalogs, display manifest, startup catalog validation (incl. Cqrs + Rules permissions) | Authorization | Authorization (Cqrs/Rules unchecked, D5) |
| Caller context (`IAuthorizationContext`): principal, actor, real actor, scope, permission set, role set, token | Authorization | `IAuthContext` + `CallContext` + `ICacheScopeContext.User` (D19) |
| Ambient actor (`ActorContext`) for consumers without the runtime (Audit, Logging, Tenancy, Cqrs cache scope) | `.Public` type, **written only by Authorization adapters** | planned by Audit in `YFex.Auditing` |
| Permission sources (role map, token claims, scoped claims, data store, temporary grants) and their merge | Authorization | Authorization + Cqrs `IApproverDirectory` (D10) |
| Policy model + evaluation (sync visibility, async authoritative), verdict with reason | Authorization | Authorization + Cqrs authorizers (D2, D3) |
| **The** message policy table (CQRS commands/queries, HTTP request types) | Authorization registry, fed by Cqrs configuration | split (D1) |
| Route policies, view-model command policies | Authorization registry (generator) | routes only (D14) |
| Capability visibility (search/AI) | Authorization policies looked up by key | `IAuthorizationView` single permission (D12) |
| Resolved-context cache, permission versions, change signals, snapshots | Authorization | Messaging.Rpc (D7) |
| Approvals / step-up (approval tokens, verification, offline caps, approver re-check) | Authorization | Rules + Cqrs (D9) |
| Temporary (time-boxed) permission grants | Authorization | — |
| Impersonation authority + effective permissions while impersonating | Authorization (implements Tenancy's gate) | Tenancy/Web claim check (D11) |
| Reveal (data-access) **evaluation** | Authorization | — (designs only) |
| Reveal **policy** (sensitivity/category → requirement, per tenant) | YFex.Privacy (uses Authorization requirements) | — |
| Authorization events (denials, approvals, grants, impersonation, reveals) | Authorization emits through the core probe contracts (Diagnostics now, Audit core contract later) | not audited |

What Authorization does **not** own: authentication (JWT validation is the host's; token capture is the transport's), consent
(subject's choice, YFex.Privacy — "RequirePermission checks the caller, RequireConsent checks the subject"), execution policy
(YFex.Jobs), data scoping/partitioning (YFex.Tenancy), logging verbosity overrides (YFex.Logging; protected *by* a permission).

## 4. Thin adapters left in the owning libraries

| Library | Keeps | Deletes |
|---|---|---|
| **YFex.Cqrs** | fluent verbs on its builders (`.RequirePermission/.RequireRoles/.RequireAuthenticated/.RequirePolicy/.Authorize(...)/.Authorize<TRequirement>()/.RequireApproval(...)`) that **record `Requirement`s**; `CqrsAuthorizationConfiguration` exporting them to the registry; `IDecisionBound` (moved from Rules) | `AuthorizerKind`, `AuthorizerDescriptor`, `ICommandAuthorizer<T>`, `IQueryAuthorizer<T>`, `CommandPolicy.Authorize`, `QueryPolicy.Authorize`, `BuildCompositeAuthorizer`, the evaluator/factory parameters of `Build`, `IApproverDirectory` |
| **YFex.Messaging.Rpc** | identity capture (`ICallIdentitySource`, connection/header/session), `CallContext` (now *holds* an `IAuthorizationContext`), `CallContextPrincipalAccessor`, the Fusion compute service `ICallPermissions` (returns `PermissionSnapshot`), connection disconnect on revoke, the stage-2 call sites (one line each) | `IPermissionsVersions`, `PermissionsVersions`, `CallPermissions` record, `CallAuthorization` (+ `[ThreadStatic]`), `CallContextResolver` caching logic, `AddYFexCqrsAuthorization` second registry build |
| **YFex.Rules** | deciding *that* an approval is needed (`ApprovalDirective(Permission, …)`), the decision fingerprint, `ApprovalRequest` | `OverrideToken*`, issuers/verifiers, `OfflineApprovalCap` (generalized), `IDecisionBound` (→ Cqrs) |
| **YFex.Rules.Admin** | a generated `RuleAdminPermissions` catalog; resource requirements for "the table's publish permission"; `ApproveDecision` command | string options, `Demand`, `PermissionFor` string logic |
| **YFex.Capabilities** | `CapabilityKey` → policy key mapping; `ActionGate` (now a `[Union]` struct mapping the verdict + side-effect confirmation) | `IAuthorizationView`, `AuthorizationView`, reflection attribute reading |
| **YFex.NavigatR** | the pre-construction check (already good) | the hand-built denial text (uses the verdict) |
| **YFex.Tenancy / YFex.Web** | `ITenantImpersonationGate` interface + header option; middleware calls gate and enters the actor scope | `PermissionTenantImpersonationGate` as the default when Authorization is present |
| **YFex.Privacy / YFex.Audit / YFex.Logging** (future) | their catalogs (`PrivacyPermissions`, `AuditPermissions`, `LoggingPermissions`), `RevealPolicy` (Privacy), event sinks | — |

## 5. Public surface after consolidation (normative names, signatures sketched)

```csharp
// ── YFex.Authorization.Public (leaf), namespace YFex.Authorization.Public ─────────────────────────────────────────────────────────────────────────────────
public readonly record struct PermissionKey { string Value; int Ordinal; /* -1 = not in any catalog */ }     // + Ordinal (03 §2)
public readonly record struct Permission    { PermissionKey Key; string Value; }                              // unchanged
public readonly record struct ActorIdentity(string Subject, ActorKind Kind, string? HomeScope);   // HomeScope = canonical ScopeIdentity text (.Public can't see Tenancy)
public enum ActorKind : byte { User = 0, Service = 1, Device = 2, Agent = 3 }
public enum ActorRelation : byte { None = 0, TenantImpersonation = 1, UserImpersonation = 2, Delegation = 3, AgentOnBehalf = 4 }
public sealed record ActorScope(ActorIdentity Actor, ActorIdentity? RealActor, ActorRelation Relation, string? SessionId);
public static class ActorContext { public static ActorScope? Current { get; } /* AsyncLocal; written by Authorization adapters only (06 §4) */ }

// ── YFex core (generic, ⚑Z13) ──
public readonly record struct ValidityPeriod(DateTimeOffset From, DateTimeOffset? Until) { bool Contains(DateTimeOffset now); bool IsExpired(DateTimeOffset now); TimeSpan? Remaining(DateTimeOffset now); }

// ── YFex.Authorization.Public ───────────────────────────────────────────────────────────────────────────────────────────────
public interface IAuthorizationContext          // was IAuthContext (⚑Z4)
{
    ClaimsPrincipal Principal { get; }
    ActorIdentity Actor { get; }                // whose permissions apply
    ActorIdentity? RealActor { get; }           // who is really at the keyboard (impersonation/delegation)
    ActorRelation Relation { get; }
    string? Scope { get; }                      // canonical ScopeIdentity text the permissions were resolved in (.Public stays Tenancy-free)
    bool IsAuthenticated { get; }
    PermissionSet Permissions { get; }          // bitset, O(1) Contains
    long Version { get; }                       // permission version it was resolved at
    AccessToken? Token { get; }
    bool HasPermission(in Permission permission);
    bool IsInRole(in Role role);                // resolved once into a role set (fixes D4)
    bool HasClaim(in Claim claim);
}

public interface IPolicyEvaluator
{
    bool IsVisible(CompiledPolicy policy, IAuthorizationContext caller);                                          // sync, UI/search
    AuthorizationVerdict Authorize(CompiledPolicy policy, IAuthorizationContext caller, object? resource = null); // sync when possible
    ValueTask<AuthorizationVerdict> AuthorizeAsync(CompiledPolicy policy, IAuthorizationContext caller,
        object? resource, CancellationToken cancellationToken);                                                    // authoritative
}

public interface IMessageAuthorization          // the ONE gate all dispatchers call (04 §2)
{
    ValueTask<AuthorizationVerdict> AuthorizeAsync<TMessage>(TMessage message, IAuthorizationContext caller,
        CancellationToken cancellationToken) where TMessage : notnull;
    bool IsVisible(Type messageType, IAuthorizationContext caller);
}

public interface IPermissionDirectory           // "does subject X hold P in scope S now" (replaces IApproverDirectory)
{
    ValueTask<bool> HoldsAsync(ActorIdentity subject, Permission permission, string? scope, CancellationToken cancellationToken);
}
```

Everything else (`PermissionSet`, `Requirement`, `CompiledPolicy`, `AuthorizationVerdict`, snapshots, approvals, grants) is
specified in 03, 05 and 06.
