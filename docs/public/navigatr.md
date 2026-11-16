# YFex.NavigatR

**Library:** `YFex.NavigatR` + `YFex.NavigatR.SourceGenerator`

YFex.NavigatR is a type-safe, async-first navigation framework for .NET applications. It replaces stringly-typed page navigation with immutable route records, exhaustive `NavigationResult` unions that force callers to handle every outcome (success, denied, cancelled), and a source generator that eliminates boilerplate for parameter passing, result plumbing, and DI registration. Routes carry their parameters as record properties -- no dictionaries, no casting, no runtime surprises.

---

## Core Concepts & Mental Model

### Navigator

The `Navigator` is the central orchestrator. It owns a navigation stack (the `Breadcrumb`), resolves ViewModels from routes via DI, drives the `INavigable` lifecycle, and manages a `NavigablePool` that controls memory pressure from suspended screens.

Each `Navigator` instance represents one navigation surface -- a single tab, window, or pane. For multi-surface apps, `NavigatorHost` manages multiple Navigators.

### IRoute

A route is an immutable record that identifies a destination. Parameters live on the record itself -- no separate parameter bags:

```csharp
using YFex.NavigatR;

// Parameterless route
public record HomeRoute : IRoute
{
    public string? DisplayName => "Home";
}

// Route with a parameter
public record ProductRoute(int ProductId) : IRoute;

// Route that produces a typed result
public record PickerRoute : IRouteProduces<PickerResult>;
```

Because routes are records, structural equality works out of the box. Two `ProductRoute(42)` instances are equal, which is what makes prefetch token matching and history deduplication reliable.

### INavigable

Every navigable ViewModel implements `INavigable`. Only the first three members are required; the rest are default no-ops:

```csharp
public interface INavigable : IDisposable
{
    Task OnNavigation(NavigationContext context, CancellationToken ct = default);   // first show; may Deny
    Task OnResume(CancellationToken ct = default);                                  // shown again after a suspend
    Task OnSuspend(CancellationToken ct = default);                                 // left but kept (or user AFK)

    Task OnNavigating(NavigationContext context, CancellationToken ct);             // right before OnNavigation
    Task OnLoaded(NavigationContext context, CancellationToken ct);                 // the route's data arrived; may Deny (LoadFirst)
    Task OnLoadFailed(NavigationContext context, Exception error, CancellationToken ct);
    Task OnShown(CancellationToken ct);                                             // became the visible page (every time)
    Task OnLeaving(LeavingContext context, CancellationToken ct);                   // leave guard: context.Stay(...)
    Task OnActive(CancellationToken ct = default);                                  // back from AFK
}
```

In practice you inherit `PageViewModel` (from `YFex.Mvvm`), which turns `OnShown` into State activation (`[Live]`, `[Subscribe]`), exposes `IsLoading`/`LoadError`, and lets you override `OnLoaded`, `OnLoadFailed` and `OnLeaving`. Add `[Route]` and the generator writes the typed bridges.

### NavigationResult

Navigation outcomes are discriminated unions. The compiler enforces exhaustive handling:

```csharp
public readonly union NavigationResult(
    NavigationSuccess,
    NavigationDenied,
    NavigationCancelled
);

public readonly union NavigationResult<TResult>(
    NavigationSuccess<TResult>,
    NavigationDenied,
    NavigationCancelled
);
```

There is no `null` result, no exception-based control flow, no boolean flags. Every path is explicit.

### NavigationTask (Lazy vs Eager)

`Navigator.NavigateTo()` returns a `NavigationTask`, not a raw `Task`. This gives you two execution modes:

- **Await directly (lazy):** Navigation fires when you `await`. The caller is suspended normally and resumes after `OnNavigation` completes. The result is discarded.
- **`.UntilReturns()` (eager):** Starts immediately. Pins the caller (it cannot be evicted or receive `OnSuspend`/`OnResume`). Waits until the target screen closes via back navigation.
- **`.UntilReturns<TResult>()` (eager):** Same as above, but waits until the target ViewModel calls `Returns()`, `Cancel()`, or `Deny()`.

