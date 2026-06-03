# YFex.Messaging

**Libraries:** YFex.Messaging, YFex.Messaging.Fusion, YFex.Messaging.Rpc, YFex.Messaging.Rpc.Sqlite, YFex.Messaging.Rpc.IndexedDb, YFex.Messaging.Rpc.Encryption, YFex.Messaging.Rpc.Wolverine

## 1. Overview

YFex.Messaging is an in-process event bus with reactive data-binding, offline-first command queuing, and server-push integration. At its core, `IEventBus` delivers events via broadcast, targeted, or grouped delivery with both sync and async recipients. `[Subscribe<T>]` wires lifecycle-aware handlers on `StateObject` subclasses -- subscriptions activate/deactivate automatically with the host ViewModel. `[Live]` properties bind ViewModel state to async data sources with configurable caching tiers (`Local`, `ServerShared`, `ClientPersistent`), polling, staleness tracking, and suspend/resume behavior. The offline stack -- `IClientCache`, `IOutbox`, `ISyncFailureLog` -- persists cached query results and queued commands across restarts via pluggable backends (SQLite for desktop/server, IndexedDB for Blazor WASM), with optional AES-256-GCM encryption. Fusion integration provides server-pushed invalidation over WebSocket for real-time updates without polling.

---

## 2. Core Concepts & Mental Model

### IEventBus

The singleton in-process event bus. All event delivery goes through it -- whether from user code, generated `[Subscribe<T>]` handlers, or the Fusion/RPC bridge:

```
                    Publish<T>(event)
                         |
                    IEventBus
                    /    |    \
              Broadcast  Target  Group
              (all)      (id)    (id)
               |          |       |
         Sync handlers    Sync    Sync
         Async handlers   Async   Async
```

Subscriptions return `IDisposable`. By default, the bus holds a **weak reference** to recipients -- they can be garbage-collected without explicit unsubscription. Pass `KeepAlive = true` for strong references.

### [Live] Reactive Properties

A `[Live]` method defines a data source. The source generator emits: a readonly property, `IsXLoading`, `XError`, `IsXStale`, and `RefreshXAsync()`. The property is backed by `ILiveState<T>`, which handles polling, caching, and lifecycle:

```
[Live] method
     |
  ILiveState<T>   ←──  ILiveStateFactory (Default or Fusion)
     |
  LiveCache tier
     |
  Local:           in-process only
  ServerShared:    Fusion ComputedState, server-push invalidation
  ClientPersistent: ServerShared + local persistent storage
```

### [Subscribe\<T\>] Declarative Handlers

`[Subscribe<T>]` marks a method as an event handler. The source generator emits adapter classes and wires subscription into the `StateObject` activation lifecycle (`OnActivateCascading` / `OnDeactivateCascading`) or the `MessagingHost` constructor.

### Offline Stack

Four interfaces form the offline persistence layer:

```
IClientCache ──── cached query results (key-value, typed)
IOutbox ──────── queued commands awaiting replay
ISyncFailureLog ─ commands that permanently failed
SyncStatus ────── bindable UI state (IsOffline, IsSyncing, PendingCommandCount)
```

Each has an in-memory implementation for tests and a persistent implementation backed by `IClientStorage` (SQLite or IndexedDB).

### IDispatcher Runtime Selection

The dispatcher routes static helper calls to the appropriate backend:

| Host | Dispatcher | Path |
|---|---|---|
| Client (Blazor/MAUI/WPF) | `FusionMessageBus` | Fusion proxy --> WebSocket --> Server |
| Server (Wolverine) | `LocalDispatcher` | Direct to `IHandlerInvoker` via `IMessageBus` |
| Tests / Simple apps | `LocalDispatcher` | DI-resolved `IQueryHandler<>` / `ICommandHandler<>` |

---

## 3. Integration Model & Lifecycle

### Client Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using YFex.Messaging;
using YFex.Messaging.Rpc;

var builder = WebApplication.CreateBuilder(args);

// Core event bus (if using messaging without RPC)
builder.Services.AddYFexMessaging();

