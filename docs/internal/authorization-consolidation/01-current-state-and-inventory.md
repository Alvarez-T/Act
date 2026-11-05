# 01 — Current state and inventory

> Verified against the code on 2026-10-02 (RoselYn + grep over every repo under `C:\Dev\YFex` and `C:\Dev\YFex.Automation`;
> skipped `Act*`, `_Backup`, websites, `Hacks`, `CamillPlan`, `Engine`). Paths are relative to `C:\Dev\YFex`. Line numbers are
> the ones read on that date. Defects are numbered **D1…Dn** and referenced by the other files.

## 1. What already works (keep)

`YFex.Authorization` (repo `YFex.Authorization`, ~2 900 lines, 41 + 3 tests) implemented most of the old proposal
(`docs/internal/authorization-design.md`, status still says "Proposed" — it is outdated):

| Piece | Where | State |
|---|---|---|
| Value objects `PermissionKey`, `Permission`, `Role`, `PolicyName`, `ClaimType`, `Claim`, `AccessToken` | `src/YFex.Authorization.Public/*.cs` | done, good (Length convention, converters, redacted token) |
| `[PermissionCatalog]` + `[PermissionMeta]` + generated `Values`/`All`/`Manifest`, analyzer AUTH001–004 | `src/YFex.Authorization.Generator` | done |
| `Requirement`/`Policy`, `IPolicyEvaluator` (sync visibility + async authoritative), `INamedPolicyHandler` | `.Public/Requirement.cs`, `src/YFex.Authorization/PolicyEvaluator.cs` | done |
| `CompiledAuthorizationRegistry` (`ByMessageType`, `ByViewModelType`, catalog check at build) | `src/YFex.Authorization/CompiledAuthorizationRegistry.cs` | done, **but `ByMessageType` is not read by CQRS dispatch (D1)** |
| `IPermissionProvider` / `IScopedPermissionProvider` / `DefaultPermissionProvider` (role map + token claims + `scoped_role`/`scoped_permission` claims per `ScopeIdentity`) | `src/YFex.Authorization/PermissionProviders.cs` | done |
| Inbound HTTP gate (JWT + endpoint filter, 401/403/200 test) | `src/YFex.Authorization.AspNetCore` | done |
| Outbound token propagation + `ITokenStore` | `src/YFex.Authorization.Http`, `YFex.Web.Refit/Builder/PrincipalPropagationExtensions.cs` | done |
| NavigatR gate before VM construction | `YFex.NavigatR/src/YFex.NavigatR/Navigator.cs:66-93` | done (good) |
| Search/AI filter-before-rank | `YFex.Capabilities`, `YFex.Search.Capabilities` | done, **with one permission per capability (D12)** |

Consumers already on `YFex.Authorization.Public`: Cqrs, NavigatR, Messaging.Rpc (runtime project), Capabilities, Search,
Rules.Admin (through Cqrs). Reference graph today:

```
YFex.Authorization.Public → YFex core only
YFex.Authorization        → .Public + YFex.Tenancy (ScopeIdentity)
YFex.Cqrs                 → core, YFex.Rules, YFex.Authorization.Public
YFex.NavigatR             → .Public, YFex.Tenancy
YFex.Messaging.Rpc        → YFex.Cqrs, YFex.Authorization (runtime), YFex.Tenancy
YFex.Rules                → core, YFex.Validation         (MUST NOT reference Authorization — YFex/CLAUDE.md layer rule)
YFex.Rules.Admin          → YFex.Rules, YFex.Cqrs
YFex.Capabilities         → .Public, YFex.Tenancy
YFex.Tenancy.Resolution   → YFex.Tenancy                   (cannot reference Authorization: Authorization → Tenancy)
YFex.Web.AspNetCore       → YFex.Tenancy (+ Cqrs through YFex.Web)
```

## 2. Seam inventory

### 2.1 YFex.Cqrs — a second authorizer vocabulary