### NavigablePool

The pool limits how many suspended ViewModels stay alive in memory. When pool capacity is exceeded, the oldest suspended (non-keep-alive) entry is disposed. Entries marked `IKeepAlive` and entries in `Pinned` state are never evicted.

---

## Integration Model & Lifecycle

### RouteRegistry Setup

The `RouteRegistry` is a singleton that maps route types to ViewModel types, and knows each route's data loader and load mode. **Page ViewModels are not registered in DI**: the navigator builds them (constructor dependencies come from the history's DI scope) and the history entry owns and disposes them.

```csharp
using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;

services.AddNavigatR(routes => routes.RegisterGeneratedRoutes(),   // emitted by the generator into the assembly that declares [Route] pages
    new NavigatorOptions { HistoryPolicy = NavigationHistoryPolicy.PruneForwardOnBranch });  // optional: defaults of every history

var provider = services.BuildServiceProvider();
provider.UseNavigatR();                   // publishes the host to UI page hosts (YPageHost); see "UI page hosts" below

// Without the generator, one line per route (inside AddNavigatR's callback):
registry.Register<ProductRoute, ProductViewModel>()
        .WithData((sp, route, ct) => sp.GetRequiredService<IProductApi>().GetAsync(route.ProductId, ct))
        .Load(NavigationLoad.LoadFirst);
```

`AddNavigatR` registers three singletons: `RouteRegistry` (filled by the callback), `NavigatorHost` (with the default `NavigatorOptions`) and `INavigationHost` (the same host, for UI page hosts). Registering `RouteRegistry` and `NavigatorHost` by hand still works.

The generator runs only on projects that reference it as an analyzer (references are not transitive):

```xml
<ProjectReference Include="..\YFex.NavigatR\src\YFex.NavigatR.SourceGenerator\YFex.NavigatR.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

### NavigatorHost for Multi-Context

`NavigatorHost` manages multiple `Navigator` instances for tabbed or multi-pane UIs:

```csharp
using YFex.NavigatR;

var host = sp.GetRequiredService<NavigatorHost>();

// First context is automatically active
var tab1 = host.CreateContext(navPane: myUiHook, poolCapacity: 10);

// Additional contexts start inactive
var tab2 = host.CreateContext(navPane: anotherUiHook, setActive: false);

// Switch between tabs
await host.SwitchContextAsync(tab2.Id);

// Close a tab
host.CloseContext(tab1.Id);
```

When switching contexts, the host suspends the current Navigator's top entry and resumes the target's.

### INavigation (Platform Hook)

`INavigation` lives in `YFex.UI.Public` (repo YFex.UI). UI hosts implement it — YFex.Avalonia's `YPageHost` does — and never reference NavigatR. Pass it to `NavigatorHost.CreateContext(navPane, options)`, or let the UI host create its own history through `INavigationHost` (next section).

```csharp
using YFex.UI.Public;

public interface INavigation
{
    void PerformNavigation(object view);                         // the page instance the history entry owns
    void OnNavigationDenied();
    void OnNavigationDenied(string? reason) => OnNavigationDenied();
    void OnNavigationCleared() { }                               // the history became empty
    bool OnOpenRequested(object route) => false;                 // a page asked for the route in a new history (docking)
    event Action? UserBecameInactive;
    event Action? UserBecameActive;
}
```

### UI page hosts: INavigationHost and sessions

A UI page host gets its own history without referencing NavigatR. `NavigatorHost` implements the `YFex.UI.Public` bridge; `provider.UseNavigatR()` publishes it through an internal locator that the UI libraries read. One page host = one session = one history (one `Navigator`, own DI scope, the host's `DefaultOptions`). Routes cross the boundary as `object` (route records, compared with `Equals`); a string is a deep link.

```csharp
public interface INavigationHost { INavigationSession CreateSession(INavigation pane); }