// Full client stack: Fusion, cache, outbox, dispatcher, sync
builder.Services.AddYFexMessagingRpcClient(opts =>
{
    opts.WebSocketEndpoint = new Uri("wss://api.example.com");
    opts.Storage = ClientStorageBackend.Sqlite;   // or IndexedDb, Auto, InMemory
    opts.Encryption = EncryptionMode.AesGcm;
    opts.EncryptionKeySource = EncryptionKeySource.OsKeyStore;
    opts.OutboxEntryTtl = TimeSpan.FromDays(7);
});
```

`AddYFexMessagingRpcClient` registers:
- `IEventBus` (with RPC forwarding for server events)
- `ILiveStateFactory` (Fusion-backed)
- `IClientCache`, `IOutbox`, `ISyncFailureLog`
- `SyncStatus` (bindable singleton)
- `IDispatcher` (`FusionMessageBus`)
- `INetworkStatus` (Fusion `RpcPeer.ConnectionState` projection)
- `OutboxReplayer` (hosted service that drains the queue on reconnect)

### Server Setup

```csharp
using YFex.Messaging.Rpc;

// Minimal Fusion RPC server
builder.Services.UseYFexMessagingRpcServer();

// With Wolverine handler integration
builder.Services.UseYFexMessagingRpcServerWithWolverine(opts =>
{
    opts.ConvertExceptionsToResults = true;
});

// Map the WebSocket RPC endpoint in your middleware pipeline
app.MapYFexMessagingRpc();  // defaults to /rpc/ws
```

### Activation-Driven Subscription Lifecycle

`[Subscribe<T>]` on a `StateObject` (ViewModel) ties the subscription to activation:

```
ViewModel.Activate()
    → OnActivateCascading()
        → bus.Subscribe<T>(adapter)    ← generated code
    
ViewModel.Deactivate()
    → OnDeactivateCascading()
        → subscription.Dispose()       ← generated code
```

`[Subscribe<T>]` on a `MessagingHost` ties the subscription to the constructor/DI disposal:

```
MessagingHost constructor
    → OnHostStarting()
        → RegisterSubscription(bus.Subscribe<T>(adapter))   ← generated code

DI container shutdown
    → DisposeAsync()
        → all subscriptions disposed in reverse order
```

---

## 4. Step-by-Step Usage

### Publish an Event

```csharp
using YFex.Messaging;

// Inject IEventBus or use EventBusProvider.Current
IEventBus bus = EventBusProvider.Current;

// Synchronous broadcast
bus.Publish(new OrderCreated(orderId, customerId));

// Await async subscribers
await bus.PublishAsync(new OrderCreated(orderId, customerId));

// Targeted delivery: only subscribers with matching TargetId receive it
bus.Publish(new ChatMessage(text), new PublishOptions { TargetId = $"room:{roomId}" });

// Grouped delivery
bus.Publish(new StockUpdate(symbol, price), new PublishOptions { GroupId = "trading-floor" });
```

### Subscribe with [Subscribe\<T\>]

```csharp
using YFex.Messaging;
using YFex.State;

public partial class OrderViewModel : PageViewModel
{
    [Observable] public partial Order? CurrentOrder { get; set; }

    // Sync handler: called inline during Publish()
    [Subscribe<OrderCreated>(FilterBy = "CurrentOrder.Id")]
    private void OnOrderCreated(in OrderCreated e)
    {
        // Only fires when e.Id matches this.CurrentOrder.Id
        RefreshOrderAsync();
    }

    // Async handler
    [Subscribe<PaymentReceived>(DebounceMs = 500)]
    private async ValueTask OnPaymentReceived(PaymentReceived e, CancellationToken ct)
    {
        await RefreshPaymentStatusAsync(ct);
    }
}
```

> **Note:** The method must be on a `partial` class inheriting from `StateObject` (or its descendants: `MvvmStateObject`, `ViewModel`, `PageViewModel`) or from `MessagingHost`.

### Create a [Live] Property

```csharp
using YFex.Messaging;
using YFex.State;

