# NavigatR Internals

This document covers the internal architecture of `YFex.NavigatR`: the navigator state machine, pool eviction strategy, prefetch lifecycle, return value plumbing, history stack policies, generator emission patterns, and AFK handling. Read this when you're modifying navigation behavior, debugging lifecycle issues, or extending the generator.

---

## Navigator State Machine

Navigation is a three-step process:

1. **Resolve** — Look up ViewModel type from route via `RouteRegistry`
2. **Construct** — Resolve ViewModel from DI scope (or reuse from pool)
3. **Execute** — Run lifecycle methods: `OnPrefetch` -> `OnNavigation` -> `OnSuspend`/`OnResume`

### Execution Modes

`NavigationTask` supports three execution semantics:

| Mode | Trigger | Behavior |
|---|---|---|
| Lazy | `await nav.NavigateTo(route)` | Executes on await; caller suspended |
| UntilReturns (void) | `.UntilReturns()` | Caller pinned; waits for back navigation |
| UntilReturns\<T\> | `.UntilReturns<T>()` | Caller pinned; waits for `Returns(T)` / `Cancel()` / `Deny()` |

> **Note:** "Pinned" means the caller's `NavigationEntry.State` is set to `Pinned`. Pinned entries are never pool-evicted, and `OnSuspend`/`OnResume` are never called on them. This is important for modal flows where the caller must survive in memory until the callee returns.

---

## Pool Eviction (NavigablePool)

LRU eviction with capacity tracking:

```
1. On OnSuspend -> entry added to LRU tail (most recently suspended)
2. On OnResume  -> entry moved to LRU tail (refreshed)
3. On new navigation -> TrimToCapacity() evicts from LRU head (least recently used)
4. IKeepAlive entries are never evicted regardless of position
```

Optional `suspendedTimeout`: entries idle for longer than the timeout are auto-released even if the pool hasn't reached capacity.

> **Why LRU instead of FIFO?** The user is more likely to go back to recently visited pages. LRU keeps the most recently accessed entries alive, which is the better heuristic for navigation patterns. FIFO would evict the second-most-recent page when the user navigates forward three times, which is usually wrong.

> **Warning:** `IKeepAlive` entries bypass eviction entirely. Use sparingly — a ViewModel that holds expensive resources (large images, open streams) and implements `IKeepAlive` will never be released until the navigator is disposed.

---

## Prefetch Lifecycle

```
navigator.Prefetch(route)
  -> resolve ViewModel from DI
  -> set entry state to Prefetching
  -> run OnPrefetch(context, ct) off-thread
  -> store results in PrefetchToken

navigator.NavigateTo(token)
  -> check token.IsExpired
  -> if valid: skip DI resolve, use prefetched entry
  -> inject prefetch task results into OnNavigation
  -> if expired: fall through to fresh navigation
```

> **Why prefetch?** For data-heavy pages, the network call dominates navigation latency. Prefetch lets you start the data fetch while the user is hovering over a navigation link or during an animation, so the page appears instantly when they commit to navigating.

`PrefetchPolicy.CancelPrevious` cancels any in-flight prefetch for a different route before starting a new one. This prevents wasted work when the user rapidly moves between navigation targets.

---

## Return Value Plumbing

For `INavigable<TResult>`:

1. Generator emits a `TaskCompletionSource<NavigationResult<TResult>>` field
2. `Returns(T)` -> `TCS.TrySetResult(Success(value))`
3. `Cancel()` -> `TCS.TrySetResult(Cancelled())`
4. `Deny(reason)` -> `TCS.TrySetResult(Denied(reason))`
5. `WaitForResultAsync()` -> returns `TCS.Task`

```csharp
// Caller side:
var result = await navigator
    .NavigateTo(new SelectCustomerRoute())
    .UntilReturns<Customer>();

result.Match(
    success: customer => UseCustomer(customer),
    cancelled: () => { /* user pressed back */ },
    denied: reason => ShowError(reason)
);

// Callee side (SelectCustomerViewModel):
Returns(selectedCustomer);  // completes the TCS with Success
```

