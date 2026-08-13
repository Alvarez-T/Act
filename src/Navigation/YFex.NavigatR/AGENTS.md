# YFex.NavigatR

## Why this exists

Owns the navigation stack and the lifecycle of everything on it: which screen is current, which
suspended screens stay in memory, and which get disposed under pressure. Separate from
`YFex.Mvvm` so that stack semantics — history policy, pooling, pinning, prefetch — can be tested
with no ViewModel base class, no `INotifyPropertyChanged`, and no UI framework in the graph.

## Owns / does not own

**Owns** — `Navigator` (one navigation surface: a tab, window or pane), `NavigatorHost` (many
surfaces), `RouteRegistry` (route type → ViewModel type), `NavigablePool` (eviction of suspended
entries), the `NavigationResult` union, and the `INavigable` lifecycle contract.

**Does not own** — `PageViewModel` and the base class app ViewModels actually inherit
(`YFex.Mvvm`); `INavigation`, `IDialog`, `IToast` and the other UI contracts
(`YFex.UI.Abstractions`); code generation for `[Route]` / `[Prefetch]`
(`YFex.NavigatR.SourceGenerator`).

## Public surface

Start at `Navigator`. `NavigateTo(IRoute)` returns a `NavigationTask`, not a `Task` — awaiting it
fires navigation and discards the result; `.UntilReturns()` / `.UntilReturns<T>()` start
immediately and pin the caller until the target screen closes or calls `Returns`/`Cancel`/`Deny`.
Around that: `IRoute` / `IRouteProduces<T>`, `INavigable`, `NavigationContext` (`Deny` lives
here), `NavigationEntry` + `NavigationEntryState`, `NavigationHistoryPolicy`, `IKeepAlive`,
`PrefetchToken`, and `NavigatorHost` for multi-surface apps.

## Rules

- **A `Pinned` entry never receives `OnSuspend` or `OnResume` and is never evicted.** That is the
  entry awaiting `.UntilReturns()`. Cleanup you put in `OnSuspend` will not run for the duration
  of the child navigation, so it cannot be where you release a resource the child needs freed.
- `.UntilReturns<T>()` must match the ViewModel's `INavigable<T>` exactly, or you get
  `NavigationResultExpectedException` at runtime — there is no variance and no coercion.
- A ViewModel evicted from the pool is disposed; navigating back to that history entry builds a
  fresh instance and re-runs `OnNavigation`. Mark the route `IKeepAlive` when re-running is wrong.
- `context.Deny()` does not stop execution. Always `return` immediately after it.
- `NavigationHistoryPolicy` is per-`Navigator` and changes what a mid-history navigation does to
  the forward stack. Do not assume browser semantics; `PreserveForwardOnBranch` inserts.

## Wiring

```csharp
services.AddSingleton<RouteRegistry>();
services.AddSingleton<NavigatorHost>();
services.AddScoped<ProductViewModel>();      // one per navigable ViewModel

var registry = sp.GetRequiredService<RouteRegistry>();
registry.Register<ProductRoute, ProductViewModel>();   // one per route
```

**Do not use `NavigatRRegistration.RegisterAll`.** It delegates to a partial method the source
generator was meant to implement from the app assembly, which C# does not allow across an
assembly boundary — see [../AGENTS.md](../AGENTS.md). Manual `Register<TRoute, TViewModel>()` is
the working path.

`Navigator` resolves ViewModels through the scope's `IServiceProvider`, so every navigable
ViewModel must be in DI or navigation throws. Attach the platform hook via
`NavigatorHost.CreateContext(navPane)` — `Navigator.NavPane` is `internal` and only the test
assembly can set it directly.

Satellites and known-broken state: [../AGENTS.md](../AGENTS.md). Usage guide:
[docs/public/navigatr.md](../../../docs/public/navigatr.md).