public partial class CustomerDetailViewModel : PageViewModel
{
    [Observable] public partial Guid CustomerId { get; set; }

    [Live(
        Cache = LiveCache.ServerShared,
        StaleTimeMs = 30_000,
        DependsOn = [nameof(CustomerId)])]
    private async Task<Customer?> FetchCurrentCustomerAsync(CancellationToken ct)
    {
        return await Customer.Queries.GetById(CustomerId);
    }

    // The source generator creates:
    //   Customer? CurrentCustomer { get; }          — the reactive value
    //   bool IsCurrentCustomerLoading { get; }      — true while fetching
    //   Exception? CurrentCustomerError { get; }    — last error, if any
    //   bool IsCurrentCustomerStale { get; }        — true after StaleTimeMs
    //   Task RefreshCurrentCustomerAsync(CancellationToken ct)  — force re-fetch
}
```

The property name is derived from the method name by stripping `Fetch` prefix and `Async` suffix: `FetchCurrentCustomerAsync` becomes `CurrentCustomer`.

---

## 5. Deep Dive: Core API

### IEventBus

```csharp
public interface IEventBus
{
    // Subscribe a sync recipient. Dispose the return value to unsubscribe.
    IDisposable Subscribe<T>(IEventRecipient<T> recipient, SubscribeOptions options = default);

    // Subscribe an async recipient.
    IDisposable SubscribeAsync<T>(IAsyncEventRecipient<T> recipient, SubscribeOptions options = default);

    // Sync publish: sync recipients called inline, async recipients fire-and-forget.
    void Publish<T>(in T @event, PublishOptions options = default);

    // Async publish: sync recipients inline, then all async recipients awaited sequentially.
    ValueTask PublishAsync<T>(T @event, PublishOptions options = default, CancellationToken ct = default);
}
```

**Recipient interfaces:**

```csharp
// Sync: receives by ref for zero-copy value types
public interface IEventRecipient<T>
{
    void Receive(in T @event);
}

// Async
public interface IAsyncEventRecipient<T>
{
    ValueTask ReceiveAsync(T @event, CancellationToken ct);
}
```

**Delivery options:**

```csharp
// Subscribe-side filtering
public readonly struct SubscribeOptions
{
    public string? TargetId { get; init; }  // Only receive events published to this target
    public string? GroupId { get; init; }    // Only receive events published to this group
    public bool KeepAlive { get; init; }     // Strong reference (default: weak)
}

// Publish-side targeting
public readonly struct PublishOptions
{
    public string? TargetId { get; init; }  // Deliver only to subscribers with matching TargetId
    public string? GroupId { get; init; }    // Deliver only to subscribers with matching GroupId
}
```

**Delegate-based subscription (extension method):**

```csharp
using YFex.Messaging;

IDisposable token = bus.On<OrderCreated>(e => Console.WriteLine(e.OrderId));
```

### [Live] Attribute Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Cache` | `LiveCache` | `Local` | Where the value is cached and how invalidation is delivered |
| `PollMs` | `int` | `0` | Polling interval in ms. Zero means no polling |
| `PollDuringSuspend` | `bool` | `false` | Continue polling while host page is suspended |
| `SuspendBehavior` | `LiveSuspendBehavior` | `PauseAndRefreshOnResume` | What happens on page suspend/resume |
| `DependsOn` | `string[]?` | `null` | Observable property names that trigger re-fetch on change |
| `StaleTimeMs` | `int` | `0` | Ms after last fetch before `IsXStale` returns true. Zero = never stale |
| `PersistenceKey` | `string?` | `null` | Key for persistent storage (only with `ClientPersistent`) |

### ILiveState\<T\> Interface