`NavigableReturnInterceptor` uses a `ConditionalWeakTable` to track pending result callbacks — no memory leak if the ViewModel is GC'd before completing the result.

> **Why ConditionalWeakTable?** If the callee ViewModel is disposed without calling `Returns()`, `Cancel()`, or `Deny()`, the weak reference allows GC to collect it. The caller's `WaitForResultAsync()` will receive a `Cancelled` result via the finalizer or disposal path.

---

## History Stack

The breadcrumb is a list of `NavigationEntry` with a cursor index:

```
[Home] [Products] [Product/42] [Settings]
                       ^ cursor = 2
```

### PruneForwardOnBranch (default)

New navigation at cursor position 2 removes entries 3+ (Settings), then adds the new entry:

```
Before: [Home] [Products] [Product/42] [Settings]
                               ^ cursor
Navigate to [Cart]:
After:  [Home] [Products] [Product/42] [Cart]
                                         ^ cursor = 3
```

### PreserveForwardOnBranch

New navigation at cursor position 2 inserts after cursor; existing forward entries shift right:

```
Before: [Home] [Products] [Product/42] [Settings]
                               ^ cursor
Navigate to [Cart]:
After:  [Home] [Products] [Product/42] [Cart] [Settings]
                                         ^ cursor = 3
```

> **When to use PreserveForwardOnBranch?** Use it in wizard-like flows where the user might branch and then want to return to the original forward path. The default `PruneForwardOnBranch` matches browser behavior and is correct for most applications.

---

## Generator Emission

### Per-Route (auto-generated)

When a ViewModel has `[Route("name")]`, the generator emits a sealed route record:

```csharp
public sealed record ProductRoute(int Id) : IRoute
{
    public string? DisplayName => "Product";
}
```

### Per-ViewModel

The generator emits `INavigable` interface implementations that bridge the generated route to the user's methods:

```csharp
// INavigable.OnNavigation bridge
async Task INavigable.OnNavigation(NavigationContext ctx, CancellationToken ct)
{
    var param = ((ProductRoute)ctx.Route).Id;
    var prefetchResult = _prefetchTask is not null
        ? await _prefetchTask
        : await FetchDataCore(param, ct);   // renamed from user's [Prefetch] method
    _prefetchTask = null;
    await OnNavigation(param, prefetchResult, ct);
}

// INavigable.OnPrefetch bridge
Task INavigable.OnPrefetch(NavigationContext ctx, CancellationToken ct)
{
    var param = ((ProductRoute)ctx.Route).Id;
    _prefetchTask = Task.Run(() => FetchDataCore(param, ct));
    return _prefetchTask;
}
```

> **Note:** When a `[Prefetch]` method is present, the generator renames the original to `*Core` and wraps it with a prefetch-aware interceptor. This is invisible to the user but means you won't find a method with the original name in the emitted code.

### Registration

```csharp
static partial void RegisterGenerated(RouteRegistry registry)
{
    registry.Register<HomeRoute, HomeViewModel>();
    registry.Register<ProductRoute, ProductViewModel>();
}
```

---

## String Route Resolution

`RouteRegistry` compiles string patterns into regex for deep-link / URL-based navigation:

- `"products/{id}"` -> `^products/(?<id>.+)$`
- Parameter segments extracted and parsed to the route's parameter type via `IParsable<T>` or simple type conversion

> **Tip:** String route resolution is primarily for web/deep-link scenarios. For in-app navigation, prefer the strongly-typed route records — they're faster (no regex) and catch errors at compile time.

---

## AFK Handling

Platform calls `Navigator.NotifyInactive(ct)` when the application goes to background:

1. Calls `OnSuspend(ct)` on current active ViewModel (for resource release — pause timers, stop polling)
2. Does **NOT** change `NavigationEntryState` (stays `Active`)
3. On `NotifyActive(ct)`: calls `OnActive(ct)` — ViewModel can refresh stale data

> **Why not change the entry state?** The ViewModel isn't being replaced on screen. It's still the active page — it's just not visible. Changing state to `Suspended` would trigger pool eviction logic and potentially dispose the ViewModel, which is wrong for a background/foreground transition.