public interface INavigationSession : IDisposable          // Dispose = close without asking the page
{
    object? Route { get; }  bool CanGoBack { get; }  bool CanGoForward { get; }
    Task<bool> NavigateAsync(object route, CancellationToken ct = default);   // false: denied, cancelled, page stayed
    Task<bool> GoBackAsync(CancellationToken ct = default);
    Task<bool> GoForwardAsync(CancellationToken ct = default);
    Task ActivateAsync(CancellationToken ct = default);      // this history becomes the active one
    Task<bool> CloseAsync(CancellationToken ct = default);   // leave guard (LeaveReason.Close) first; false: page stayed
    event Action<NavigationState>? Navigated;                // (Route, DisplayName, Direction, CanGoBack, CanGoForward)
}
```

The history ends only when its host is closed for good (window closed, dock item closed, app code), never on unload/reload; the UI library decides when that is. `NavigationDirection` is a `YFex.UI.Public` type (NavigatR keeps a `global using` alias), so the session and `NavigationContext.Direction` report the same enum.

### Open a route in a new history (docking)

```csharp
// in a page: "open order 42 in a new tab"
if (!Navigator.OpenInNewContext(new OrderRoute(42)))
    await Navigator.NavigateTo(new OrderRoute(42));      // no host can open tabs here: navigate in place
```

`OpenInNewContext` asks the history's pane (`INavigation.OnOpenRequested`); this history does not change. A docking host answers by creating a new pane whose own session starts at the route (YFex.Avalonia: `YPageHost.OpenRequested` bubbles to it). False when no pane took it.

### Full Lifecycle Table

Everything runs on the navigator's thread (the synchronization context the history was created on); data loaders run in the background.

| Event | When | Entry state after | Notes |
|---|---|---|---|
| loader (`WithData` / static `[Prefetch]`) | on `Prefetch` (hover) or when navigating | — | background, own DI scope; no view model built |
| `OnNavigating` | the page was built | — | `context.IsLoadPending` tells whether the data is still loading |
| `OnNavigation` | first show (and rebuild after eviction) | — | `context.Deny()` blocks |
| `OnLoaded` / `OnLoadFailed` | the data is ready | — | before showing when ready or `LoadFirst`; after showing for `ShowFirst`; may `Deny` |
| `OnLeaving` | before the current page is left | — | `context.Stay(reason)` keeps the user; `ConfirmAsync` asks through the configured confirmation |
| `OnShown` | the page became visible (fresh, resume, child returned, history switched back) | `Active` | `PageViewModel` → `Activate()` (wires `[Subscribe]`, starts `[Live]`) |
| `OnSuspend` | pushed to the back stack | `Suspended` | `PageViewModel` → `OnSuspendCascading` (`[Live]` `SuspendBehavior`); never on `Pinned` |
| `OnResume` | shown again | `Active` | never when returning from `UntilReturns` |
| `OnActive` | user back from AFK | `Active` (unchanged) | |
| `Dispose` | evicted, pruned, closed, result returned | `Released` | `PageViewModel` → `Deactivate()` |

> **Warning:** `OnSuspend` and `OnResume` are **never** called on `Pinned` entries. When you call `.UntilReturns()`, the calling ViewModel stays pinned -- it is not suspended and cannot be evicted.

---

## Step-by-Step Usage

### 1. Define a Route Record

```csharp
using YFex.NavigatR;

public record ProductRoute(int ProductId) : IRoute;
```

### 2. Create a ViewModel with [Route]

```csharp
using YFex.Mvvm;
using YFex.NavigatR;
using YFex.State;

[Route(typeof(ProductRoute), Load = NavigationLoad.LoadFirst)]
public partial class ProductViewModel : PageViewModel
{
    [Observable] private string _title = "";
    [Observable] private Product? _product;

    // Static data loader: background, own DI scope, no access to the page's state (NAV008/NAV009).
    [Prefetch]
    private static Task<Product> LoadProduct(int productId, IProductApi api, CancellationToken ct)
        => api.GetAsync(productId, ct);

    // Generated bridge: the route parameter, on the UI thread.
    public partial Task OnNavigation(int parameter, CancellationToken ct)
    {
        Title = $"Product {parameter}";
        return Task.CompletedTask;
    }

