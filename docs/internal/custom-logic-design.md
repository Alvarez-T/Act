# YFex.Tenancy + YFex.CustomLogic — Design & Plan

**Status:** P0 + P1 **implemented and tested** (2026-09-23); P2 (NavigatR) and P3 (Avalonia) still
design-only. See §8 for what is built. Adds a per-scope
customization mechanism on top of the shipped `YFex.Cqrs` (`ICommandHandler<>` seam,
`LocalDispatcher`, the frozen `CommandPolicy` registry), `YFex.NavigatR` (`RouteRegistry`
route→ViewModel map), `YFex.Web` tenancy contracts (`ITenantInfo`, `ICurrentTenantProvider`),
and `YFex.Avalonia` (templated controls + C# markup). Mirrors the provider-seam + frozen-table +
generator pattern of `YFex.Cqrs.SourceGenerator` and `YFex.EventSourcing`.

> Illustrative API — names are placeholders pending implementation.

> **Naming note.** This doc uses **`YFex.Tenancy`** for the identity library (the name we agreed
> was "more specific"). Your action message wrote `YFex.Tenant`; if you prefer the singular,
> rename throughout — it changes nothing structural. See §10, D1.

---

## 0. Goals & non-negotiable principles

Let a specific **user, tenant, or organization** get custom behavior or a custom UI for a feature
that does *not* fit the default handler and does *not* fit a Strategy — **without** copy-pasting
the component and **without** the core ever writing `if (user == "acme")`. The framework wires the
selection; the author only writes the delta.

Two axes are kept strictly separate (collapsing them is the code smell this replaces):

- **Identity — "who am I?"** The `Organization → Tenant → User` hierarchy. This is *data*. It lives
  in **`YFex.Tenancy`**. It knows nothing about overrides.
- **Mechanism — "what specialized code runs?"** The override engine. It lives in
  **`YFex.CustomLogic`**. It knows nothing about *how* a tenant is discovered.

**Locked decisions (this doc is written against them):**

- **Specificity order: `User → Tenant → Organization → default`. Most-specific wins, single
  winner for logic.** The resolver walks User first; the first registered `[CustomLogic]` that
  matches the current scope chain is used; less-specific matches are ignored. (Additive UI
  contributions are a separate opt-in — §6.2.)
- **Discovery is generator-scanned.** An incremental generator finds every `[CustomLogic]` type,
  emits registration + a frozen dispatch table, and raises `YFCUST` diagnostics for collisions.
  No runtime assembly scanning, no reflection.

**Principles (apply to every type):**

- **AoT-first.** Both libraries set `<IsAotCompatible>true</IsAotCompatible>`. Zero runtime
  reflection on any path — no `Expression.Compile`, `MakeGenericMethod`/`MakeGenericType`,
  `Activator`, `DynamicInvoke`. Every override is invoked through a `static` typed delegate
  captured by the generator where the generic parameters are in scope — the exact trick already
  used by `ReverseRuleDescriptor.FromHandler` / `OfflineHandlerDescriptor.FromType` in
  `CommandConfigurationBuilder`.
- **Zero/low allocation on the hot path.** The registry is a `FrozenDictionary` built once at
  startup. Keys are blittable `readonly record struct`s with interned `int` scope values (no
  per-dispatch string hashing) and a precomputed hash. The scope chain is at most 3 deep and is
  resolved into a stack buffer (`[InlineArray(3)]` / `stackalloc Span<ScopeKey>`) — never a
  `List`. All lambdas are `static`. **The 95% path — no override for this (target, scope) — is a
  single failed `TryGetValue` and zero allocations.**
- **Own primitives.** `Scope`, `ScopeValue`, `ScopeKey`, `CustomLogicKey` are value types, not
  loose `string`/`int` — same convention as `SagaId`/`PropertyBitmap64`.
- **Additive to the framework.** Core command handlers and core pages never name a tenant. A new
  customer ships as a *new assembly*; core is not recompiled (open/closed).
- **No customization inside `.axaml`.** Every UI override is either a *resource swap*
  (declarative, native Avalonia cascade) or a *C# contribution/slot/page* (imperative, testable).
  XAML stays the default look.

---

## 1. Layering & projects

```
L1  YFex.Tenancy               Identity & scope model. Scope, ScopeValue, ScopeKey, IScopeContext,
                               ambient accessor, ScopeValueInterner. Neutral. AoT.
                               refs: YFex (core) only. NO Web, NO UI, NO Cqrs.

L2  YFex.CustomLogic           The override engine. ICustomLogic<...> seam, [CustomLogic] attribute,
                               CustomLogicKey, CustomLogicRegistry (frozen), the resolution walk.
                               refs: YFex.Tenancy. AoT. NO host frameworks.

Gen YFex.CustomLogic.Generator netstandard2.0 incremental generator. Scans [CustomLogic],
                               emits AddYFexCustomLogic(IServiceCollection) + the frozen descriptor
                               array (static typed delegates) + the interner seed. Raises YFCUST.
```

**Dependency direction (the layering check):**

```
YFex.CustomLogic   ──►  YFex.Tenancy (scope model)
YFex.Tenancy       ──►  YFex (core primitives only)

YFex.Cqrs          ──►  YFex.CustomLogic   (new, additive — one hook in LocalDispatcher; §3.4)
YFex.NavigatR      ──►  YFex.Tenancy       (new, additive — scoped route overlay; §4)
YFex.Mvvm          ──►  YFex.CustomLogic   (UI view swap + slot application; §5)
YFex.Web           ──►  YFex.Tenancy       (implements IScopeContext from ICurrentTenantProvider)
YFex.Avalonia      ──►  YFex.CustomLogic   (page swap, slots, fields, theme selection; §5)
```

Hosts provide the *discovery* of the current scope; the engine provides *selection*. YFex.Web keeps
its whole resolver/middleware/store stack — it only additionally implements `IScopeContext`.

---

## 2. YFex.Tenancy — identity & scope

> **⚠ GENERIC MODEL (revised 2026-09-24).** The framework fixes **no** hierarchy. The original
> `enum Scope { Organization, Tenant, User }` baked one company's structure into a shared library and
> is **removed**. Levels are now opaque **byte ranks** (higher = more specific); the consuming app
> defines its own levels with its own enum and annotates via the **generic attribute**
> `[CustomLogic<TScope>(...)]`. Throughout the examples below, `Scope.Organization/Tenant/User` are
> **illustrative of one app's levels**, not framework types — read them as an app's
> `enum StoreScope : byte { Region, Store, Terminal }` etc. See `web-tenancy-consolidation-plan.md` §0.

### 2.1 The scope model

```csharp
namespace YFex.Tenancy;

// No framework Scope enum. A rung's level is an opaque byte rank (higher = more specific);
// the APP defines the levels via its own byte-backed enum.

/// One rung of the current identity: (rank, interned value). Blittable, cache-friendly.
public readonly record struct ScopeKey(byte Rank, int ValueId);

/// The raw (un-interned) rung. Build from the app's own enum: ScopeValue.Of(MyScope.Store, "s-42").
public readonly record struct ScopeValue(byte Rank, string Value)
{
    public static ScopeValue Of<TScope>(TScope level, string value) where TScope : struct, Enum;
}

/// The ambient identity as an ORDERED SET OF RUNGS (most-specific-first) — not fixed named fields.
public readonly struct ScopeIdentity   // .Rungs (ReadOnlySpan<ScopeValue>), .Of(params ScopeValue[]), .None
{
    public const int MaxLevels = 8;
}
```

### 2.2 Ambient scope — `IScopeContext`

The one thing a host must supply. It fills a caller-owned stack buffer, **most-specific first**,
and returns how many rungs are present. No allocation, no `IEnumerable`.

```csharp
public interface IScopeContext
{
    /// Writes the current chain into `buffer` in User→Tenant→Org order; returns the count (0–3).
    /// A user with no org context yields just [User]; an anonymous server job yields 0.
    int Resolve(Span<ScopeKey> buffer);
}
```

- **Server / background:** `AsyncLocalScopeContext` (mirrors `AsyncLocalTenantContextAccessor`
  already in `YFex.Web.Data`).
- **ASP.NET Core:** adapter over `ICurrentTenantProvider.Current` (+ the authenticated user's id /
  org claim). Lives in `YFex.Web`, not in `YFex.Tenancy`.
- **Avalonia:** per-window/session accessor over the logged-in principal (§5.5).

### 2.3 Interning scope values

Raw scope strings (`"acme"`, `"globex-west"`, `"u-42"`) are interned to `int` ids **once at
startup** by `ScopeValueInterner`, seeded by the generator from every `[CustomLogic]` literal plus
any host-registered dynamic values. The hot path only ever compares `int`s. Unknown runtime values
(a tenant with no override) resolve to a sentinel `-1` that can never match a table key — so their
lookups fail fast without polluting the interner.

### 2.4 Identity records (optional, host-facing)

`Organization`, `Tenant`, `User` `readonly record struct` ids, and an optional
`IOrganizationScoped : ITenantInfo { string OrganizationId { get; } }` that richer apps implement
so the Web adapter can derive the Org rung from the current tenant. Additive to the existing
`ITenantInfo` (which stays generic — see its own doc comment anticipating "organization +
establishment").

---

## 3. YFex.CustomLogic — the override engine

### 3.1 The seam

Two shapes over one idea. Logic returns a result-or-fall-through; UI mutates in place.

```csharp
namespace YFex.CustomLogic;

/// Behavioral override (server). Return a value to short-circuit the default handler,
/// or `Fallthrough` to defer to the default (or a less-specific match, per §6).
public interface ICustomLogic<TInput, TResult>
{
    ValueTask<CustomResult<TResult>> HandleAsync(TInput input, CancellationToken ct);
}

/// UI/composition override. Mutates the already-built component (slot injection, field add, …).
public interface ICustomLogic<TTarget>
{
    void Apply(TTarget target);
}

/// A tiny non-boxing union: Handled(value) | Fallthrough.
public readonly struct CustomResult<T> { /* [Union]-style; no allocation */ }
```

### 3.2 The attribute

```csharp
// GENERIC over the app's own level enum — the framework names no levels.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CustomLogicAttribute<TScope>(TScope scope, string value) : Attribute
    where TScope : struct, Enum
{
    public TScope Scope { get; } = scope;
    public byte   Rank  { get; } = Convert.ToByte(scope);  // the enum's underlying byte = specificity
    public string Value { get; } = value;
    /// UI-only: opt into additive application (all matching run) instead of single-winner. §6.2.
    public bool   Additive { get; init; }
}
```

```csharp
// The app declares its OWN levels (byte-backed enum; higher = more specific):
enum OrgScope : byte { Organization = 0, Tenant = 1, User = 2 }   // one app's choice

[CustomLogic<OrgScope>(OrgScope.Organization, "acme")]          // whole org
public sealed class AcmeOrderLogic : ICustomLogic<PlaceOrder, OrderPlaced> { ... }

[CustomLogic<OrgScope>(OrgScope.Tenant, "globex-west")]         // one tenant under Globex
public sealed class GlobexWestOrderLogic : ICustomLogic<PlaceOrder, OrderPlaced> { ... }

[CustomLogic<OrgScope>(OrgScope.User, "u-42")]                  // one power user
public sealed class VipOrderLogic : ICustomLogic<PlaceOrder, OrderPlaced> { ... }
```

### 3.3 The frozen registry

```csharp
/// Blittable, precomputed-hash key. Target is the command/page CLR type being overridden.
public readonly record struct CustomLogicKey(Type Target, Scope Kind, int ValueId);

public sealed class CustomLogicRegistry
{
    private readonly FrozenDictionary<CustomLogicKey, CustomLogicEntry> _map;   // built once

    /// Most-specific-first walk. `chain` is the stack buffer from IScopeContext.
    public bool TryResolve(Type target, ReadOnlySpan<ScopeKey> chain, out CustomLogicEntry entry)
    {
        for (int i = 0; i < chain.Length; i++)                 // User, then Tenant, then Org
            if (_map.TryGetValue(new(target, chain[i].Kind, chain[i].ValueId), out entry))
                return true;                                    // first hit wins (locked)
        entry = default;
        return false;                                           // → default handler / page
    }
}

/// The typed, reflection-free invoke captured by the generator.
public readonly struct CustomLogicEntry
{
    public readonly Type ImplType;
    // Server form: resolve impl from sp, call typed HandleAsync, return erased CustomResult.
    public readonly Func<IServiceProvider, object, CancellationToken, ValueTask<object?>>? Invoke;
    // UI form: resolve impl, call typed Apply(target).
    public readonly Action<IServiceProvider, object>? Apply;
}
```

### 3.4 CQRS integration — one hook, zero new dispatcher concepts

The dispatcher consults the registry immediately before it resolves the core
`ICommandHandler<>`. A miss (the common case) is one failed `TryGetValue`; the pipeline is
otherwise unchanged, so custom logic still inherits validation, authorization, resilience,
invalidation, and the offline outbox for free.

```csharp
// inside LocalDispatcher.CommandAsync<TCommand,TResult>, before core handler resolution:
int n = _scope.Resolve(stackalloc ScopeKey[3]);
if (_custom.TryResolve(typeof(TCommand), chain[..n], out var e))
{
    var erased = await e.Invoke!(_sp, cmd, ct);
    if (erased is TResult handled) return handled;   // Handled → short-circuit
    // Fallthrough → continue to core handler below
}
return await _coreHandler.HandleAsync(cmd, ct);
```

Replace / wrap / conditionally-defer are all expressible: an impl that returns `Handled` replaces
core; one that injects the core handler and calls it returns `Handled(wrapped)`; one that returns
`Fallthrough` under some condition defers. (A merged partial class could do none of these — this is
why the seam beats the partial-file idea.)

### 3.5 What the generator emits

`YFex.CustomLogic.Generator` (`ForAttributeWithMetadataName("YFex.CustomLogic.CustomLogicAttribute")`)
discovers every impl and emits, into the consuming assembly:

```csharp
public static partial class GeneratedCustomLogic
{
    public static IServiceCollection AddYFexCustomLogic(this IServiceCollection s)
    {
        s.AddScoped<AcmeOrderLogic>();
        s.AddScoped<GlobexWestOrderLogic>();
        s.AddScoped<VipOrderLogic>();

        return s.AddSingleton(CustomLogicRegistry.Build(
            interned: new[] { "acme", "globex-west", "u-42" },       // interner seed
            entries: new CustomLogicDescriptor[]
            {
                new(typeof(PlaceOrder), Scope.Organization, "acme",  static (sp, cmd, ct) =>
                    sp.GetRequiredService<AcmeOrderLogic>()
                      .HandleAsync((PlaceOrder)cmd, ct).Erase()),
                new(typeof(PlaceOrder), Scope.Tenant, "globex-west", static (sp, cmd, ct) =>
                    sp.GetRequiredService<GlobexWestOrderLogic>()
                      .HandleAsync((PlaceOrder)cmd, ct).Erase()),
                new(typeof(PlaceOrder), Scope.User, "u-42",          static (sp, cmd, ct) =>
                    sp.GetRequiredService<VipOrderLogic>()
                      .HandleAsync((PlaceOrder)cmd, ct).Erase()),
            }));
    }
}
```

`EquatableArray` for incremental caching; the emitted delegates are `static` (no closures);
`Type.GetType`/`MakeGenericMethod` never appear.

### 3.6 Analyzers (`YFCUST`)

- **YFCUST001** — two `[CustomLogic]` claim the same `(Target, Scope, Value)`. Compile error
  (mirrors `YFCACHE`/`YFQUE`). This is the "last-write-wins surprise" guard.
- **YFCUST002** — impl's seam `TInput`/`TTarget` is not a registered command / known page type.
- **YFCUST003** — impl is not `sealed`, or not resolvable from DI (missing accessible ctor).
- **YFCUST004** — `Additive = true` on a behavioral (`ICustomLogic<TInput,TResult>`) impl, where
  single-winner is mandatory (additive only applies to UI contributions).

---

## 4. NavigatR route changes

**Reality check (from `src/Navigation/AGENTS.md`):** the NavigatR generator is *not* wired and
`NavigatRRegistration.RegisterAll` cannot work across assemblies; the working path is manual
`registry.Register<TRoute, TViewModel>()`. So the scope overlay must be **additive and
manual-registration-friendly** — it must not depend on the route generator being fixed.

### 4.1 Scoped route overlay

`RouteRegistry` today is `Dictionary<Type, Type>` route→VM (`ResolveViewModel`). Add a second,
scope-keyed overlay and a scope-aware resolve overload. The base map and existing API are
untouched (single-tenant apps pay nothing).

```csharp
public sealed partial class RouteRegistry
{
    private FrozenDictionary<RouteScopeKey, Type>? _scoped;   // (routeType, ScopeKey) → VM type
    private readonly record struct RouteScopeKey(Type Route, Scope Kind, int ValueId);

    public void RegisterFor<TRoute, TViewModel>(Scope scope, string value)
        where TRoute : IRoute where TViewModel : INavigable { /* add to builder */ }

    /// Scope-aware resolution. Falls back to the base route→VM map on a miss.
    public Type ResolveViewModel(IRoute route, ReadOnlySpan<ScopeKey> chain)
    {
        var t = route.GetType();
        for (int i = 0; i < chain.Length; i++)                        // User → Tenant → Org
            if (_scoped is not null &&
                _scoped.TryGetValue(new(t, chain[i].Kind, chain[i].ValueId), out var vm))
                return vm;
        return ResolveViewModel(route);                               // existing behavior
    }
}
```

`Navigator` resolves the chain from an injected `IScopeContext` and calls the new overload; when no
`IScopeContext` is registered it uses the old single-arg path verbatim. Pooling/eviction are
unaffected — a scoped VM is still keyed by its concrete VM type in `NavigablePool`; mark the route
`IKeepAlive` where re-running `OnNavigation` on a swapped VM is wrong.

### 4.2 Layering note

This adds `YFex.NavigatR → YFex.Tenancy` (L2→L1, allowed). `YFex.Tenancy` is host-neutral and pulls
in nothing NavigatR forbids (no `YFex.Mvvm`, no UI framework). **Alternative if you want NavigatR to
stay reference-pure:** put the overlay in a thin `YFex.NavigatR.CustomLogic` bridge, or perform the
swap one layer up in `YFex.Mvvm` (which already references more). Default recommendation: the direct
`YFex.Tenancy` reference — it is the smallest change and keeps resolution in one place. (§10, D3.)

---

## 5. Avalonia — four grains of UI customization

All four read the **same** `IScopeContext` chain. Pick per feature; mix freely.

### 5.1 Whole-page swap — "this org gets a different page"

Combine §4.1 (route→VM by scope) with a scope-aware **view locator** (VM→View). The org's page may
be a standalone `UserControl`/`YPage`, or subclass the base page and override only the slots it
needs (C# markup makes this a `virtual` method override, not a copy).

```csharp
// default: OrderRoute → OrderViewModel → OrderPage
// acme:    OrderRoute → AcmeOrderViewModel → AcmeOrderPage
registry.Register<OrderRoute, OrderViewModel>();
registry.RegisterFor<OrderRoute, AcmeOrderViewModel>(Scope.Organization, "acme");
```

The view locator overlay is the exact same `(TargetType, ScopeKey) → Type` frozen-table shape,
built by the same generator from `[CustomLogic(Scope.…, "…")]` on `IViewFor<TViewModel>` impls.

### 5.2 Slot / region injection — "extra fields only they need"

A page exposes named regions (a `SlotHost` control, or `PART_*` presenters — `YPage` already does
this for command bars in `OnApplyTemplate`). An `ICustomLogic<OrderPage>` contributes controls into
a slot for its scope; the base page never names the tenant.

```csharp
[CustomLogic(Scope.Tenant, "globex-west")]
public sealed class GlobexOrderFields : ICustomLogic<OrderPage>
{
    public void Apply(OrderPage page) =>
        page.Slot("extras").Add(new YTextBox().Label("PO Number").Bind(...));
}
```

Applied in the page's `OnApplyTemplate` / activation: resolve the chain, `TryResolve`, `Apply`.

### 5.3 Field contributions — data-driven forms, zero XAML

For form-heavy pages, an `IFieldContribution` per scope yields field *descriptors* (name, type,
binding, validation) that a generic form renderer materializes. Orgs add fields declaratively; no
`.axaml` is touched and no page is subclassed. Scales best when many orgs each want a few fields.

### 5.4 Theme / resource merge — look only, native, no codegen

A scope selects a `ResourceDictionary` merged **last** (Avalonia's cascade is last-wins), overriding
only the keys that differ — reusing the existing `Themes/YFex` · `Emerald` · `Dark` machinery. A
tenant theme is a thin overlay, not a copied `ControlTheme`. This grain needs no generator and no
`YFex.CustomLogic` at all — it is pure Avalonia driven by the scope.

### 5.5 Ambient scope in Avalonia

A `WindowScopeContext : IScopeContext` derived from the desktop session's logged-in principal
(user id, tenant, org), refreshed on sign-in / tenant switch. Same interface the server implements —
the UI grains and the CQRS hook share one notion of "current scope".

### 5.6 Recommended combination

Page-swap (5.1) for wholesale org-specific screens · slot-injection (5.2) for additive fields on
shared pages · theme merge (5.4) for look. 5.3 when the form is already data-driven.

---

## 6. Resolution semantics (locked)

### 6.1 Behavioral (single winner)

Walk **User → Tenant → Organization**; first `[CustomLogic]` match wins; the rest are ignored;
no match → default handler/page. One result, predictable, CSS-specificity-like. This is the locked
rule and applies to all `ICustomLogic<TInput,TResult>`.

### 6.2 UI contributions (additive, opt-in)

Slot/field injection is naturally additive — an org *and* a tenant may each want to add a field.
For `ICustomLogic<TTarget>` (UI), `[CustomLogic(..., Additive = true)]` opts into "**all** matching
run, applied **least-specific first** (Org → Tenant → User)" so the most-specific contribution is
applied last and can override earlier ones. Default remains single-winner to honor §6.1;
`Additive = true` is rejected on behavioral impls (YFCUST004).

---

## 7. Performance & AoT summary

| Concern | Strategy |
|---|---|
| Lookup | `FrozenDictionary<CustomLogicKey, Entry>`, one per registry, built at startup |
| Key | `readonly record struct` (Type, Scope, int) — interned value, precomputed hash |
| Scope chain | ≤3 rungs into `stackalloc Span<ScopeKey>` / `[InlineArray(3)]`; no `List`, no `IEnumerable` |
| Invoke | `static` typed delegate captured by generator; no reflection, AoT-clean |
| Miss path (95%) | single failed `TryGetValue`, zero allocation, straight to default |
| Discovery | generator-emitted registration array; no startup assembly scanning |
| Closures | none — every emitted lambda is `static` |
| Result | non-boxing `CustomResult<T>` union (server); `void` (UI) |

Both libraries: `<IsAotCompatible>true</IsAotCompatible>`, no `Expression.Compile`,
no `MakeGenericMethod`/`MakeGenericType`, no `Activator`.

---

## 8. Phasing

- **P0 — `YFex.Tenancy`. ✅ DONE.** `Scope`, `ScopeKey`, `ScopeValue`, `ScopeIdentity`,
  `IScopeContext`, `AsyncLocalScopeContext`, `ScopeValueInterner`, `AddYFexTenancy`. Builds AoT-clean.
  (Web adapter over `ICurrentTenantProvider` still to add when P2/Web lands — §10 D5.)
- **P1 — `YFex.CustomLogic` + generator + CQRS bridge. ✅ DONE.** Seam (`ICustomLogic<,>` /
  `ICustomLogic<>`), `CustomResult<T>`/`CustomOutcome`, `[CustomLogic]`, `CustomLogicRegistry`
  (frozen), `ICustomLogicSource` aggregation, `AddYFexCustomLogic`. Generator
  (`YFex.CustomLogic.Generator`) scans `[CustomLogic]`, emits per-assembly `ICustomLogicSource` +
  `AddCustomLogic_<Assembly>()`, raises `YFCUST001–004`. CQRS integration via the optional
  `ICommandCustomLogic` seam in `YFex.Cqrs.Runtime` + one hook in `LocalDispatcher` (both command
  overloads) + the `YFex.CustomLogic.Cqrs` bridge (`AddYFexCommandCustomLogic`). **6 end-to-end tests
  green** (`YFex.CustomLogic.Tests`): User>Tenant>Org precedence, org fallback, no-match→default,
  conditional fallthrough, unknown-value sentinel. Messaging suite: no regression (pre-existing
  `LiveGeneratorTests` timing failures are unrelated).
- **P2 — NavigatR scoped route overlay. ✅ DONE.** `RouteRegistry.RegisterFor<TRoute,TViewModel>(Scope,value)`
  + scope-aware `ResolveViewModel(IRoute, in ScopeIdentity)` (string-keyed overlay, most-specific-first,
  base fallback). `Navigator` resolves an **optional** `IScopeContext` and routes all VM resolution
  (incl. the resume/restore paths) through it — absent → unchanged behavior. `IScopeContext` gained a
  raw `Current` accessor. **6 tests green** (`YFex.NavigatR.Tenancy.Tests`, a standalone project since
  the main `NavigatR.Tests` is path-broken). NavigatR → YFex.Tenancy only (no CustomLogic ref).
- **P3 — Avalonia grains. ◑ PARTIAL.**
  - ✅ **Scope contexts:** `MutableScopeContext` (single-slot, UI/desktop — serves the §5.5
    "WindowScopeContext" role) added to `YFex.Tenancy`; chain-fill factored into `ScopeChain`.
  - ✅ **Composition mechanism (§5.2/§5.3 engine):** `CustomLogicRegistry.Apply(scope, sp, target)` in
    `YFex.CustomLogic` runs the most-specific `ICustomLogic<TTarget>` override against a built component.
    **2 tests green** (field-injection applies for the matching tenant, skipped otherwise) — total
    `YFex.CustomLogic.Tests` now **8**.
  - ✅ **Whole-page swap (§5.1):** `ViewLocator.RegisterFor<TViewModel,TView>(Scope,value)` +
    `ScopeContext` property; a scope override wins over base registration/conventions in `Build`.
    `YFex.Avalonia` builds. (Reflection-based file — not on the AoT hot path.)
  - ☐ **Remaining thin adapters:** a `SlotHost` control + a base-page `OnApplyTemplate` hook that calls
    `registry.Apply(...)`; the `ResourceDictionary`/`ThemeVariant` merge helper (§5.4, pure Avalonia);
    a headless UI test for the view swap. These need the app's "get the registry/scope from a control"
    convention — deferred pending that decision.

**Built layout (extracted to sibling solutions, 2026-09-24):**
- `C:\Dev\YFex\YFex.Tenancy\` — `tenancy.slnx` + `src/YFex.Tenancy` (future `src/YFex.Tenancy.Resolution`, see
  [web-tenancy-consolidation-plan.md](web-tenancy-consolidation-plan.md)).
- `C:\Dev\YFex\YFex.CustomLogic\` — `customlogic.slnx` + `src/YFex.CustomLogic`, `src/YFex.CustomLogic.Generator`,
  `src/YFex.CustomLogic.Cqrs`, `tests/YFex.CustomLogic.Tests`.
- In the main repo: `src/Navigation/YFex.NavigatR.Tenancy.Tests`; edits in `YFex.NavigatR` (RouteRegistry
  overlay + Navigator) and (sibling `YFex.UI`) `YFex.Avalonia` (ViewLocator).
- Cross-repo `ProjectReference`s use `..\..\..\..\` (siblings under `C:\Dev\YFex`). `yfex.slnx` references the
  five moved projects under folder `/Tenancy/` via `../YFex.Tenancy/...` and `../YFex.CustomLogic/...`.

Each phase builds and tests green before the next. P2 and P3 both depend only on P0 (scope) + P1
(engine), so they can proceed in parallel once P1 lands.

---

## 9. What this explicitly does *not* do

- **No per-user override explosion.** `[CustomLogic(Scope.User, …)]` is for a handful of bespoke
  power users. Hundreds of per-user variants belong in *data/config* consumed by the default
  handler, not in the compiled table. (Consider a `YFCUST` info diagnostic if User-scope entries
  for one target exceed a threshold.)
- **No `.axaml` code generation.** UI customization is resource swap or C# contribution only (§0).
- **No new dispatcher concept.** CQRS gains one lookup; the pipeline, policies, and outbox are
  unchanged.
- **No change to how tenants are *discovered*.** That stays in each host; the engine only *selects*.

---

## 10. Open decisions

- **D1 — library name.** `YFex.Tenancy` (this doc) vs `YFex.Tenant` (your message) vs `YFex.Scoping`.
  Structural no-op; pick the word.
- **D2 — additive UI contributions.** Ship §6.2 in P3, or defer additive slot/field injection and
  start single-winner everywhere?
- **D3 — NavigatR reference.** Direct `YFex.NavigatR → YFex.Tenancy` (recommended) vs a
  `YFex.NavigatR.CustomLogic` bridge vs doing the swap in `YFex.Mvvm`.
- **D4 — interning source.** Seed the interner purely from generator literals, or also allow host
  code to register dynamic scope values at startup (needed if tenant/org ids are DB-driven and not
  known at compile time — likely yes for Tenant/User scope).
- **D5 — user identity source on the server.** Which claim is the `User` rung (`sub`, app user id)?
  Affects the Web `IScopeContext` adapter only.