```csharp
public interface ILiveState<T> : IDisposable
{
    T? Value { get; }                      // Current value (default while first fetch in-flight)
    bool IsLoading { get; }                // True while a fetch is executing
    Exception? Error { get; }              // Last exception from fetch method
    DateTimeOffset? LastFetchedAt { get; } // UTC timestamp of last successful fetch
    bool IsStale { get; }                  // True when value is older than StaleTimeMs
    bool IsFromOfflineCache { get; }       // True when served from persistent storage, not live
    Task RecomputeAsync(CancellationToken ct = default);  // Force a fresh fetch
    event Action<ILiveState<T>>? Updated;  // Fires after every completed fetch
}
```

### [Subscribe\<T\>] Attribute Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `KeepAlive` | `bool` | `false` | Strong reference -- survives host GC |
| `FilterBy` | `string?` | `null` | Comma-separated property paths for event filtering |
| `Target` | `string?` | `null` | Property name used as target id filter |
| `Group` | `string?` | `null` | Property name used as group id filter |
| `DebounceMs` | `int` | `0` | Fire once after N ms of silence |
| `ThrottleMs` | `int` | `0` | Fire immediately, then ignore for N ms |

> **Warning:** `DebounceMs` and `ThrottleMs` are mutually exclusive. Setting both produces a compile-time diagnostic.

**Method signatures:**

```csharp
// Sync handler (called inline during Publish)
[Subscribe<OrderCreated>]
private void OnOrderCreated(in OrderCreated e) { }

// Async handler (awaited during PublishAsync)
[Subscribe<OrderCreated>]
private async ValueTask OnOrderCreated(OrderCreated e, CancellationToken ct) { }
```

### MessagingHost

For long-lived singleton services (not ViewModels) that need event subscriptions for the application lifetime:

```csharp
using YFex.Messaging;

public class TelemetryService : MessagingHost
{
    [Subscribe<UserAction>]
    private void OnUserAction(in UserAction e)
    {
        // Subscribed in constructor via generated OnHostStarting()
        // Unsubscribed on DI container disposal via DisposeAsync()
    }

    [Subscribe<ErrorOccurred>(KeepAlive = true)]
    private async ValueTask OnError(ErrorOccurred e, CancellationToken ct)
    {
        await LogToExternalServiceAsync(e, ct);
    }
}
```

### IClientCache

```csharp
public interface IClientCache
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default);
    ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    ValueTask InvalidateAsync(string key, CancellationToken ct = default);
    ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default);
    ValueTask MarkStaleAsync(string key, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default);
}
```

Cache keys follow the format `query:{TypeName}:{ParameterHash}`. `UpdateAsync` reads, applies the mutator, and writes atomically -- used for optimistic updates. `MarkStaleAsync` flags an entry as stale without removing it, so it can still be served offline while triggering a refresh on reconnect.

### IOutbox

```csharp
public interface IOutbox
{
    ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand;
    ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default);
    ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default);
    ValueTask RemoveAsync(Guid key, CancellationToken ct = default);
    int PendingCount { get; }
    event Action<OutboxEntry>? Enqueued;
    event Action<Guid>? Drained;
}
```

`OutboxEntry` is `[MemoryPackable]` for efficient serialization to persistent storage:

```csharp
[MemoryPackable]
public sealed partial record OutboxEntry(
    Guid IdempotencyKey,
    string CommandTypeName,
    byte[] Payload,
    DateTimeOffset EnqueuedAt,
    int AttemptCount,
    string? LastFailure,
    DateTimeOffset? LastAttemptAt);
```

### ISyncFailureLog

```csharp
public interface ISyncFailureLog
{
    IReadOnlyList<SyncFailure> Failures { get; }
    SyncFailure? Find(Guid idempotencyKey);
    ValueTask AcknowledgeAsync(Guid idempotencyKey, CancellationToken ct = default);
    ValueTask RetryAsync(Guid idempotencyKey, CancellationToken ct = default);
    event Action<SyncFailure>? FailureAdded;
}

public sealed record SyncFailure(
    Guid IdempotencyKey,
    string CommandTypeName,
    byte[] Payload,
    string Reason,              // "max-retries", "expired", "conflict-escalated", "outbox-overflow"
    DateTimeOffset FailedAt,
    bool IsAcknowledged);
```

### SyncStatus (Bindable UI State)