    // Generated bridge: the loaded data, on the UI thread. Deny here when the data says no.
    public partial Task OnLoaded(int parameter, Product loadProduct, CancellationToken ct)
    {
        Product = loadProduct;
        return Task.CompletedTask;
    }
}
```

> **Note:** The class **must** be `partial`. Constructor dependencies come from DI (the history's scope); the UI services (`Toast`, `Dialog`, …) and `Navigator` come from the base classes.

### 3. Navigate

```csharp
// Fire-and-forget (lazy -- runs on await)
await navigator.NavigateTo(new ProductRoute(42));

// Wait until the product page closes
var result = await navigator.NavigateTo(new ProductRoute(42)).UntilReturns();

result.Switch(
    success   => { /* page closed normally */ },
    denied    => { /* OnNavigation called Deny() */ },
    cancelled => { /* CancellationToken fired */ }
);
```

### 4. Navigate with a Typed Result

```csharp
using YFex.NavigatR;

// Route
public record ColorPickerRoute : IRouteProduces<Color>;

// ViewModel
[Route(typeof(ColorPickerRoute))]
public partial class ColorPickerViewModel : PageViewModel<Color>
{
    // Generated methods: Returns(Color), Cancel(), Deny(string?)

    public void OnColorSelected(Color color) => Returns(color);
    public void OnCancelled() => Cancel();
}

// Caller
var result = await navigator.NavigateTo(new ColorPickerRoute()).UntilReturns<Color>();

result.Switch(
    success   => ApplyColor(success.Value),
    denied    => ShowError(denied.Reason),
    cancelled => { /* user backed out */ }
);
```

---

## Deep Dive: Core API

### Navigator Methods

```csharp
// Forward navigation
NavigationTask NavigateTo(IRoute route, CancellationToken ct = default);
NavigationTask NavigateTo(IRoute route, NavigationLoad load, CancellationToken ct = default);   // override ShowFirst/LoadFirst
NavigationTask NavigateTo(PrefetchToken token, CancellationToken ct = default);
NavigationTask NavigateTo(string route, CancellationToken ct = default);

// History navigation (false when the page asked to stay, or nothing moved)
Task<bool> NavigateBackward(int? index = null, CancellationToken ct = default);
Task<bool> NavigateForward(int? index = null, CancellationToken ct = default);
Task<bool> NavigateBackwardTo<TRoute>(CancellationToken ct = default) where TRoute : class, IRoute;
Task<bool> NavigateForwardTo<TRoute>(CancellationToken ct = default) where TRoute : class, IRoute;
Task<bool> NavigateToIndex(int index);
void ClearHistory();

// New history (docking): asks the pane; false when nobody took it
bool OpenInNewContext(IRoute route);

// Prefetching (data only)
PrefetchToken Prefetch(IRoute route, TimeSpan? timeout = null, bool? cancelPrevious = null);

// State
NavigationEntry? Current { get; }                     // entry.Page is the page instance
bool CanGoBack { get; }
bool CanGoForward { get; }
IReadOnlyList<NavigationEntry> Breadcrumb { get; }
IReadOnlyList<NavigationEntry> History { get; }
NavigationHistoryPolicy HistoryPolicy { get; set; }
NavigatorOptions Options { get; }
event Action<NavigatedEventArgs>? Navigated;
event Action<NavigationDenied>? ActivePageDenied;