| Item | Location |
|---|---|
| `IQueryAuthorizer<TQuery>.Authorize(ClaimsPrincipal, TQuery)`, `ICommandAuthorizer<TCommand>.Authorize(ClaimsPrincipal, TCommand)` | `YFex.Cqrs/src/YFex.Cqrs/Configuration/Interfaces.cs:22-29` |
| `AuthorizerKind {RequireAuthenticated, Roles, PolicyName, Inline, TypeRef, Permission}`, `AuthorizerDescriptor` (roles as `string[]`, policy as `string`) | `Registration/Descriptors.cs:12-40` |
| Builder verbs `.RequireAuthenticated/.RequireRoles(string[] and Role[])/.RequirePermission/.RequireAuthorization(string)/.Authorize(Func<ClaimsPrincipal,T,bool>)/.Authorize<TAuthorizer>()` | `Configuration/CommandConfigurationBuilder.cs:134-176, 513-552`, `QueryConfigurationBuilder.cs:113-155` |
| Compiled gate `Func<ClaimsPrincipal, object, bool>? Authorize` | `Runtime/CommandPolicy.cs:28`, `Runtime/QueryPolicy.cs:11` |
| Compilation `BuildCompositeAuthorizer` (inline role loop, evaluator fold) | `CompiledMessagingRegistry.cs:346-421` |
| `CompiledMessagingRegistry.Build(..., IPolicyEvaluator?, Func<ClaimsPrincipal, IAuthContext>?)` and a second build in `AddYFexCqrsAuthorization` | `CompiledMessagingRegistry.cs:70-76`; `YFex.Messaging/src/YFex.Messaging.Rpc/RpcMessagingExtensions.cs:233-256` |
| `CqrsCapabilityInfo.RequiredPermission` = first permission only | `CompiledMessagingRegistry.cs:140-184` |
| `IApproverDirectory.HasPermissionAsync(string userId, string permission)` | `Runtime/RulesStage.cs:248-255`, used at `:149-163` |
| `ErrorType` has `Unauthorized` only (no forbidden vs unauthenticated) | `ErrorType.cs` |
| `ICacheScopeContext.User : ClaimsPrincipal` (per-user cache keys) | `ICacheScopeContext.cs:8`, `YFex.Cqrs.Tenancy/TenancyCacheScopeContext.cs:70-78` |

Old-doc claim "PolicyName/TypeRef compile to null (silently unenforced)": **no longer true.** They now fold into a policy that the
sync evaluator always denies (`CompiledMessagingRegistry.cs:383-391, 334-345`). That trades one defect for another (D3).

### 2.2 YFex.Messaging.Rpc — authorization living in the transport

| Item | Location | Is it authorization? |
|---|---|---|
| `IPermissionsVersions` / `PermissionsVersions` (per-subject + global counters) | `CallContext/CallContextSignals.cs:17-42` | **yes** — transport-neutral concept |
| `CallContextSignals.PermissionsChanged / AllPermissionsChanged / RevokeAsync` | `CallContextSignals.cs:48-81` | **yes** (the signal); disconnecting connections is transport |
| `ICallPermissions` Fusion compute service + `CallPermissions(Subject, IsAuthenticated, Version, string[] Permissions)` | `CallContext/CallPermissions.cs:19-62` | snapshot = **yes**; compute service = transport |
| `CallContextResolver` — caches the resolved `IAuthContext` per connection identity until the version changes | `CallContextMiddleware.cs:21-71` | cache = **yes**; identity capture = transport |
| `CallAuthorization` static with `[ThreadStatic] t_authorizing` + `ContextFor` | `CallContext/CallAuthorization.cs:13-44` | workaround for D2 |
| `CallContextPrincipalAccessor` (decorates `IPrincipalAccessor` so CallContext wins) | `CallAuthorization.cs:78-117` | adapter (keep, in Rpc) |
| Stage-2 calls in every dispatcher | `LocalDispatcher.cs:76,159,255`, `ValidatingHandlerInvoker.cs:66-74`, `CallContextMiddleware.cs:117-156`, `FusionMessageBus.cs:233,278,317` (client UX), `YFex.Messaging.Rpc.Wolverine/WolverineLocalDispatcher.cs:52-59` | adapter |

