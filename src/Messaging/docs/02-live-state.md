# Live State — the `[Live]` attribute (`YFex.Messaging` + `YFex.Messaging.Fusion`)

## Why it exists

UI constantly needs to display data that lives somewhere else — a count from the server, a filtered list, a computed total. Doing this by hand means juggling four things per value: the value itself, a loading flag, an error, and a refresh trigger. Then you have to remember to re-fetch when inputs change, poll if it goes stale, pause when the page is on the back stack, and serve something when offline.

`[Live]` collapses all of that into one attribute on one method.

## What you write vs. what you get

You write a fetch method:

```csharp
public partial class DashboardViewModel : PageViewModel
{
    [Live(PollMs = 5000)]
    private Task<int> TotalCustomersAsync(CancellationToken ct)
        => Customer.Queries.GetCount(ct);
}
```

The generator emits, from `TotalCustomersAsync` (the `Async` suffix is stripped):

| Generated member | Meaning |
|---|---|
| `int TotalCustomers` | The current value (bindable). `default` until the first fetch completes. |
| `bool IsTotalCustomersLoading` | True while a fetch is in flight. |
| `Exception? TotalCustomersError` | Last fetch error, or null. |
| `bool IsTotalCustomersStale` | True once older than `StaleTimeMs` (if set). |
| `Task RefreshTotalCustomersAsync()` | Force a fresh fetch. |
| lifecycle wiring | Subscription is started/stopped via the activation cascade (`OnActivateCascading` / `OnDeactivateCascading`). |

Bind `TotalCustomers`, `IsTotalCustomersLoading`, and `TotalCustomersError` in the view and the whole loading/error/refresh dance is handled.

## Method signature rules

- Returns `Task<T>` or `ValueTask<T>`.
- Takes a `CancellationToken` as the **last** parameter.
- Host class is `partial`.

---

## Controlling refresh behavior

Everything is on the attribute:

| Option | Effect |
|---|---|
| `PollMs = 5000` | Re-fetch every 5 s. `0` (default) = never poll; refresh only on demand or on dependency change. |
| `DependsOn = [nameof(Filter), nameof(Page)]` | Auto re-fetch whenever these `[Observable]` properties change. Use `nameof` for safety. |
| `StaleTimeMs = 30000` | After 30 s since the last success, `IsXStale` becomes true (drives "refreshing…" hints). `0` = never stale. |
| `PollDuringSuspend = true` | Keep polling even when the host page is on the back stack. Default pauses. |
| `SuspendBehavior = …` | Fine-grained suspend policy (below). |
| `Cache = LiveCache.ServerShared` | Where the value is cached / how invalidation arrives. Default `Local`. |
| `PersistenceKey = "..."` | Persist the cached value across process restarts (with `LiveCache.ClientPersistent`). |

If `PollMs == 0` and no `DependsOn` is set, the generator emits info diagnostic **YFLIV0002** — a nudge that the property will only ever refresh when you call `RefreshXAsync()`.

### Suspend behavior

For `PageViewModel`-hosted live properties, `LiveSuspendBehavior` decides what happens when the page goes to the back stack:

- `PauseAndRefreshOnResume` *(default)* — stop updating; re-fetch on resume only if stale.
- `StayLive` — keep the subscription active while suspended.
- `AlwaysRefetchOnResume` — force a fresh fetch on every resume.
- `FreezeOnSuspend` — freeze the cached value; no auto-refresh.

---

## The two backends behind `[Live]`

`[Live]` talks to an `ILiveState<T>` produced by an `ILiveStateFactory`. There are two implementations, selected by which package you register:

### 1. Default (task-based)

`AddYFexMessaging` alone gives you a simple task-based live state: it runs the fetch, tracks loading/error/timestamps, and polls. No cross-value caching. Good for straightforward screens.

### 2. Fusion-backed (`YFex.Messaging.Fusion`)

```csharp
services.AddYFexFusion();
```

Replaces the factory with one backed by **ActualLab.Fusion**'s `ComputedState<T>`. Now live values participate in Fusion's dependency graph: results are cached, and when an upstream computed value is invalidated, dependents recompute automatically — no manual `Refresh`. This is what you want when several live values derive from shared server state.

`FusionLiveState<T>` also supports offline cache hooks: on a failed fetch it can serve a persisted value (`IsFromOfflineCache` flips true), and it saves each success back to the cache. Those hooks are injected by `YFex.Messaging.Rpc` — see [`03-rpc-and-offline.md`](03-rpc-and-offline.md).

### `ILiveState<T>` surface

If you ever need the raw reactive value (outside a `[Live]` property), the interface is:

```csharp
T?      Value;
bool    IsLoading;
Exception? Error;
DateTimeOffset? LastFetchedAt;
bool    IsStale;
bool    IsFromOfflineCache;
Task    RecomputeAsync(CancellationToken ct = default);
event Action<ILiveState<T>>? Updated;
```

---

## Binding a raw Fusion state directly

`FusionStateBinding<T>` wraps any Fusion `IState<T>` and exposes it as an `[Observable]`-compatible value with `INotifyChanged` + `IActivatable`. Declare it as an observable property on a `StateObject` and the activation cascade drives its lifecycle — handy when you already have a Fusion compute service and want to surface it without a `[Live]` method.

---

## Rules of thumb

- Name the method `XxxAsync`; the property becomes `Xxx`.
- Reach for `DependsOn` before `PollMs` — event/dependency-driven refresh beats blind polling.
- Register `AddYFexFusion` (or the RPC client, which includes it) when multiple live values share upstream state or you need offline serving.
