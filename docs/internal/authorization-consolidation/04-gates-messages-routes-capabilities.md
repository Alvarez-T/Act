# 04 — Gates: messages, routes, view-model commands, capabilities, HTTP

## 1. Principle: declare once, one table, every surface asks the same question

```
 declare                                   compile (startup)                        ask (runtime)
 ───────                                   ─────────────────                        ─────────────
 Cqrs aggregate configuration ─┐                                                    ┌─ LocalDispatcher / ValidatingHandlerInvoker /
 IAuthorizationConfiguration ──┼──► CompiledAuthorizationRegistry ──► CompiledPolicy├─ CallContextMiddleware / Wolverine  (authoritative)
 [RequirePermission] on VMs,  ─┤     (catalog check, PolicyKey →                    ├─ AspNetCore endpoint filter          (authoritative)
   StateCommands, endpoints    │      CompiledPolicy, frozen)                       ├─ FusionMessageBus (client, UX only)  (visibility)
 Rules/RuleAdmin exports ──────┘                                                    ├─ Navigator (before VM construction)  (visibility)
                                                                                    ├─ VM command CanExecute               (visibility)
                                                                                    └─ Capabilities / Search / AI agent     (visibility + verdict)
```

`PolicyKey` = message type | route view-model type | (view-model type, command member) | capability key. The same compiled policy
answers the visibility question (sync, hidden when undecidable) and the authoritative question (async-capable).

## 2. CQRS (fixes D1, D2, D3, D4, D5)

### 2.1 Before

```csharp
// ✍ Today — Cqrs vocabulary, ClaimsPrincipal, string roles/policies
b.Command<VoidSale, Receipt>()
    .RequirePermission(SalesPermissions.SaleVoid)                // checked through IPolicyEvaluator…
    .RequireRoles("Supervisor")                                         // …but this is a case-sensitive claim scan (D4)
    .RequireAuthorization("cashier-session-open")                       // ALWAYS denies on dispatch (D3)
    .Authorize((user, cmd) => cmd.Total <= 500m || user.IsInRole("Manager"));

[RequirePermission(SalesPermissions.Ids.SaleVoid)]                          // fills ByMessageType, which dispatch never reads (D1)
public sealed partial record VoidSale(…) : ICommand<Receipt>;
```

Runtime today: `CompiledMessagingRegistry` compiles a `Func<ClaimsPrincipal, object, bool>`. Every dispatcher calls it through
`CallAuthorization.Authorize`, which stashes the already-resolved context in a `[ThreadStatic]` so the evaluator does not rebuild it.

### 2.2 After

```csharp
// ✍ Dev writes — same place (aggregate configuration), typed, one vocabulary
b.Command<VoidSale, Receipt>()
    .RequirePermission(SalesPermissions.SaleVoid)
    .RequireRoles(StoreRoles.Supervisor)                                     // Role value object from an app roles class
    .Authorize((caller, command) => command.Total <= 500m || caller.IsInRole(StoreRoles.Manager))   // IAuthorizationContext, not ClaimsPrincipal
    .Authorize<OpenCashierSessionRequirement>();                             // DI singleton, IAsyncAuthorizationRequirement<VoidSale>

public sealed class OpenCashierSessionRequirement(ICashierSessions sessions) : IAsyncAuthorizationRequirement<VoidSale>
{
    public PermissionSet VisibleWith => PermissionSet.Empty;          // visibility hint: no resource → do not hide (see §2.4)
    public ValueTask<bool> IsSatisfiedAsync(IAuthorizationContext caller, VoidSale command, CancellationToken cancellationToken)
        => sessions.IsOpenAsync(command.TerminalId, caller.Actor.Subject, cancellationToken);
}
```

```csharp
// ⚙ What Cqrs does with it (adapter, ~80 lines replacing AuthorizerDescriptor + BuildCompositeAuthorizer)
// The builder verbs append Requirement values to the message accumulator. After Build, the messaging registry exposes them as an
// IAuthorizationConfiguration (contract moved to .Public so Cqrs needs no runtime reference):
internal sealed class CqrsAuthorizationConfiguration(CompiledMessagingRegistry registry) : IAuthorizationConfiguration
{
    public void Configure(AuthorizationBuilder builder)
    {
        foreach (var (messageType, requirements) in registry.AuthorizationRequirements)
            builder.ForMessage(messageType).Add(requirements);       // merged with ForMessage<T>() and endpoint rows → ONE policy
    }
}
```