### 2.3 YFex.Rules / Rules.Admin / Rules.Dynamic — permissions as strings, approvals as a private token system

| Item | Location |
|---|---|
| `ApprovalDirective(string Permission, …)`, `LimitDirective.ApprovalPermission : string?` | `YFex.Rules/src/YFex.Rules/Directives/Directive.cs:56,75` |
| `TableSchema.PublishPermission`, row `ApprovalPermission` (strings) | `Tables/TableContracts.cs:40,64,218-230` |
| `ApprovedBy(string UserId, string Permission)` | `Decisions/Adjustments.cs:13` |
| `OverrideToken` (+ `OverrideTokenKind Server/Device`), `IOverrideTokenIssuer/Verifier`, `HmacOverrideTokens`, `DeviceOverrideTokenIssuer/Verifier` (ECDsa), `OfflineApprovalCap`, `CompositeOverrideTokenVerifier` | `Approvals/OverrideTokens.cs:9-230` (validation D34) |
| `IDecisionBound.Approvals : IReadOnlyList<OverrideToken>?` | `Approvals/Enforcement.cs:7-13` |
| Dynamic rule specs `"permission": "…"` | `YFex.Rules.Dynamic/Model/RuleSpec.cs:27`, `Compiler/RuleCompiler.cs:129,166` |
| `RuleAdminOptions.ParameterPublishPermission/OverridePermission/RulePublishPermission/ViewPermission/TablePublishPermission` (strings) | `YFex.Rules.Admin/RuleAdminHandlers.cs:12-24` |
| `RulePublisher.PermissionFor` + `Demand(IAuthContext, string)` → `Permission.TryParse` per call | `RuleAdminHandlers.cs:137-156` |
| `ApproveDecisionHandler` demands the permission then issues the token | `RuleAdminHandlers.cs:206-215` |
| Aggregate config: every RuleAdmin message only `.RequireAuthenticated()` | `YFex.Rules.Admin/RuleAdminSetup.cs:21-65` |

### 2.4 YFex.Capabilities / YFex.Search.Capabilities

| Item | Location |
|---|---|
| `IAuthorizationView.RequiredPermissions : FrozenDictionary<CapabilityKey, Permission>` (one permission) | `YFex.Capabilities/src/YFex.Capabilities/Registries.cs:35-37, 81-90` |
| `CapabilityAgentApi.CanInvoke` = view lookup + `IsAllowed(permission)` + side effect → `ActionGate` | `Agent.cs:93-104` |
| `ActionGate` native union, `ActionDenied(Permission Missing)` | `ActionGate.cs:10-29` |
| `ReadRequirePermissionAttribute` → `type.GetCustomAttributes(...)` | `Sources/CapabilitySourceBase.cs:87-93` |
| CQRS capability permission fallback (registry, then attribute by reflection) | `YFex.Capabilities.Cqrs/CqrsCapabilitySource.cs:97-109` |
| Filters | `Search/CapabilitySearchService.cs:85`; `YFex.Search/src/YFex.Search.Capabilities/CapabilityCatalogProvider.cs:56` |

### 2.5 YFex.NavigatR

Routes get permissions from `b.ForRoute<TViewModel>()` or `[RequirePermission]` on the VM class (the generator routes the row to
`ByViewModelType` when a base class is named `StateObject`/`MvvmStateObject`/`ViewModel`/`PageViewModel`). `Navigator` checks
synchronously before constructing the VM (`Navigator.cs:81-93`). Good. Only small issues (D16, D17).

### 2.6 YFex.Tenancy + YFex.Web — impersonation

