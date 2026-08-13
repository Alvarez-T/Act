# The Event Bus (`YFex.Messaging`)

## Why it exists

Components in an MVVM app constantly need to react to things that happen elsewhere: an order was placed, a connection dropped, a setting changed. Wiring these with direct references creates a tangle — the badge would have to know about the order screen, the toast service, the sync engine, and so on.

The event bus decouples **who raises an event** from **who reacts to it**. Publishers fire a message; any number of subscribers receive it. Neither side references the other.

**Design goals:** zero-allocation on the hot path (interface recipients, `in`-passed struct events), lifecycle-safe (subscriptions follow a `StateObject`'s activation and auto-drop when the host is collected), and AOT-friendly (subscriptions are generated, not reflected).

## The mental model

- **Event** = a plain record or struct. Nothing special required for in-process use.
- **Publish** = fire the event. Synchronous recipients run inline; async recipients can be awaited or fire-and-forget.
- **Subscribe** = declare a handler method with `[Subscribe<T>]`. The generator wires it into the host's activation lifecycle.

---

## Subscribing — the normal way

Put `[Subscribe<T>]` on a method in a `partial` `StateObject`/`ViewModel`. The generator emits an adapter and hooks subscribe/unsubscribe into `OnActivateCascading` / `OnDeactivateCascading`, so the handler is only live while the host is active.

```csharp
public partial class InboxViewModel : PageViewModel
{
    [Observable] public partial int Unread { get; set; }

    // Sync handler — 'in' avoids copying struct events
    [Subscribe<MessageReceived>]
    void OnMessage(in MessageReceived e) => Unread++;

    // Async handler — awaited by PublishAsync, fire-and-forget by Publish
    [Subscribe<MessageReceived>]
    async ValueTask PersistAsync(MessageReceived e, CancellationToken ct)
        => await _store.SaveAsync(e, ct);
}
```

`[Subscribe<T>]` allows multiple handlers on the same method target and the same event on different methods.

### Filtering options

All set on the attribute:

| Option | What it does |
|---|---|
| `FilterBy = "Model.Id"` | Only fires when the event's `Id` equals `this.Model.Id`. Comma-separate for multiple fields (all must match). |
| `Target = nameof(SessionId)` | Point-to-point: only receives events published via `PublishToAsync(targetId, …)` where `targetId` equals this property's value. |
| `Group = nameof(RoomId)` | Group fan-out: only receives events published via `PublishToGroupAsync(groupId, …)` matching this property. |
| `DebounceMs = 300` | Coalesce bursts — handler fires once after 300 ms of silence. Great for search keystrokes. |
| `ThrottleMs = 300` | Fire immediately, then ignore further events for 300 ms. |
| `KeepAlive = true` | Pin the subscription with a strong reference (survives GC of the host). Default is a weak reference. |

`DebounceMs` and `ThrottleMs` are mutually exclusive.

---

## Publishing

Use the `IEventBus` directly, the static facade, or the generated `Event.Publish` helpers.

```csharp
// Broadcast to everyone
bus.Publish(new OrderPlaced(42, 99.90m));

// Await all async recipients (sequentially)
await bus.PublishAsync(new OrderPlaced(42, 99.90m), ct: ct);

// Point-to-point: only subscribers whose Target property == "session-7"
await bus.PublishAsync(evt, new PublishOptions { TargetId = "session-7" });

// Group fan-out: only subscribers in room "lobby"
await bus.PublishAsync(evt, new PublishOptions { GroupId = "lobby" });
```

`Publish` (sync) calls sync recipients inline and invokes async recipients fire-and-forget. `PublishAsync` runs sync recipients first, then awaits async recipients one after another.

### Delegate subscriptions (no attribute)

For quick, non-ViewModel wiring, `EventBusExtensions.On` registers a delegate. You own the returned `IDisposable`.

```csharp
IDisposable sub = bus.On<OrderPlaced>(e => Console.WriteLine(e.OrderId));
// …later
sub.Dispose();
```

---

## `MessagingHost` — for long-lived services

`[Subscribe<T>]` normally rides a `StateObject`'s activation lifecycle. But a **singleton service** isn't a `StateObject` and lives for the whole app. Derive it from `MessagingHost`:

```csharp
public sealed partial class ConnectivityLogger : MessagingHost
{
    [Subscribe<ConnectionChanged>]
    void OnChanged(in ConnectionChanged e) => _log.Info($"Network: {e.State}");
}
```

The generator subscribes all handlers from the constructor (`OnHostStarting`) and registers the tokens so they're released when DI disposes the host (`DisposeAsync`). No manual subscribe/unsubscribe, no `IAsyncDisposable` boilerplate.

---

## Registration

```csharp
services.AddYFexMessaging();
```

Registers `DefaultEventBus` as the singleton `IEventBus` and wires it into `EventBusProvider` so the static facade works. In an RPC app, `AddYFexMessagingRpcClient` registers an `RpcEventBus` instead (a composite that also forwards events over the wire) — see [`03-rpc-and-offline.md`](03-rpc-and-offline.md).

---

## Rules of thumb

- The host class **must be `partial`** — the generator emits into it.
- Prefer `in T` sync handlers for structs; they avoid copies on the hot path.
- Default subscriptions are **weak** — if a handler must outlive its host, set `KeepAlive = true`.
- Cross-process events (RPC) need `[MemoryPackable]` on the event record — the generator emits diagnostic **YFRPC0001** to remind you.