```csharp
public sealed class SyncStatus : INotifyPropertyChanged
{
    public bool IsOffline { get; }              // True when disconnected
    public bool IsSyncing { get; }              // True while outbox is draining
    public int PendingCommandCount { get; }     // Number of commands awaiting replay
    public DateTimeOffset? LastSyncAt { get; }  // Last successful sync timestamp
    public Exception? LastSyncError { get; }    // Last sync error, if any
}
```

Bind directly to UI:

```xml
<!-- Avalonia XAML example -->
<TextBlock Text="{Binding SyncStatus.PendingCommandCount, StringFormat='Pending: {0}'}"
           IsVisible="{Binding SyncStatus.IsOffline}" />
```

### Sync Events

Published on `IEventBus` during the sync lifecycle:

```csharp
// Command enqueued to outbox
public sealed record CommandQueuedEvent(
    Guid IdempotencyKey, string CommandTypeName, DateTimeOffset QueuedAt) : IEvent;

// Command replayed (success or failure)
public sealed record CommandReplayedEvent(
    Guid IdempotencyKey, string CommandTypeName, bool Succeeded, string? ErrorMessage) : IEvent;

// Command moved to failure log permanently
public sealed record SyncFailureEvent(
    Guid IdempotencyKey, string CommandTypeName, string Reason) : IEvent;

// Network connectivity changed
public sealed record NetworkStatusChangedEvent(SyncState Previous, SyncState Current) : IEvent;
```

### Storage Backends

**SQLite (Desktop/Server):**

```csharp
services.AddYFexSqliteStorage(opts =>
{
    opts.DatabasePath = "yfex-cache.db"; // Default: app data folder
});
```

Dedicated `SqliteClientCache`, `SqliteOutbox`, `SqliteSyncFailureLog` with proper indexes. WAL mode enabled by default.

**IndexedDB (Blazor WASM):**

```csharp
services.AddYFexIndexedDBStorage(opts =>
{
    opts.UnavailableAction = IndexedDbUnavailableAction.FallbackToMemory;
});
```

JS interop layer over browser's IndexedDB. Falls back to in-memory in private browsing when configured.

**Auto-Detection:**

```csharp
services.AddYFexMessagingRpcClient(opts =>
{
    opts.Storage = ClientStorageBackend.Auto;
    // Auto → IndexedDB on Blazor WASM, SQLite elsewhere
});
```

### Encryption

AES-256-GCM encryption decorator wraps any storage backend:

```csharp
services.AddYFexEncryptedStorage(opts =>
{
    opts.KeySource = EncryptionKeySource.OsKeyStore; // DPAPI/Keychain/libsecret
});
```

Blob format: `[version:1][nonce:12][ciphertext:N][tag:16]`

Keys remain plaintext for indexability. Only values are encrypted. The AEAD authentication tag detects tampering.

### Fusion Integration

**`FusionLiveState<T>`** wraps a Fusion `ComputedState<T>` as `ILiveState<T>`. When the server's computed value changes, Fusion pushes invalidation over WebSocket, and the client re-fetches automatically -- no polling needed. Supports offline fallback: if the computation fails and a `loadFromCache` delegate is provided, the cached value is served with `IsFromOfflineCache = true`.

**`FusionStateBinding<T>`** bridges Fusion `IState<T>` into the State activation lifecycle for manual scenarios outside the `[Live]` generator.

**`FusionMessageBus`** is the client-side `IDispatcher` that routes queries/commands through Fusion compute service proxies over WebSocket.

**Event Channels:** Server-side `EventChannelHost<TEvent>` publishes events via Fusion `[ComputeMethod]`. Client-side `FusionEventStream<TEvent>` consumes them and injects into the local `IEventBus`.

### Wolverine Integration

```csharp
services.UseYFexMessagingRpcServerWithWolverine(opts =>
{
    opts.ConvertExceptionsToResults = true;
    opts.EnableServerPushedEvents();
});
```