| Item | Location |
|---|---|
| `ITenantImpersonationGate.CanImpersonate(ClaimsPrincipal?)` | `YFex.Tenancy/src/YFex.Tenancy.Resolution/ITenantImpersonationGate.cs:11-15` |
| `PermissionTenantImpersonationGate` — raw claim `permission` = `"Tenant.Impersonate"` | same file `:22-37` |
| `ITenantImpersonationAuditor.RecordAsync(realUserId, targetTenantId)` (+ Null default) | `ITenantImpersonationAuditor.cs` |
| Only HTTP honours impersonation (header `X-Tenant-Impersonate`) | `YFex.Web/src/Web/YFex.Web.AspNetCore/Tenant/TenantResolutionMiddleware.cs:59-74`; defaults `YFexWebTenantServiceExtensions.cs:36-38` |

### 2.7 YFex.Jobs — execution policy, **not** user authorization

`RequestPermissionCommand(PermissionRequest)` → `HubPolicyDecision {Granted, Denied, Deferred, Blocked}`
(`YFex.Jobs/src/YFex.Jobs.Hub.Abstractions/Services/ProcessControl/ProcessControlContract.cs:19`,
`YFex.Jobs.Hub.Contracts/Policy/HubPolicyDecision.cs`, `Engine/IExecutionPolicyEngine.cs:24`,
`YFex.Jobs.Abstractions/Policies/IExecutionPolicy.cs`). The subject is a **job on a node**, not a principal; the inputs are holds,
schedules, leases, hub reachability; the outcomes include *defer until*. There is no catalog, no role, no user. It answers "may this
work run **now** on **this machine**", which is scheduling/process control. Decision ⚑Z16: stays in Jobs. The wire names are frozen
(`HubContractMapper.cs:11-12`), so no rename.

### 2.8 Designs not implemented yet that need permissions

| Design | What it needs | Where |
|---|---|---|
| YFex.Privacy | `PrivacyPermissions` catalog (`privacy.reveal.personal/identifier/sensitive`, `privacy.detokenize`, `privacy.requests.handle`, `privacy.legal-hold.manage`, `privacy.notices.publish`); `RevealPolicy` (sensitivity/category → permission, per tenant, justification for sensitive); server-side omission (⚑P17); record-level classification (⚑P24); "consent is not a permission" | `docs/internal/privacy/08-integrations.md §1, §5, §9, §10`; `04-consent-and-purposes.md §11` |
| YFex.Audit | `audit.read`, `audit.read.all`, `audit.reveal`, `audit.reveal.sensitive`; `ActorContext` (real actor vs impersonation, `ActorRelation`) in core; approval-token issuance must be audited | `docs/internal/audit/08-privacy-retention-and-access.md:21-25,113-122`; `02-entry-model.md:130-190` |
| YFex.Logging | remote scoped verbose override with mandatory TTL, audited | `docs/internal/logging/06-configuration.md §4` |

### 2.9 Searched, not authorization (false positives)

| Hit | Why it is not app authorization |
|---|---|
| YFex.Payment `Authorize`/`Authorized` | card payment authorization |
| YFex.Web credentials, `IAuthScheme`, `EndpointAuthHandler` | outbound API authentication (service credentials) |
| YFex.System/YFex.Security UAC `ConsentPromptLevel`, `IsElevated`; YFex.Devices `IElevation`, `RequiresElevation` | operating-system administrator rights |
| YFex.Data DataMining `RequiresElevatedPrivileges` | database server privileges |
| YFex.UI/Avalonia/Blazor `Elevation*` | shadow depth |
| YFex.Fiscal "Autorização" | SEFAZ authorization of an NF-e |
| YFex.Jobs `PolicyDecision.Grant` | execution policy (§2.7) |
| YFex.Tenancy `[ScopedTo<TScope>]` | data scoping/customization, not access control |
| YFex.Devices NMEA `ElevationDegrees` | satellite elevation |
| YFex.Automation, Brain, CustomLogic, Data, Domain, EventSourcing, Farma, Finance, Marketplace, Orchestration, Resilience, Logging, LoggY, Profiling | no authorization code (Farma has no users/permissions yet) |

## 3. Defects

