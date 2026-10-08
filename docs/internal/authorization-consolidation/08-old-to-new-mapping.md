# 08 — Old → new mapping

Legend: **moved** (same behaviour, new home/name) · **replaced** (new mechanism) · **deleted** · **kept**. Phase numbers refer to 09.

## YFex.Authorization (repo)

| Old | New | Kind | Phase |
|---|---|---|---|
| `.Public.PermissionKey`, `.Public.Permission` | same types, stay in `.Public` (+ `Ordinal`, `Declare`); `.Public` kept a leaf, now also referenced by Rules | kept (⚑Z1/Z2) | 1 |
| — | YFex core `ValidityPeriod` | new (⚑Z13) | 0 |
| — | `.Public` `ActorIdentity`, `ActorKind`, `ActorRelation`, `ActorScope`, `ActorContext` | new (⚑Z3) | 0 |
| `IAuthContext`, `IAuthContext<T>`, `AuthContext`, `ResourceAuthContext<T>`, `IAuthContextFactory`, `AuthContextFactory` | `IAuthorizationContext` (resource passed to the evaluator instead of a generic context), `AuthorizationContext`, `IAuthorizationContextFactory` | replaced (⚑Z4) | 1 |
| `[PermissionMeta]` on `public const string` fields, generated `Catalog.Values.X` | `[Permission("id", Name = …)]` on `static partial Permission` properties; generated property bodies + `Catalog.Ids.X` consts for attributes; `PermissionMetaEntry` → `PermissionEntry` | replaced (⚑Z2/Z4) | 1 |
| `cfg.AddCatalog(X.Manifest)` | module-initializer registration (manual call still accepted) | replaced | 1 |
| `FrozenSet<PermissionKey>` in `AuthContext` | `PermissionSet` bitset | replaced (⚑Z10) | 1 |
| `Requirement` record struct + `RequirementKind` | `[Union]` `Requirement` + `CompiledPolicy` | replaced | 1 |
| `AuthorizationResult` `union(Allowed, Denied)` | `[Union]` `AuthorizationVerdict` + `Denial` | replaced (⚑Z11) | 1 |
| `IPolicyEvaluator.IsAllowed(Permission/Policy)`, `AuthorizeAsync<TResource>` | `IsVisible`, `Authorize`, `AuthorizeAsync(policy, caller, resource)`; `HasPermission` stays on the context | replaced | 1 |
| `IPermissionProvider`, `IScopedPermissionProvider`, `DefaultPermissionProvider`, `RolePermissionMap` | `IPermissionSource` + built-in sources (`RolePermissionMapSource`, `PermissionClaimSource`, `ScopedClaimSource`, `TemporaryGrantSource`); `RolePermissionMap` kept | replaced | 1 |
| `IAuthorizationConfiguration`, `AuthorizationBuilder` (runtime project) | same types in `.Public` (generated configs and Cqrs need no runtime reference) | moved | 1 |
| `CompiledAuthorizationRegistry.ByMessageType/ByViewModelType` | `PolicyKey → CompiledPolicy` table (messages, routes, view-model commands, capabilities); built lazily from DI | replaced | 1–2 |
| `HttpContextPrincipalAccessor` (rebuild per access) | `YFexAuthorizationMiddleware` + cached context; accessor reads only | replaced | 1 |
| generator: first type attribute only, simple-name VM detection, `Permission.Parse` rows | all attributes merged, methods → view-model command rows, metadata-name detection, catalog accessors, AUTH006–007 (AUTH005 reserved) | replaced | 1 |

## YFex.Cqrs

| Old | New | Kind | Phase |
|---|---|---|---|
| `AuthorizerKind`, `AuthorizerDescriptor` | `Requirement` list per message (accumulator) | deleted | 2 |
| `ICommandAuthorizer<T>`, `IQueryAuthorizer<T>`, `.Authorize<TAuthorizer>()` | `IAuthorizationRequirement<T>` / `IAsyncAuthorizationRequirement<T>`, `.Authorize<TRequirement>()` | replaced (⚑Z7) | 2 |
| `.Authorize(Func<ClaimsPrincipal, T, bool>)` | `.Authorize(Func<IAuthorizationContext, T, bool>)` | replaced (⚑Z7) | 2 |
| `.RequireRoles(params string[])` | removed (keep `Role[]`) | deleted | 2 |
| `.RequireAuthorization(string)` | `.RequirePolicy(PolicyName)` (actually enforced, async path) | replaced (⚑Z8) | 2 |
| — | `.RequireAnyPermission(...)`, `.RequireApproval(permission, bind)` | new | 2 / 4 |
| `CommandPolicy.Authorize`, `QueryPolicy.Authorize` | — (dispatchers ask `IMessageAuthorization`) | deleted | 2 |
| `CompiledMessagingRegistry.Build(..., policyEvaluator, principalFactory)` | `Build(...)` without them; `AuthorizationRequirements` exposed; `CqrsAuthorizationConfiguration` | replaced | 2 |
| `CqrsCapabilityInfo.RequiredPermission` (first only) | `PolicyKey` (+ display permission) | replaced | 5 |
| `IApproverDirectory` | `IPermissionDirectory` (Authorization) | replaced | 4 |
| `Error` flat struct + `ErrorType` enum (`Unauthorized` for everything) | `[Union]` `Error` with `ErrorKind` tag; cases `NotFound`, `Failed`, `Unauthenticated`, `Forbidden(Denial)`, `ValidationProblem`, `Conflict`, `ApprovalRequired` | replaced (⚑Z11) | 2b |
| `ICacheScopeContext.User : ClaimsPrincipal` | `ICacheScopeContext.Subject : string?`, default = `ActorContext` subject; no subject → `ScopedByUser()` not cached (P5) | replaced | 3a ✅ |
| — (in Rules) `IDecisionBound` | `YFex.Cqrs.IDecisionBound` (+ `IApprovalBound`) | moved | 4 |
| — | `Approvals.Commands.Approve(Permission, Fingerprint) : ICommand<ApprovalToken>` | new (replaces `RuleAdmin.Commands.ApproveDecision`) | 4 |

