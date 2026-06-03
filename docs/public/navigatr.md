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

Every navigable ViewModel implements `INavigable`. This interface defines the full lifecycle contract:

```csharp
public interface INavigable : IDisposable
{
    Task OnNavigation(NavigationContext context, CancellationToken ct = default);
    Task OnResume(CancellationToken ct = default);
    Task OnSuspend(CancellationToken ct = default);
    Task OnPrefetch(NavigationContext context, CancellationToken ct) => Task.CompletedTask;
    Task OnActive(CancellationToken ct = default) => Task.CompletedTask;
}
```

In practice, you never implement `INavigable` directly. You inherit from `PageViewModel` (from `YFex.Mvvm`) and apply `[Route]` -- the source generator wires everything.

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

The `RouteRegistry` is a singleton that maps route types to ViewModel types. The source generator produces a `RegisterAll` method that populates it automatically:

```csharp
using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;

// In your DI setup
services.AddSingleton<RouteRegistry>();
services.AddSingleton<NavigatorHost>();

// After building the service provider
var registry = sp.GetRequiredService<RouteRegistry>();
NavigatRRegistration.RegisterAll(registry); // Source-generated
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

To connect NavigatR to your UI framework, implement `INavigation`:

```csharp
using YFex.NavigatR;

public class AvaloniaNavigation : INavigation
{
    private readonly ContentControl _host;

    public AvaloniaNavigation(ContentControl host) => _host = host;

    public void PerformNavigation(object view)
    {
        // 'view' is the resolved ViewModel instance
        // Map it to a View and display it
        _host.Content = ViewLocator.Resolve(view);
    }

    public void OnNavigationDenied()
    {
        // Optional: show a toast, log, etc.
    }

    public event Action? UserBecameInactive;
    public event Action? UserBecameActive;
}
```

### Full Lifecycle Table

| Event | When | Entry State After | Notes |
|---|---|---|---|
| `OnNavigation` | First navigation to this screen | `Active` | Call `context.Deny()` to block |
| `Activate()` | Called by Navigator after `OnNavigation` confirms | `Active` | Wires `[Subscribe]`, creates `[Live]` states |
| `OnSuspend` | Screen pushed to back-stack | `Suspended` | Never called on `Pinned` entries |
| `Deactivate()` | Called by Navigator after `OnSuspend` | `Suspended` | Pauses `[Live]` fetching |
| `OnResume` | Screen returns to focus | `Active` | Never called when returning from `UntilReturns` |
| `Activate()` | Called by Navigator after `OnResume` | `Active` | Re-subscribes, re-fetches stale data |
| `OnActive` | User returns from AFK | `Active` (unchanged) | No state transition |
| `OnPrefetch` | Off-thread data preloading | `Prefetching` | Generator-driven, never implemented directly |
| `Dispose` | Screen destroyed (pool eviction or back nav) | `Released` | Calls `Deactivate()` if still active |

> **Warning:** `OnSuspend` and `OnResume` are **never** called on `Pinned` entries. When you call `.UntilReturns()`, the calling ViewModel stays pinned -- it is not suspended and cannot be evicted. This is intentional: the caller's state must remain intact for the result to make sense.

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

[Route(typeof(ProductRoute), Parameter = typeof(int))]
public partial class ProductViewModel : PageViewModel
{
    private readonly IProductApi _api;

    public ProductViewModel(
        IProductApi api,
        Navigator navigator,
        INotification notification,
        IDialog dialog,
        IToast toast)
        : base(navigator, notification, dialog, toast)
    {
        _api = api;
    }

    // The generator wires this partial from OnNavigation(NavigationContext, ct).
    // 'parameter' is extracted from ProductRoute.ProductId automatically.
    public partial Task OnNavigation(int parameter, CancellationToken ct)
    {
        return LoadProductAsync(parameter, ct);
    }

    private async Task LoadProductAsync(int productId, CancellationToken ct)
    {
        // Load product data...
    }
}
```