| # | Severity | Defect | Evidence |
|---|---|---|---|
| **D1** | **security** | `[RequirePermission]` on a message type and `b.ForMessage<T>()` fill `CompiledAuthorizationRegistry.ByMessageType`, but **no CQRS dispatcher reads it**. Only the AspNetCore filter and `CqrsCapabilitySource` do. The README's "gated identically whether dispatched internally (Gate 1) or received over HTTP (Gate 3)" is false. | grep `ByMessageType\|TryGetMessagePolicy` — no hit in `YFex/src` |
| **D2** | design | Two policy vocabularies for one message (Cqrs `AuthorizerDescriptor` + Authorization `Requirement`). The Cqrs gate takes `ClaimsPrincipal`, so the evaluator needs an `IAuthContext` factory per call, and Messaging.Rpc needs a `[ThreadStatic]` hand-off to avoid rebuilding it. | `CompiledMessagingRegistry.cs:346-421`; `CallAuthorization.cs:15-43` |
| **D3** | correctness | `.RequireAuthorization("name")` and `.Authorize<TAuthorizer>()` **always deny**: they become `PolicyRef`, which the sync evaluator treats as hidden. `Authorize<T>()` maps to a policy name `"type:" + FullName` that no handler can ever match, and the `ICommandAuthorizer<T>` instance is never created. The dispatchers are async, so the authoritative async path was available. | `CompiledMessagingRegistry.cs:383-391`; `PolicyEvaluator.cs:76-85`; test `AuthorizationGateTests` asserts the deny |
| **D4** | correctness | Cqrs roles are checked with `ClaimsPrincipal.IsInRole(string)` (case-sensitive claim scan), while `Role` equality is case-insensitive. `AuthContext.IsInRole` does the same scan on every check. | `CompiledMessagingRegistry.cs:366-373`; `AuthContext.cs:26` |
| **D5** | startup safety | Permissions declared through Cqrs `.RequirePermission(...)` are never checked against catalogs (only `IAuthorizationConfiguration` rows are). Rules/Rules.Admin permission strings are never checked at all: a typo is an approval nobody can ever give. | `CompiledAuthorizationRegistry.Build`; §2.3 |
| **D6** | performance | `HttpContextPrincipalAccessor.Current` builds a new context (provider call + `HashSet` + `FrozenSet`) **on every access**; one request reads it several times (filter, NavigatR, Capabilities). `DefaultPermissionProvider` scans the whole role map with `IsInRole` per role. | `HttpContextPrincipalAccessor.cs:24-40`; `PermissionProviders.cs:54-86` |
| **D7** | layering | Permission versions, change signals and the permission snapshot live in Messaging.Rpc, so a desktop/LocalDispatcher host, the AspNetCore adapter, NavigatR and Capabilities cannot use them. | §2.2 |
| **D8** | client gap | The client receives `CallPermissions` but nothing turns it into an `IPrincipalAccessor`: client NavigatR/Capabilities/menus cannot see server-granted permissions without app glue. `CallPermissions.Has` is `Array.IndexOf` over strings; the server builds it by downcasting `(context.Auth as AuthContext)?.Permissions`. No offline copy. | `CallPermissions.cs:29-62` |
| **D9** | duplication | Rules owns a private approval/elevation mechanism (HMAC + ECDsa tokens, key rotation, offline caps, approver re-check through Cqrs `IApproverDirectory`). Privacy reveal, impersonation and plain commands ("void sale needs a supervisor") need the same thing. Token issuance is not audited (audit inventory). | §2.3 |
| **D10** | design | `IApproverDirectory` ("does user X hold P now") is a second permission source beside `IPermissionProvider`, with string ids. | `RulesStage.cs:252-255` |
| **D11** | security | Impersonation: the gate reads a raw claim (`"Tenant.Impersonate"`, case-sensitive), so a permission granted through a role never counts. It bypasses the evaluator, works only over HTTP (RPC `CallContext.WithTenant()` has no equivalent), and loses the real actor after the middleware. Permissions while impersonating are not restricted. | §2.6; audit 02 |
| **D12** | correctness | Capabilities keep **one** permission per capability (the first). A command that needs roles, two permissions, a named policy or a resource check shows as available in search/AI and is then denied at dispatch, which breaks the filter-before-rank promise. RuleAdmin commands show as "authenticated only" although their handler demands a permission. | `Registries.cs:35-37`; `CompiledMessagingRegistry.cs:177-184`; `RuleAdminSetup.cs` |
| **D13** | AOT | `CapabilitySourceBase.ReadRequirePermissionAttribute` uses `GetCustomAttributes`; `CallContextPrincipalAccessor.Create` uses `ActivatorUtilities.CreateInstance`. | `CapabilitySourceBase.cs:87-93`; `CallAuthorization.cs:113-117` |
| **D14** | generator | `[RequirePermission]` is `AllowMultiple` and allowed on methods, but the generator takes only the **first** attribute of a **type** and ignores methods. On a `[StateCommand]` method it hides the command from search/AI (Capabilities generator) but nothing blocks executing it. | `AuthorizationGenerator.cs:45-50, 114-126`; Capabilities `Discovery.cs:92-154` |
| **D15** | wire/UX | Denials carry no reason over the wire (`"Unauthorized."`) and no 401/403 split, so a client cannot show "requires *X*", ask for re-login, or start an approval flow. Denials are not logged. | `LocalDispatcher.cs:160`; `CallContextMiddleware.cs:155`; `ErrorType.cs` |
| D16 | naming | `IAuthContext`, `AuthContext`, `IAuthContextFactory`, `ResourceAuthContext`, Capabilities `Authz`/`AuthzFor`, `PermissionMeta` abbreviate (user rule: no abbreviations). | — |
| D17 | generator | Route-vs-message detection by base-class **simple name** (`"ViewModel"` anywhere matches). Generated rows call `Permission.Parse(literal)` instead of the catalog accessor. | `AuthorizationGenerator.cs:26-27, 128-134, 200-203` |
| D18 | hot path | `AuthorizationResult`/`ActionGate` use the native `union` keyword with record cases on a per-dispatch path; `Requirement` is a fat struct (permission + role array + claim + policy + delegate); `Denied` can name only one missing permission. | `.Public/AuthorizationResult.cs`, `Requirement.cs` |
| D19 | consistency | Three ambient "who is calling" sources: `CallContext.Current`, `IPrincipalAccessor` (`AmbientPrincipalAccessor` AsyncLocal / HttpContext), Cqrs `ICacheScopeContext.User`; the audit design plans a fourth (`ActorContext`). | §2.1, §2.2, audit 02 |