// AFK hooks, authorization
void NotifyInactive(CancellationToken ct = default);
void NotifyActive(CancellationToken ct = default);
Task<NavigationDenied?> RecheckAuthorizationAsync(CancellationToken ct = default);
```

`NavigatorOptions` (per history, passed to `NavigatorHost.CreateContext(navPane, options)`; `NavigatorHost.DefaultOptions`, set by `AddNavigatR`, for histories created without options and by UI page hosts): `HistoryPolicy`, `PoolCapacity`, `SuspendedTimeout`, `PrefetchPolicy`, `PrefetchTimeout` (how long prefetched data is kept), `Confirmation` (the `INavigationConfirmation` used by `LeavingContext.ConfirmAsync`; otherwise one from DI).

### [Route] Attribute Options

| Property | Type | Default | Description |
|---|---|---|---|
| `RouteName` | `string` | -- | When set (constructor), the generator creates a route record named `{RouteName}Route` |
| `RouteType` | `Type` | -- | When set (constructor), binds to an existing route record you defined |
| `DisplayName` | `string?` | `null` | Sets `IRoute.DisplayName` on the generated route |
| `Parameter` | `Type?` | `null` | Declares the route parameter type. Generator adds it to the route constructor and creates a typed `OnNavigation(T, CancellationToken)` partial |
| `ParameterRequired` | `bool` | `true` | When `false`, the parameter is nullable with default `null` |

Two constructor forms:

```csharp
// Auto-generate route record
[Route("product", Parameter = typeof(int))]
public partial class ProductViewModel : PageViewModel { }
// Generates: public sealed record ProductRoute(int Parameter) : IRoute;

// Use existing route record
[Route(typeof(ProductRoute), Parameter = typeof(int))]
public partial class ProductViewModel : PageViewModel { }
```

### [Prefetch] Attribute and NavigationLoad

`[Prefetch]` marks **static** data loaders. The navigator runs them in the background, in parallel, each load in its own DI scope (the scope ends with the load: return plain data, not tracked entities), on `Prefetch` (hover) or when the user navigates. No view model exists yet. Parameters: the route parameter (by type), a `CancellationToken`, services. Each `Task<T>`/`ValueTask<T>` adds one parameter to the generated `OnLoaded`.

`NavigationLoad` decides when the page appears:

| Mode | Page appears | Use for |
|---|---|---|
| `ShowFirst` (default) | at once; `IsLoadPending`/`PageViewModel.IsLoading` true until `OnLoaded` (show a skeleton). When the prefetch already finished, `OnLoaded` runs before showing: no skeleton | lists, pages useful without their data |
| `LoadFirst` | when the data arrived; the current page stays meanwhile. `OnLoaded` may `Deny` ("order not found") and the user never leaves. A newer navigation cancels the pending one | one specific record |

Set the route default with `[Route(…, Load = …)]` or `.Load(…)`; override one navigation with `NavigateTo(route, NavigationLoad.ShowFirst)`.

Prefetched data is kept for `PrefetchTimeout` (default 30 s); a navigation to an equal route (record equality) takes it, with or without the token; otherwise it is dropped (disposed when `IDisposable`) and a running loader cancelled. Routes the caller may not open are not prefetched.

### NavigationContext

```csharp
public sealed class NavigationContext
{
    public IRoute Route { get; }
    public IRoute? PreviousRoute { get; }
    public NavigationDirection Direction { get; }  // Initial, Forward, Backward
    public int StackDepth { get; }                 // 0 = root
    public NavigationLoad Load { get; }
    public bool IsLoadPending { get; }             // data still loading while the page shows
    public bool HasData { get; }
    public Exception? LoadError { get; }
    public T Data<T>();                            // the loader's value (in OnLoaded)
    public bool TryGetData<T>(out T value);
    public void Deny(string? reason = null);
}
```

### IKeepAlive

Prevent a ViewModel from being evicted from the pool under memory pressure:

```csharp
using YFex.NavigatR;

public record DashboardRoute : IRoute, IKeepAlive;
```

Keep-alive entries survive pool eviction. They remain in memory across navigations until explicitly disposed via back navigation or `ClearHistory()`.

### String Routes

For dynamic or deep-linking scenarios, register URL-style patterns:

```csharp
using YFex.NavigatR;

var registry = sp.GetRequiredService<RouteRegistry>();

// Pattern with parameter segment
registry.Register("products/{id}", typeof(ProductViewModel));

// Navigate by string -- the segment is parsed to the route's parameter type
await navigator.NavigateTo("products/42");

// Fixed parameter for complex types
registry.Register("admin/dashboard", typeof(DashboardViewModel), new DashboardConfig(...));
```

The segment is parsed via `IParsable<T>` for all BCL primitives and any user type implementing it.

---

## Common Patterns & Recipes

### Simple Navigation

```csharp
// Navigate to a page, don't wait for it to close
await navigator.NavigateTo(new HomeRoute());
```

### Navigation with Parameter

```csharp
public record OrderRoute(Guid OrderId) : IRoute;