> **Note:** The class **must** be `partial`. The source generator adds the `INavigable.OnNavigation` explicit implementation, route registration, and parameter extraction.

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
using YFex.NavigatR;

// Forward navigation
NavigationTask NavigateTo(IRoute route, CancellationToken ct = default);
NavigationTask NavigateTo(PrefetchToken token, CancellationToken ct = default);
NavigationTask NavigateTo(string route, CancellationToken ct = default);

// History navigation
Task NavigateBackward(int? index = null, CancellationToken ct = default);
Task NavigateForward(int? index = null, CancellationToken ct = default);
Task NavigateBackwardTo<TRoute>(CancellationToken ct = default) where TRoute : IRoute;
Task NavigateForwardTo<TRoute>(CancellationToken ct = default) where TRoute : IRoute;
Task NavigateToIndex(int index);
void ClearHistory();

// Prefetching
PrefetchToken Prefetch(IRoute route, TimeSpan? timeout = null, bool? cancelPrevious = null);

// AFK hooks
void NotifyInactive(CancellationToken ct = default);
void NotifyActive(CancellationToken ct = default);

// State
Guid Id { get; }
NavigationHistoryPolicy HistoryPolicy { get; set; }
INavigation? NavPane { get; set; }
IReadOnlyList<NavigationEntry> Breadcrumb { get; }
```

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

### [Prefetch] Attribute

Mark methods to run off-thread before navigation completes. The generator intercepts the method call: if a prefetch result is available it returns immediately, if a prefetch is in-flight it awaits the running task, otherwise it calls the method fresh.

```csharp
using YFex.NavigatR;

[Route("product", Parameter = typeof(int))]
public partial class ProductViewModel : PageViewModel
{
    [Prefetch]
    public async Task<ProductData> FetchProduct(int productId, CancellationToken ct)
    {
        return await _api.GetProductAsync(productId, ct);
    }

    // Generator injects FetchProduct's return value as a parameter:
    public partial Task OnNavigation(int parameter, ProductData fetchProduct, CancellationToken ct)
    {
        // fetchProduct is already loaded -- no await needed
        Product = fetchProduct;
        return Task.CompletedTask;
    }
}
```

Multiple `[Prefetch]` methods on the same ViewModel run in parallel. Each `Task<T>` return adds one parameter to the generated `OnNavigation`.

### NavigationContext

Passed to `OnNavigation` on every fresh navigation:

```csharp
public sealed class NavigationContext
{
    public IRoute Route { get; }
    public IRoute? PreviousRoute { get; }
    public NavigationDirection Direction { get; }  // Initial, Forward, Backward
    public int StackDepth { get; }                 // 0 = root

    public void Deny(string? reason = null);
}
```

> **Tip:** When `[Route(Parameter = typeof(T))]` is declared, you never interact with `NavigationContext` directly. The generator extracts the parameter from the route and passes it to your typed `OnNavigation(T, CancellationToken)`.

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
using YFex.NavigatR;

// Start loading data while the user is still on the current screen
var token = navigator.Prefetch(new ProductRoute(42));

// ... user clicks confirm ...

// Navigate using the token -- data already loaded, instant transition
await navigator.NavigateTo(token);
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
[Route(typeof(AdminRoute))]
public partial class AdminViewModel : PageViewModel
{
    public override async Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        if (!await _authService.IsAdminAsync(ct))
        {
            context.Deny("Insufficient permissions");
            return; // Always return immediately after Deny
        }

        await LoadAdminDataAsync(ct);
    }
}
```

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
| `Prefetching` | `OnPrefetch` running, not yet navigated | No (does not count toward pool) | N/A |

### NavigationHistoryPolicy

| Policy | Behavior | Use Case |
|---|---|---|
| `PruneForwardOnBranch` | Forward entries discarded when branching mid-history | Web browser, wizards, checkout flows |
| `PreserveForwardOnBranch` | Forward entries preserved; new entry inserted after cursor | Document editors, research tools |

### NavigationDirection

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
