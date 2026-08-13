# RPC & Offline (`YFex.Messaging.Rpc`)

## Why it exists

A reactive app that talks to a server has to answer hard questions the moment the network is unreliable:

- What happens to a command the user issued while offline? *(It must not be lost.)*
- What does a query return when the server is unreachable? *(Ideally the last known value.)*
- How does the UI know it's offline, syncing, or caught up? *(A bindable status.)*
- When the queued command finally reaches a server whose state moved on, who wins? *(Conflict policy.)*

`YFex.Messaging.Rpc` is the **offline-first CQRS transport**. It wraps YFex.Cqrs command/query dispatch with a Fusion RPC channel, an **outbox** for queued commands, a **client cache** for offline queries, and a bindable **sync status**.

## The mental model

```
        ┌──────── your ViewModel calls Customer.Commands.Create(...) ────────┐
        │                                                                     ▼
   IDispatcher (FusionMessageBus on client / LocalDispatcher on server)
        │
        ├─ online  → send over Fusion WebSocket RPC → server IHandlerInvoker → handler
        │
        └─ offline → IQueueable command? → IOutbox (persisted)
                     ICacheable query?   → IClientCache (serve last value)
                                             ▲
                          OutboxReplayer ────┘ drains the outbox on reconnect
```

Whether a command queues or a query serves-from-cache is driven by **marker interfaces on the message** (`IQueueable`, `ICacheable`) plus the policies in your `IAggregateConfiguration<T>` — the same CQRS configuration you already write. The RPC layer just honors them.

---

## Client setup

```csharp
services.AddYFexMessagingRpcClient(o =>
{
    o.WebSocketEndpoint = new("wss://api.myapp.com/rpc/ws");
    o.OutboxOptions.MaxEntries = 5_000;
    o.OutboxEntryTtl = TimeSpan.FromDays(7);
});
```

`AddYFexMessagingRpcClient` registers the full client stack in one call:

- **Fusion client** + WebSocket connection to your server.
- **Fusion-backed `ILiveStateFactory`** (so `[Live]` gets caching + offline serving automatically).
- **`IClientCache`** — offline query cache.
- **`IOutbox`** + **`ISyncFailureLog`** — offline command queue and its dead-letter log.
- **`SyncStatus`** — bindable connectivity/sync singleton.
- **`RpcEventBus`** — composite `IEventBus` that forwards events over the wire *and* delivers them locally.
- **`INetworkStatus`** — projected from the Fusion connection state.
- **`OutboxReplayer`** — hosted service that drains the outbox when the connection returns.

By default storage is in-memory. Add a durable backend (`AddYFexSqliteStorage`, `AddYFexIndexedDBStorage`) so the outbox and cache survive restarts — see [`04-storage-and-backends.md`](04-storage-and-backends.md).

> `EnableServerPushedEvents()` opts into the server→client event stream (`FusionEventStream` → `IEventBus` bridge) so the server can push events to connected clients.

## Server setup

```csharp
builder.Services.UseYFexMessagingRpcServer();
// or, if you handle messages with Wolverine:
builder.Services.UseYFexMessagingRpcServerWithWolverine();

var app = builder.Build();
app.MapYFexMessagingRpc();          // maps the /rpc/ws WebSocket endpoint
```

`UseYFexMessagingRpcServer` registers Fusion core, an `IHandlerInvoker` (default `LocalHandlerInvoker` resolves handlers from DI), an always-connected network status, and a `LocalDispatcher` for in-process static-helper calls. The server treats itself as always online; outbox/cache are no-op stubs there.

---

## The moving parts

### `IOutbox` — offline command queue

Commands implementing `IQueueable` are enqueued as an `OutboxEntry` (idempotency key, serialized MemoryPack payload, attempt count, last failure). `OutboxReplayer` drains them on reconnect. Overflow past `MaxEntries` (or TTL/size caps) is moved to the failure log rather than silently dropped.

```csharp
public interface IOutbox
{
    ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct) where T : ICommand;
    ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct);
    ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct);
    ValueTask RemoveAsync(Guid key, CancellationToken ct);
    int PendingCount { get; }
    event Action<OutboxEntry>? Enqueued;
    event Action<Guid>? Drained;
}
```

### `IClientCache` — offline query cache

Cacheable queries store their result under a dispatcher-generated key (`query:{TypeName}:{ParameterHash}`). When offline, the cached value is served. Supports TTL, optimistic `UpdateAsync` (read-modify-write), `MarkStaleAsync` (serve now, refresh on reconnect), and prefix scans for batch invalidation.

### `SyncStatus` — bindable UI state

Singleton implementing `INotifyPropertyChanged`. Bind these directly in the UI:

`IsOffline`, `IsSyncing`, `PendingCommandCount`, `LastSyncAt`, `LastSyncError`. Updated by the replayer and the network-status projection.

### `INetworkStatus`

`IsConnected` + a `State` enum (`Connected`, `Disconnected`, `Reconnecting`, `Syncing`) + a `Changed` event. Client uses a Fusion-backed projection; server uses `AlwaysConnectedNetworkStatus`.

### Conflict resolution

When a replayed command hits a server whose state moved on, an `IConflictResolver<TCommand>` decides the outcome. Three built-ins:

| Resolver | `ConflictPolicy` | Behavior |
|---|---|---|
| `EscalateConflictResolver<T>` | `Escalate` | Return a `Conflict` error to the caller. |
| `RetryLaterConflictResolver<T>` | `RetryLater` | Re-enqueue for another attempt. |
| `DiscardConflictResolver<T>` | `Discard` | Treat the conflict as a no-op and drop the command. |

Implement your own for domain-specific merges.

---

## How events travel over RPC

`RpcEventBus` composes the local `DefaultEventBus` with an `IRemoteEventBus`. `Publish`/`PublishAsync` deliver locally **and** forward across the wire; a server listener delivers inbound remote events into the local bus. Cross-process event records must be `[MemoryPackable]` (generator diagnostic **YFRPC0001** flags omissions).

---

## Rules of thumb

- Mark commands you want queued offline with `IQueueable`; mark queries you want served offline with `ICacheable`. The RPC layer honors the markers — you don't call the outbox/cache by hand.
- Always add a durable storage backend in production, or the outbox is lost on restart.
- Bind `SyncStatus` in your shell so users can see offline/pending state.
- Cross-process event and command payloads must be `[MemoryPackable]`.