// Navigate
await navigator.NavigateTo(new OrderRoute(orderId));
```

### Navigation with Result

```csharp
using YFex.NavigatR;

public record ConfirmDeleteRoute(string ItemName) : IRouteProduces<bool>;

// Caller
var result = await navigator
    .NavigateTo(new ConfirmDeleteRoute("Invoice #42"))
    .UntilReturns<bool>();

result.Switch(
    success   => { if (success.Value) DeleteItem(); },
    denied    => { },
    cancelled => { }
);
```

### Prefetching

```csharp
// Hover over a row: start loading the data (no page is built)
var token = navigator.Prefetch(new ProductRoute(42));

// Click: the page receives the data already loaded (equal route finds the prefetch even without the token)
await navigator.NavigateTo(new ProductRoute(42));
```

### Multi-Tab Navigation

```csharp
using YFex.NavigatR;

var host = sp.GetRequiredService<NavigatorHost>();

var inbox = host.CreateContext(inboxPane, poolCapacity: 5);
var drafts = host.CreateContext(draftsPane, setActive: false, poolCapacity: 5);

await inbox.NavigateTo(new InboxRoute());
await drafts.NavigateTo(new DraftsRoute());

// User clicks "Drafts" tab
await host.SwitchContextAsync(drafts.Id);
// inbox's top entry receives OnSuspend, drafts' top entry receives OnResume
```

### Denying Navigation (Guards)

```csharp
public override Task OnNavigation(NavigationContext context, CancellationToken ct = default)
{
    if (!_session.CanOpenAdmin)
    {
        context.Deny("Insufficient permissions");
        return Task.CompletedTask; // Always return immediately after Deny
    }
    return Task.CompletedTask;
}
```

Permission checks normally belong to the authorization gate (a view-model policy), which denies before the page is built and re-checks when permissions change.

### Leaving Guard (unsaved changes)

```csharp
public override async Task OnLeaving(LeavingContext context, CancellationToken ct = default)
{
    if (!IsDirty) return;
    if (!await context.ConfirmAsync("Discard your changes?", ct)) return;   // stays when the user says no
}
```

`context.Stay(reason)` keeps the user without asking; `ConfirmAsync` uses the history's `INavigationConfirmation` (message box, inline bar, flyout: the app decides). Asked before a new navigation, back, forward and `NavigatorHost.CloseContextAsync`; not while the page is pinned, nor when an authorization re-check closes it.

---

## Testing & Mocking

### Testing ViewModels That Navigate

`PageViewModel` provides a parameterless constructor specifically for tests:

```csharp
using YFex.NavigatR;