## 4. Test baseline (2026-10-02, `dotnet test --artifacts-path C:\Temp\authz-art -p:NuGetAudit=false`)

| Project | Passed | Failed | Skipped |
|---|---|---|---|
| YFex.Authorization.Tests | 41 | 0 | 0 |
| YFex.Authorization.AspNetCore.Tests | 3 | 0 | 0 |
| YFex.Messaging.Tests | 150 | 0 | 2 |
| YFex.Messaging.Rpc.Tests | 41 | 0 | 0 |
| YFex.NavigatR.Tests | 129 | **5 (pre-existing, unrelated: StringRouteTests ×3, NavigatorHostTests.SwitchContextAsync…, NavigatorNavigationTests.NavigateTo_Simple_DirectionIsInitialOnFirstNav)** | 0 |
| YFex.Rules.Tests | 64 | 0 | 0 |
| YFex.Rules.Admin.Tests | 22 | 0 | 0 |
| YFex.Capabilities.Tests / .Cqrs.Tests / .Generator.Tests | 28 / 7 / 5 | 0 | 0 |
| YFex.Tenancy.Tests | 27 | 0 | 0 |
| YFex.Web.Data.Tests | 30 | 0 | 0 |

Users of the Cqrs authorize verbs outside tests: only `YFex.Rules.Admin/RuleAdminSetup.cs` (`RequireAuthenticated`). Breaking the
Cqrs authorize API therefore costs little.