```csharp
// ⚙ Every dispatcher (LocalDispatcher, ValidatingHandlerInvoker, CallContextMiddleware, WolverineLocalDispatcher): one line
var verdict = await _authorization.AuthorizeAsync(command, _caller.Current, cancellationToken);   // completes synchronously when IsSyncOnly
if (!verdict.IsAllowed) return QueueableResult<TResult>.From(Errors.From(verdict));            // 401/403/approval/justification (03 §5)
```

**Result:** one policy per message type, built from configuration, `ForMessage<T>()` and (for HTTP request types) attributes.
Catalog-checked at startup. Enforced by every dispatcher and the HTTP filter. Named policies and requirement classes work because
dispatch uses the async authoritative path. The `[ThreadStatic]`, the principal factory, the second registry build
(`AddYFexCqrsAuthorization`) and the Cqrs authorizer types are deleted.

**Fail-closed rule:** if the messaging registry contains any authorization requirement and no `IMessageAuthorization` is registered,
the dispatcher constructor throws at startup ("call AddYFexAuthorization"). Today it silently denies at runtime.

**Behaviour change (release note):** message types that carried `[RequirePermission]` or `ForMessage<T>()` rows were not enforced
on CQRS dispatch (D1) and now are. Before Phase 2 lands, grep every app for these declarations.

### 2.3 Attributes on CQRS records (⚑Z6)

Decision (confirmed): **no analyzer for now.** `[RequirePermission(SalesPermissions.Ids.SaleVoid)]` on an `ICommand`/`IQuery` record
keeps working; its row joins the same message policy as the configuration (merged with AND) and is now enforced on dispatch (Z5).
Configuration stays the recommended place (YFex convention "configuration over attributes for CQRS"). AUTH005 is reserved in case
this is tightened later.

### 2.4 Visibility of resource requirements

A capability list or menu has no message instance, so resource requirements cannot be decided there. The rule stays "hidden when
undecidable", with one refinement so useful actions do not vanish:

- `IAuthorizationRequirement<T>.VisibleWith : PermissionSet` — the permissions that *could* satisfy the requirement. Visible when the
  caller holds any of them. `Empty` = "does not affect visibility" (e.g. "cashier session open" is checked at execution only).
- Inline `.Authorize((caller, message) => …)` lambdas cannot declare a hint, so they **do not affect visibility** (documented). Use a
  requirement class when visibility matters.

### 2.5 Rules.Admin: from handler checks to configuration (fixes D12 for RuleAdmin)

```csharp
// ✍ Before — RuleAdminSetup.cs: AI/search sees "authenticated only"; the handler demands a string permission at the end
b.Command<Commands.PublishTable, long>().RequireAuthenticated()…;
// RulePublisher.Demand(user, PermissionFor(host, RuleDataKind.Table, code))  → Permission.TryParse(string) per call
```

```csharp
// ✍ After — the publish permission is a resource requirement (each table names its own Permission), visible to whoever holds any
b.Command<Commands.PublishTable, long>().Authorize<TablePublishRequirement>()…;
b.Command<Commands.PublishParameters, long>().RequirePermission(RuleAdminPermissions.ParametersPublish)…;
b.Command<Commands.PublishRule, long>().RequirePermission(RuleAdminPermissions.RulesPublish)…;
b.Command<Commands.SetRuleOverride, long>().RequirePermission(RuleAdminPermissions.Override)…;

internal sealed class TablePublishRequirement(RuleSetRegistry registry) : IAuthorizationRequirement<Commands.PublishTable>
{
    public PermissionSet VisibleWith => registry.AllTablePublishPermissions;          // computed once at startup
    public bool IsSatisfied(IAuthorizationContext caller, Commands.PublishTable command)
        => caller.HasPermission(registry.PublishPermissionFor(command.RuleSet, command.TableCode));   // typed Permission (.Public)
}
```