## YFex.Messaging.Rpc

| Old | New | Kind | Phase |
|---|---|---|---|
| `IPermissionsVersions`, `PermissionsVersions` | `IPermissionVersions`, `InMemoryPermissionVersions` (Authorization) | moved | 3a ✅ |
| `CallContextSignals.PermissionsChanged/AllPermissionsChanged/RevokeAsync` | `IAuthorizationSignals.PermissionsChanged/AllPermissionsChanged/RevokedAsync` (Authorization) + `RpcAuthorizationChangeObserver` (invalidate, disconnect) | moved | 3a ✅ |
| `CallPermissions` record | `PermissionSnapshot` (Authorization, payload only) + `PermissionSnapshotFormatter` (Rpc) | replaced | 3a ✅ |
| `ICallPermissions` | kept, returns `PermissionSnapshot` (proxy golden test regenerated) | kept | 3a ✅ |
| `CallAuthorization` (+ `[ThreadStatic]`), `CallPrincipalSource.Authorize` | `IMessageAuthorization` call in each dispatcher | deleted | 2 |
| `CallContextResolver.AuthFor` cache logic | per-identity fast path kept; a miss goes through `AuthorizationContextCache` | moved | 3a ✅ |
| `CallContext.Auth : IAuthContext` | `CallContext.Caller : IAuthorizationContext` | renamed | 1 |
| `AddYFexCqrsAuthorization()` (second registry build) | registered automatically by `UseYFexMessagingRpcServer`/`AddYFexAuthorization` | deleted | 2 |
| `CallContextPrincipalAccessor` (`ActivatorUtilities`) | kept, factory without reflection | kept | 3a ✅ |
| — | client `UseServerPermissions()` → `SnapshotPrincipalAccessor` + `ServerPermissionsFeed` (online) | new (⚑Z9) | 3a ✅ |
| — | signed, persisted offline snapshot | new (⚑Z9) | 3b |

## YFex.Rules / Rules.Admin / Rules.Dynamic

| Old | New | Kind | Phase |
|---|---|---|---|
| `string` permission in `ApprovalDirective`, `LimitDirective.ApprovalPermission`, `TableSchema.PublishPermission`, row `ApprovalPermission`, `ApprovedBy` | `Permission` (`.Public`ore) | replaced (⚑Z2) | 4 |
| Dynamic spec `"permission": "…"` | still a string in JSON; resolved against declared keys at publish (`authorization.permission.unknown`) | replaced | 4 |
| `OverrideToken`, `OverrideTokenKind`, `IOverrideTokenIssuer/Verifier`, `HmacOverrideTokens`, `DeviceOverrideToken*`, `CompositeOverrideTokenVerifier` | `ApprovalToken*` family in Authorization (byte-identical payload) | moved + renamed (⚑Z12) | 4 |
| `OfflineApprovalCap(Permission, MaxRequested, MaxAge)` | Authorization `OfflineApprovalPolicy(Permission, MaxAge)` + Rules `OfflineApprovalCap(Permission, MaxRequested)` | split | 4 |
| `RuleAdminOptions.*Permission` strings, `RulePublisher.Demand/PermissionFor` | `RuleAdminPermissions` catalog, typed option overrides, gate requirements (`RuleAdminRequirements`, `RuleAdminViewRequirement`, `ApproveDecisionRequirement`) | replaced | 4a ✅ |
| `RuleAdmin.Commands.ApproveDecision` | `Approvals.Commands.Approve` (Cqrs) | replaced | 4 |

## YFex.Capabilities / YFex.Search / YFex.NavigatR

| Old | New | Kind | Phase |
|---|---|---|---|
| `IAuthorizationView`, `AuthorizationView`, `CompiledCapabilityRegistry.Authz/AuthzFor` | `CompiledPolicy` per capability (via `PolicyKey`) | deleted | 5 |
| `ActionGate` native union `(Allowed, NeedsConfirmation, Denied(Permission))` | `[Union]` struct, `Denied(Denial)` + new `NeedsApproval` | replaced | 5 |
| `CapabilitySourceBase.ReadRequirePermissionAttribute` (reflection) | generator / registry | deleted | 5 |
| Search filters `IsAllowed(permission, ctx)` | `IsVisible(policy, caller)` | replaced | 5 |
| Navigator denial text | from verdict + manifest; optional re-check on signals | replaced | 5 |

## YFex.Tenancy / YFex.Web

| Old | New | Kind | Phase |
|---|---|---|---|
| `PermissionTenantImpersonationGate` (default) | `PolicyTenantImpersonationGate` (Authorization) when Authorization is wired | replaced (⚑Z14) | 6 |
| `ITenantImpersonationGate`, `ITenantImpersonationAuditor` | kept (auditor fed also by `ImpersonationStarted`) | kept | 6 |
| real actor lost after middleware | `ActorContext` scope with `Relation = TenantImpersonation` | new | 6 |
| — | RPC `CallContext` impersonation (`AllowImpersonation()`) | new | 6 |

## Untouched (justified)

`YFex.Jobs` execution policy (01 §2.7); `YFex.Tenancy [ScopedTo]`; Web outbound credentials; Payment authorization; OS elevation.