| Component | Role |
|---|---|
| `WolverineHandlerInvoker` | Routes commands/queries to Wolverine's `IMessageBus` |
| `WolverineLocalDispatcher` | Server-side static-helper calls bypass Fusion |
| `ResultConversionMiddleware` | Converts exceptions to `Result.Fail()` across the wire |
| `EventBridge<TEvent>` | Bridges Wolverine cascading messages to Fusion event channels |

### Handler Interfaces (for LocalDispatcher)

```csharp
using YFex.Cqrs;
using YFex.Messaging.Rpc;

public class GetCustomerHandler : IQueryHandler<GetByIdQuery, Customer>
{
    public Task<Customer> HandleAsync(GetByIdQuery query, CancellationToken ct)
    {
        // Resolve from database, etc.
    }
}

public class CreateCustomerHandler : ICommandHandler<CreateCommand, Customer>
{
    public Task<Customer> HandleAsync(CreateCommand cmd, CancellationToken ct)
    {
        // Write to database, return created entity
    }
}

public class DeleteCustomerHandler : ICommandHandler<DeleteCommand>
{
    public Task HandleAsync(DeleteCommand cmd, CancellationToken ct)
    {
        // Void command handler
    }
}
```

---

## 6. Common Patterns & Recipes

### Event-Driven ViewModel Update

```csharp
using YFex.Messaging;
using YFex.State;

public partial class NotificationBadgeViewModel : ViewModel
{
    [Observable] public partial int UnreadCount { get; set; }

    [Subscribe<NotificationReceived>]
    private void OnNotification(in NotificationReceived e)
    {
        UnreadCount++;
    }

    [Subscribe<NotificationRead>]
    private void OnRead(in NotificationRead e)
    {
        UnreadCount = Math.Max(0, UnreadCount - 1);
    }
}
```

### Live Property with Polling

```csharp
using YFex.Messaging;
using YFex.State;

public partial class DashboardViewModel : PageViewModel
{
    [Live(PollMs = 10_000, SuspendBehavior = LiveSuspendBehavior.StayLive)]
    private async Task<DashboardStats> FetchStatsAsync(CancellationToken ct)
    {
        return await Dashboard.Queries.GetStats(ct);
    }

    // Stats refreshes every 10 seconds, even while the page is on the back-stack
}
```

### Offline Write with Queuing

```csharp
using YFex.Cqrs;
using YFex.Cqrs.Configuration;

public partial class Task
{
    public static partial class Commands
    {
        // IQueueable: enqueued when offline, replayed on reconnect
        public partial record CompleteCommand(Guid TaskId) : ICommand, IQueueable;
    }
}

public class TaskConfig : IAggregateConfiguration<Task>
{
    public void Configure(AggregateConfigurationBuilder<Task> b)
    {
        b.Command<Task.Commands.CompleteCommand>()
            .IdempotencyKey(cmd => $"task:complete:{cmd.TaskId}")
            .OnConflict(ConflictPolicy.Discard)  // Idempotent: discard duplicates
            .OnOffline((cmd, ct) =>
            {
                // Mark as complete in local UI immediately
                return ValueTask.CompletedTask;
            })
            .Invalidates<Task.Queries.GetByIdQuery, Task>(
                (q, cmd) => q.Id == cmd.TaskId);
    }
}

// Usage: works identically online or offline
QueueableResult result = await Task.Commands.Complete(taskId);
if (result.IsQueued)
    ShowToast("Saved offline. Will sync when connected.");
```

### Sync Failure Handling

```csharp
using YFex.Messaging;
using YFex.Messaging.Rpc;

public partial class SyncDashboardViewModel : PageViewModel
{
    private readonly ISyncFailureLog _failureLog;
    private readonly SyncStatus _syncStatus;

    [Observable] public partial IReadOnlyList<SyncFailure> Failures { get; set; }

    [Subscribe<SyncFailureEvent>]
    private void OnSyncFailure(in SyncFailureEvent e)
    {
        Failures = _failureLog.Failures;
    }

    [StateCommand]
    private async Task RetryFailedCommandAsync(Guid idempotencyKey, CancellationToken ct)
    {
        await _failureLog.RetryAsync(idempotencyKey, ct);
        Failures = _failureLog.Failures;
    }

    [StateCommand]
    private async Task DismissFailureAsync(Guid idempotencyKey, CancellationToken ct)
    {
        await _failureLog.AcknowledgeAsync(idempotencyKey, ct);
        Failures = _failureLog.Failures;
    }
}
```