public class ProductViewModelTests
{
    [Fact]
    public async Task OnNavigation_loads_product()
    {
        // Arrange
        var api = Substitute.For<IProductApi>();
        api.GetProductAsync(42, Arg.Any<CancellationToken>())
            .Returns(new ProductData("Widget"));

        var vm = new ProductViewModel(api);

        // Act -- call OnNavigation directly with the parameter
        await vm.OnNavigation(42, CancellationToken.None);

        // Assert
        Assert.Equal("Widget", vm.ProductName);
    }
}
```

> **Tip:** For tests that exercise actual navigation (push/pop, result passing), create a `Navigator` from a test `IServiceScope` and register routes manually via `RouteRegistry.Register<TRoute, TViewModel>()`. No source generator needed.

### Testing Navigation Guards

```csharp
[Fact]
public async Task OnNavigation_denies_non_admin()
{
    var auth = Substitute.For<IAuthService>();
    auth.IsAdminAsync(default).ReturnsForAnyArgs(false);

    var vm = new AdminViewModel(auth);
    var context = new NavigationContext(
        new AdminRoute(), previousRoute: null,
        NavigationDirection.Initial, stackDepth: 0);

    await vm.OnNavigation(context, CancellationToken.None);

    Assert.True(context.IsDenied);
}
```

---

## Troubleshooting & Gotchas

**Forgetting `partial`**
The class must be `partial` for the source generator to emit code. If your `[Route]` ViewModel is not partial, you will see no generated registration and the route will not resolve at runtime.

**Not calling `base.OnSuspend()` / `base.OnResume()`**
`PageViewModel.OnSuspend` calls `OnSuspendCascading()` which pauses `[Live]` fetching. If you override `OnSuspend` without calling `base.OnSuspend(ct)`, generated live-state cleanup will not execute.

**Result type mismatch**
Calling `.UntilReturns<Color>()` on a route whose ViewModel implements `INavigable<string>` throws a `NavigationResultExpectedException` at runtime. The types must match exactly.

**Pool eviction surprises**
When a suspended ViewModel is evicted from the pool, it is disposed. If the user navigates back to that history entry, a fresh instance is created and `OnNavigation` runs again. Use `IKeepAlive` on routes that must survive pool pressure.

**Pinned entries block eviction**
A ViewModel waiting on `.UntilReturns()` is `Pinned`. It cannot be evicted, and `OnSuspend`/`OnResume` are never called on it. If you rely on `OnSuspend` for cleanup in a ViewModel that also uses `UntilReturns`, that cleanup will not run until the child navigation completes.

**Returning after Deny**
Always `return` immediately after calling `context.Deny()`. Any state mutations after `Deny()` execute but are discarded when the ViewModel is not displayed.

---

## Reference Summary

### NavigationEntryState

| State | Description | Evictable | OnSuspend/OnResume |
|---|---|---|---|
| `Active` | Currently on screen | No | N/A |
| `Suspended` | In back-stack, alive in memory | Yes (unless `IKeepAlive`) | Yes |
| `Pinned` | Mid-await on `UntilReturns()` | Never | Never called |
| `Released` | Disposed. History entry exists, instance is null | N/A | N/A |
| `Prefetching` | Obsolete: prefetch no longer builds a view model | — | — |

### NavigationHistoryPolicy

| Policy | Behavior | Use Case |
|---|---|---|
| `PruneForwardOnBranch` | Forward entries discarded when branching mid-history | Web browser, wizards, checkout flows |
| `PreserveForwardOnBranch` | Forward entries preserved; new entry inserted after cursor | Document editors, research tools |

### NavigationDirection

`YFex.UI.Public.NavigationDirection` (moved from NavigatR in step 2 of the NavigatR-on-Avalonia plan; add `using YFex.UI.Public;` where you name it).

| Value | Description |
|---|---|
| `Initial` | First navigation in this context |
| `Forward` | Moving forward in the stack (new navigation or NavigateForward) |
| `Backward` | Moving backward in the stack (NavigateBackward) |

### [Route] Parameters

| Property | Type | Required | Description |
|---|---|---|---|
| `RouteName` | `string` | One of `RouteName` or `RouteType` | Auto-generates a route record |
| `RouteType` | `Type` | One of `RouteName` or `RouteType` | Binds to an existing route record |
| `Parameter` | `Type?` | No | Route parameter type |
| `ParameterRequired` | `bool` | No (default `true`) | Whether the parameter is nullable |
| `DisplayName` | `string?` | No | Sets `IRoute.DisplayName` |
| `Load` | `NavigationLoad` | No (default `ShowFirst`) | When the page shows relative to its data |

### NavigationResult Union Cases

| Case | Description | Property |
|---|---|---|
| `NavigationSuccess` | Navigation completed normally | -- |
| `NavigationSuccess<T>` | Navigation completed with a value | `Value: T` |
| `NavigationDenied` | Blocked by `context.Deny()` | `Reason: string?` |
| `NavigationCancelled` | Cancelled via `CancellationToken` | -- |

### PrefetchPolicy

| Policy | Description |
|---|---|
| `CancelPrevious` | Cancel in-flight prefetch when a new one starts for a different route. Same route + same params reuses existing. Default. |
| `AllowMultiple` | Allow multiple prefetches to run simultaneously |