`RuleAdminOptions` keeps typed overrides (`Permission TablePublishFallback = RuleAdminPermissions.TablesPublish`), so an
app can still remap them, and they are catalog-checked.

## 3. NavigatR routes (keep, small changes)

Dev code is unchanged: `[RequirePermission(…)]` on the page view model, or `b.ForRoute<TViewModel>()`. Changes:

- the generator recognises view models by the full metadata name of `YFex.State.StateObject` in the base chain, not by simple names
  (D17), and emits catalog accessors instead of `Permission.Parse` (AUTH006);
- `NavigationDenied` gets its text from the verdict (display name of the missing permission, from the manifest);
- optional (Phase 5): the navigator subscribes to `AuthorizationSignals` (05 §4) and re-checks the active page when the caller's
  permissions change; a page that became forbidden is closed with `NavigationDenied`.

## 4. View-model commands (fixes D14, ⚑Z19)

```csharp
// ✍ Dev writes — one attribute on the command method (classification on the member that IS the action; no config class for VMs)
public partial class SaleViewModel : PageViewModel
{
    [StateCommand, RequirePermission(SalesPermissions.Ids.SaleVoid)]
    private async Task VoidAsync(CancellationToken cancellationToken) { … }
}
```

```csharp
// ⚙ Generated by the Authorization generator — implements a State-generator hook (same pattern as validation D22)
partial class SaleViewModel
{
    partial void __OnCanExecute_Void(ref bool canExecute)
        => canExecute &= AuthorizationGate.IsVisible(this, AuthorizationPolicyKeys.SaleViewModel_Void);
}
```

**Result:** the button is disabled for callers without `sales.void`, and the command refuses to execute. The server still enforces
the `VoidSale` message policy. The State generator needs one new hook (`__OnCanExecute_<Command>`) and a re-evaluation trigger when
`AuthorizationSignals.Changed` fires (same mechanism as `[ReactsTo]`). Capabilities already read the same attribute for search/AI.

## 5. Capabilities, search, AI agent (fixes D12, D13)

```csharp
// Before — one permission per capability
if (CurrentAuthz().TryGet(key, out var permission) && !_policy.IsAllowed(permission, ctx)) return ActionGate.Deny(permission);

// After — the full compiled policy of the underlying message/route/command
var policy = _policies.For(capability.PolicyKey);                         // resolved once at registry build, cached on the capability
var verdict = _evaluator.Authorize(policy, caller);                       // sync; async-only parts → hidden
return verdict.Kind switch
{
    AuthorizationVerdictKind.Allowed when capability.SideEffect == SideEffect.Irreversible => ActionGate.NeedsConfirmation(…),
    AuthorizationVerdictKind.Allowed          => ActionGate.Allow(),
    AuthorizationVerdictKind.ApprovalRequired => ActionGate.NeedsApproval(verdict.Approvals),          // new case
    _                                         => ActionGate.Deny(verdict.Denial),
};
```

`IAuthorizationView`/`AuthorizationView` and `Authz`/`AuthzFor` are removed. `CapabilityDescriptor.RequiredPermission` stays as a
display hint only. `ReadRequirePermissionAttribute` (reflection) is removed: VM capabilities get the permission from the Capabilities
generator (already compile-time), CQRS capabilities from the registry. `CapabilitySearchService` and
`YFex.Search.Capabilities.CapabilityCatalogProvider` both call `IPolicyEvaluator.IsVisible`. `ActionGate` becomes a `[Union]` struct
(per-call path, D18).

## 6. Inbound HTTP (`YFex.Authorization.AspNetCore`)

- New `app.UseYFexAuthorization()` middleware (after `UseAuthentication`) resolves the caller context once per request (cache, 03 §3).
  It enters `ActorContext` and handles impersonation (06 §3) when Tenancy is wired.
- The endpoint filter calls `IMessageAuthorization` with the request object, so resource requirements see it. Responses:
  401 / 403 `application/problem+json` with `missing` (id + display name) / approvals / justification.
- `HttpContextPrincipalAccessor.Current` only reads the cached context (D6).