---

## 7. Testing & Mocking

### In-Memory Event Bus

`DefaultEventBus` works out of the box without DI:

```csharp
using YFex.Messaging;

[Fact]
public void Publish_delivers_to_subscriber()
{
    var bus = new DefaultEventBus();
    OrderCreated? received = null;

    bus.On<OrderCreated>(e => received = e);
    bus.Publish(new OrderCreated(Guid.NewGuid(), Guid.NewGuid()));

    Assert.NotNull(received);
}
```

### InMemoryClientCache for Cache Tests

```csharp
using YFex.Messaging.Rpc;

[Fact]
public async Task Cache_serves_value_after_set()
{
    var cache = new InMemoryClientCache();

    await cache.SetAsync("key", new Customer("Alice"), TimeSpan.FromMinutes(5));
    var result = await cache.GetAsync<Customer>("key");

    Assert.Equal("Alice", result!.Name);
}

[Fact]
public async Task MarkStale_keeps_value_but_flags()
{
    var cache = new InMemoryClientCache();
    await cache.SetAsync("key", 42);

    await cache.MarkStaleAsync("key");

    var value = await cache.GetAsync<int>("key");
    Assert.Equal(42, value);
    Assert.True(cache.IsStale("key"));
}
```

### InMemoryOutbox for Offline Tests

```csharp
using YFex.Messaging.Rpc;

[Fact]
public async Task Outbox_enqueue_returns_idempotency_key()
{
    var outbox = new InMemoryOutbox();

    var queued = await outbox.EnqueueAsync(new TestCommand("data"));

    Assert.NotEqual(Guid.Empty, queued.IdempotencyKey);
    Assert.Equal(1, outbox.PendingCount);
}
```

### Full Dispatcher Test with Offline Simulation

```csharp
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using Microsoft.Extensions.DependencyInjection;

[Fact]
public async Task Offline_command_is_queued()
{
    var sp = new ServiceCollection().BuildServiceProvider();
    var registry = CompiledMessagingRegistry.Build(
        new IAggregateConfiguration[] { new TaskConfig() });

    // Simulate offline: use a disconnected network status
    var offlineStatus = new AlwaysDisconnectedStatus();
    var outbox = new InMemoryOutbox();

    var dispatcher = new LocalDispatcher(
        new LocalHandlerInvoker(sp), registry, offlineStatus,
        new InMemoryClientCache(), outbox, new DefaultEventBus(), sp);
    YFexDispatcherProvider.Set(dispatcher);

    var result = await Task.Commands.Complete(Guid.NewGuid());

    Assert.True(result.IsQueued);
    Assert.Equal(1, outbox.PendingCount);
}
```

---

## 8. Troubleshooting & Gotchas

### Weak Reference GC

By default, `IEventBus` holds weak references to subscribers. If the subscriber is a local variable with no other references, it can be garbage-collected and silently stop receiving events:

```csharp
// BAD: handler may be GC'd immediately
bus.Subscribe(new MyHandler());

// GOOD: keep a reference or use KeepAlive
var handler = new MyHandler();
var token = bus.Subscribe(handler, new SubscribeOptions { KeepAlive = true });
```

> **Tip:** `[Subscribe<T>]` on a `StateObject` handles this automatically -- the generated code stores the subscription token in the activation lifecycle. You only need to worry about manual `bus.Subscribe()` calls.

### Missing Activation

`[Subscribe<T>]` and `[Live]` only work when the host `StateObject` is activated. If your ViewModel never calls `Activate()`, the subscriptions are never created and the live properties never fetch:

```csharp
// The NavigatR framework calls Activate() automatically for PageViewModels.
// For standalone ViewModels, you must activate manually:
var vm = new MyViewModel();
vm.Activate();  // Now [Subscribe] and [Live] are wired
```

### Event Type Registration for AOT

In AOT (ahead-of-time) compilation environments, the event bus needs to know event types at startup. Use `ConfigureEventBus` to register them:

```csharp
services.AddYFexMessagingRpcClient(opts =>
{
    opts.ConfigureEventBus = bus =>
    {
        // Register all event types that will be received from the server
        bus.RegisterEventType<OrderCreated>();
        bus.RegisterEventType<PaymentReceived>();
    };
});
```

### Fusion Not Configured for ServerShared/ClientPersistent

Using `LiveCache.ServerShared` or `LiveCache.ClientPersistent` without `AddYFexMessagingRpcClient()` results in the default `DefaultLiveStateFactory` being used instead of `FusionLiveStateFactory`. The property will still work but without server-push invalidation -- it falls back to polling or manual refresh only.

### OutboxReplayer Not Running

If queued commands are never replayed after reconnection, ensure `AddYFexMessagingRpcClient()` was called (it registers `OutboxReplayer` as a hosted service). In test scenarios without a generic host, the replayer does not run automatically.

---

## 9. Reference Summary

### LiveCache Values

| Value | Caching | Invalidation | Persistence |
|---|---|---|---|
| `Local` | In-process Fusion `ComputedState` | In-memory only | None |
| `ServerShared` | Server-side Fusion registry | Stl.Rpc push over WebSocket | None |
| `ClientPersistent` | ServerShared + local file/DB | Stl.Rpc push + local restore | SQLite or IndexedDB |

### LiveSuspendBehavior Values

| Value | On Suspend | On Resume |
|---|---|---|
| `PauseAndRefreshOnResume` | Stop receiving updates | Re-fetch if stale |
| `StayLive` | Keep subscription active | No-op |
| `AlwaysRefetchOnResume` | Stop receiving updates | Force fresh fetch regardless of staleness |
| `FreezeOnSuspend` | Freeze cached value | No auto-refresh |

### SyncState Values

| Value | Meaning |
|---|---|
| `Connected` | Online, all systems operational |
| `Disconnected` | No network connectivity |
| `Reconnecting` | Attempting to re-establish connection |
| `Syncing` | Connected, outbox replay in progress |

### ClientStorageBackend Values

| Value | Backend |
|---|---|
| `Auto` | IndexedDB on Blazor WASM, SQLite elsewhere |
| `InMemory` | Volatile in-process (tests) |
| `Sqlite` | SQLite database with WAL mode |
| `IndexedDb` | Browser IndexedDB via JS interop |

### EncryptionMode Values

| Value | Behavior |
|---|---|
| `None` | No encryption (default) |
| `AesGcm` | AES-256-GCM with AEAD authentication |

### EncryptionKeySource Values

| Value | Key Storage |
|---|---|
| `OsKeyStore` | DPAPI (Windows), Keychain (macOS), libsecret (Linux) |
| `Provided` | Key supplied by application code |

### INetworkStatus Interface

```csharp
public interface INetworkStatus
{
    bool IsConnected { get; }
    SyncState State { get; }
    event Action<SyncState>? Changed;
}
```

### All Sync Lifecycle Events

| Event | When Published |
|---|---|
| `CommandQueuedEvent` | Command enqueued to outbox (offline) |
| `CommandReplayedEvent` | Queued command replayed (success or failure) |
| `SyncFailureEvent` | Command moved to failure log permanently |
| `NetworkStatusChangedEvent` | Connectivity state transition |

### Handler Interfaces

| Interface | For |
|---|---|
| `IQueryHandler<TQuery, TResult>` | Query handlers (used by `LocalHandlerInvoker`) |
| `ICommandHandler<TCommand>` | Void command handlers |
| `ICommandHandler<TCommand, TResult>` | Result-bearing command handlers |
| `IEventRecipient<T>` | Sync event subscriber |
| `IAsyncEventRecipient<T>` | Async event subscriber |
