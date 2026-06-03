# Messaging Internals

This document covers the internal architecture of the YFex.Messaging stack: the event bus handler bucket design, snapshot-on-read publishing, outbox replay algorithm, LocalDispatcher dispatch pipeline, cache key derivation, RPC event bus composite routing, Fusion integration internals, storage backend schemas, encryption blob format, the Wolverine adapter layer, and the messaging source generator pipelines. Read this when you're modifying event delivery, debugging offline sync, extending storage backends, or working on the messaging generator.

---

## Event Bus Architecture

### Handler Buckets

`DefaultEventBus` uses `ConcurrentDictionary<Type, object>` where each value is an `EventHandlerBucket<T>`:

```csharp
class EventHandlerBucket<T>
{
    SyncEntry[] _syncHandlers;   // IEventRecipient<T> + metadata (TargetId, GroupId, WeakRef)
    AsyncEntry[] _asyncHandlers; // IAsyncEventRecipient<T> + metadata
}
```

> **Why separate sync/async arrays?** Sync handlers are invoked inline during `Publish()` — they block the caller. Async handlers are fire-and-forget during sync publish, or awaited during `PublishAsync()`. Separating them avoids a type check per handler on every publish.

### Snapshot-on-Read

Publishing takes a snapshot of the handler array before iterating. Concurrent subscribe/unsubscribe operations create new arrays (copy-on-write) without blocking publishers.

> **Why copy-on-write instead of locking?** Publishing is the hot path (high frequency). Subscription changes are rare (typically only on activation/deactivation). Copy-on-write makes publishing lock-free at the cost of copying the handler array on subscription changes — a good tradeoff for typical event bus usage patterns.

### Weak Reference Compaction

Weak-reference subscriptions are checked on every publish. Dead references are lazily compacted — the handler array is rebuilt only when dead entries are found.

> **💡 Tip:** Weak references are the default (`KeepAlive = false`). This means a ViewModel that forgets to unsubscribe will still be GC'd. The compaction on publish ensures the dead handler is cleaned up naturally. Use `KeepAlive = true` only for singletons that must never be collected.

### Delivery Filtering

On publish, each handler is checked against `PublishOptions`:
1. If `TargetId` set: only handlers with matching `SubscribeOptions.TargetId` receive
2. If `GroupId` set: only handlers with matching `SubscribeOptions.GroupId` receive
3. Otherwise: broadcast to all handlers

> **📌 Note:** Filtering is O(N) over the handler array. For high-fanout scenarios (hundreds of handlers for one event type), consider using `GroupId` to partition subscribers instead of broadcasting.

---

## Outbox Replay

`OutboxReplayer` is a `BackgroundService` that drains the offline command queue:

```
while running:
  wait for INetworkStatus.Changed → Connected
  or periodic 30s heartbeat

  for each pending entry (oldest first):
    if entry.EnqueuedAt + OutboxEntryTtl < now:
      move to ISyncFailureLog("expired")
      continue

    deserialize command from Payload
    dispatch via IDispatcher.CommandAsync<>()

    if success:
      remove from outbox
      publish CommandReplayedEvent(Succeeded=true)
    if conflict:
      resolve via IConflictResolver (from CompiledMessagingRegistry)
      Escalate → move to failure log
      RetryLater → increment attempt count, backoff
      Discard → remove silently
    if transient failure:
      increment attempt count
      if attempts >= RetryPolicy.MaxAttempts:
        move to failure log
      else:
        exponential backoff, retry next cycle

  update SyncStatus (PendingCommandCount, LastSyncAt, IsSyncing)
```

> **Why oldest-first?** Command ordering matters for causal consistency. If the user created an order and then added items to it, replaying in reverse would fail because the items reference a non-existent order.

> **⚠️ Warning:** The 30s heartbeat is a fallback. The primary trigger is `INetworkStatus.Changed`. If the network status implementation doesn't fire events reliably (e.g., in some Blazor WASM scenarios), the heartbeat ensures the queue is eventually drained.

---

## LocalDispatcher Pipeline

### Query Dispatch

```
1. Look up QueryPolicy from CompiledMessagingRegistry (FrozenDictionary<Type, QueryPolicy>)
2. Validate (if policy.Validate != null) — composite delegate, short-circuits on first failure
3. Authorize (if policy.Authorize != null) — fused predicate
4. Check INetworkStatus:
   - Online: resolve IQueryHandler<TQuery,TResult> from DI → InvokeAsync
             if ICacheable: write result to IClientCache
   - Offline: if ICacheable: serve from IClientCache (IsFromOfflineCache=true)
              else: return Result.Fail(Error.NoNetwork)
```

> **Why validate before checking network?** Even offline, we want to reject malformed queries early — saves the user from discovering validation errors only after reconnecting.

### Command Dispatch

```
1. Look up CommandPolicy from CompiledMessagingRegistry
2. Validate
3. Authorize
4. Apply optimistic updates (if policy.Optimistic != null):
   - Look up matching cached query values via Match delegate
   - Apply the Optimistic.Apply delegate to produce predicted result
   - Write updated values to IClientCache
5. Check INetworkStatus:
   - Online: invoke handler → on success: invalidate cache targets
             → on failure: rollback optimistic updates (restore pre-mutation snapshot)
   - Offline:
     a. Run OnOfflineHandler (if configured) — lambda or DI-resolved type
     b. If IQueueable: enqueue to IOutbox → return Queued(idempotencyKey)
        Mark InvalidationTargets as stale in cache
     c. If not queueable: return Result.Fail(Error.NoNetwork)
```

> **📌 Note:** Optimistic updates apply both online and offline. They're about UI snappiness, not network state. The rollback path differs: online failures restore the snapshot; offline failures leave the optimistic value in place (it will be corrected when the outbox replays on reconnect).

---

## Cache Key Derivation

For `IClientCache`:
- Pattern: `query:{TypeName}:{GetHashCode()}`
- `GetHashCode()` comes from the query record's structural equality
- Prefix-based lookup (`GetKeysWithPrefixAsync("query:GetCustomerByIdQuery:")`) for wildcard invalidation

For `StorageBackedClientCache`:
- Values serialized with `System.Text.Json` for storage in `IClientStorage`
- Keys stored as-is (plaintext, even when encryption is enabled)

> **Why plaintext keys?** Keys must be indexable for prefix scans during wildcard invalidation. Encrypting keys would require decrypting every key to find matches — O(N) decryption instead of O(1) prefix lookup.

---

## RpcEventBus — Composite Routing

`RpcEventBus` wraps both local `DefaultEventBus` and remote `IRemoteEventBus`:

```
Publish(event):
  1. Deliver to local DefaultEventBus (all in-process subscribers)
  2. If TargetId or GroupId set:
     Serialize event → RpcEventEnvelope (TypeName + MemoryPack payload)
     Send to IRemoteEventBus.PublishAsync() for server relay to other clients

StartServerListener():
  1. Connect to IRemoteEventBus.SubscribeToServerEventsAsync()
  2. For each RpcEventEnvelope from server:
     Deserialize → inject into local DefaultEventBus
  3. Deduplication by sequence number (monotonic)
```

### AOT-Safe Type Registration

`RegisterEventType<T>()` creates a pre-compiled deserializer keyed by type name string.

> **⚠️ Warning:** Without `RegisterEventType<T>()`, the bus falls back to `Type.GetType()` + reflection for deserialization. This works on JIT runtimes but **fails on NativeAOT**. Always pre-register event types in AOT scenarios. The `[Subscribe<T>]` generator calls `RegisterEventType<T>()` automatically for all subscribed types, but manually published events from the server must be registered explicitly.

---

## Fusion Integration

### FusionLiveState\<T\>

Wraps `StateFactory.NewComputed<T>()`:
- Computation delegate: the user's `[Live]` method
- On `state.Updated`: check `Computed.IsConsistent()`, update `IsLoading`, fire `Updated` event
- Optional persistence: `loadFromCache` on construct, `saveToCache` on fresh fetch
- Marshals `Updated` to UI thread via captured `SynchronizationContext`

> **Why check `IsConsistent()`?** Fusion's `ComputedState` fires `Updated` both when a new value is computed and when the old value is invalidated (marked for recomputation). Checking `IsConsistent()` distinguishes "new value ready" from "old value stale, recomputation pending" — the latter maps to `IsLoading = true`.

### FusionMessageBus

Builder pattern accumulates routing entries:

```csharp
builder.AddQuery<GetByIdQuery, Customer>(
    (q, ct) => fusionProxy.GetByIdAsync(q, ct));
```

At build time, creates `FrozenDictionary<Type, Delegate>` for O(1) dispatch. `FusionMessageBus.QueryAsync<TQuery, TResult>()` looks up the proxy call and invokes it.

> **📌 Note:** The routing table is populated by the CQRS source generator (Pipeline C, gated on `YFEX_RPC`). The generated `{Aggregate}RpcRegistrations.g.cs` calls `builder.AddQuery<>()` / `builder.AddCommand<>()` for every discovered query/command.

### Event Channels

Server-side `EventChannelHost<TEvent>`:
- `Append(TEvent)` increments sequence counter, stores event, calls `Computed.Invalidate()`
- `[ComputeMethod] GetEventsAsync(filter, sinceSeq, ct)` returns events since the given sequence

Client-side `FusionEventStream<TEvent>`:
- Holds `ComputedState<EventCursor<TEvent>>` subscribed to `IEventChannel<TEvent>`
- On update: new events forwarded to local `IEventBus`
- Deduplication via monotonic sequence number

> **Why server-push via ComputeMethod instead of raw WebSocket messages?** Fusion's `ComputedState` handles reconnection, deduplication, and batching automatically. A raw WebSocket implementation would need to reimplement all of this. The sequence number provides an additional dedup layer for the rare case where Fusion delivers a duplicate during reconnection.

---

## Storage Backends

### SQLite Schema

```sql
CREATE TABLE Cache (Key TEXT PK, Value BLOB, WrittenAt INT, ExpiresAt INT);
CREATE INDEX Cache_ExpiresAt ON Cache(ExpiresAt) WHERE ExpiresAt IS NOT NULL;

CREATE TABLE Outbox (IdempotencyKey TEXT PK, CommandTypeName TEXT, Payload BLOB,
                     EnqueuedAt INT, AttemptCount INT, LastAttemptAt INT, LastFailureReason TEXT);
CREATE INDEX Outbox_EnqueuedAt ON Outbox(EnqueuedAt);

CREATE TABLE SyncFailures (Id TEXT PK, IdempotencyKey TEXT, CommandTypeName TEXT,
                           Payload BLOB, FailureReason TEXT, OccurredAt INT, RetryCount INT);
```

WAL mode enabled by default (`PRAGMA journal_mode = WAL`) for concurrent reads. Prepared statement caching for hot paths. `SqliteConnectionFactory` manages a shared connection and runs schema initialization (`CREATE TABLE IF NOT EXISTS`) on first open.

> **💡 Tip:** The SQLite backend uses Option B (dedicated tables per concern) rather than Option A (generic key-value with prefix scans). Dedicated tables enable proper indexes — the `Outbox_EnqueuedAt` index makes oldest-first drain O(log N) instead of O(N).

### IndexedDB

JS interop via `IJSRuntime`. Three object stores: `cache`, `outbox`, `failures`. Handles `QuotaExceededError` gracefully. Falls back to `InMemoryClientStorage` in private browsing or SSR.

> **⚠️ Warning:** IndexedDB transactions are short-lived — they auto-commit when control returns to the event loop. Each JS interop call is a separate transaction. Batch operations (e.g., draining 10 outbox entries) make 10 separate transactions, not one atomic batch.

### Encryption

`EncryptedClientStorage` wraps any `IClientStorage`:
- `SetAsync`: `plaintext → AesGcmValueProtector.Protect() → ciphertext`
- `GetAsync`: `ciphertext → AesGcmValueProtector.Unprotect() → plaintext`
- Keys stored plaintext for indexability (see Cache Key Derivation above)
- Blob format: `[0x01][nonce:12][ciphertext][tag:16]`
- Version byte (`0x01`) enables future key rotation without breaking existing data

> **📌 Note:** The AEAD authentication tag (16 bytes) detects tampering. `Unprotect()` returns `null` on authentication failure rather than throwing — callers treat corrupted entries as cache misses.

---

## Wolverine Adapter

`WolverineHandlerInvoker` is a thin wrapper over `IMessageBus`:

```csharp
public Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    => _bus.InvokeAsync<T>(message, ct);
```

> **Why the abstraction?** `IHandlerInvoker` decouples the generated Fusion server implementations from Wolverine. A hypothetical `YFex.Messaging.Rpc.MediatR` package could provide its own invoker without touching the contract generation layer.

`WolverineLocalDispatcher` bypasses Fusion entirely for server-side static helper calls. No offline/cache/outbox logic — the server has no offline scenario.

`WolverineEventBridgeHandler<TEvent>` is a Wolverine cascading handler that calls `EventChannelHost<TEvent>.Append()` to trigger Fusion invalidation on every published event.

`ResultConversionMiddleware` catches handler exceptions and converts them to `Result.Fail(Error)` for wire-friendly no-throw control flow.

> **💡 Tip:** `ResultConversionMiddleware` must run as the **outermost** middleware on the response path. If placed inside Wolverine's retry middleware, retries would see the `Result.Fail()` wrapper instead of the original exception and wouldn't trigger.

---

## Generator Pipelines

### [Subscribe\<T\>] Pipeline

```
ForAttributeWithMetadataName("YFex.Messaging.SubscribeAttribute`1")
  → MessagingParser.IsCandidateSyntax
  → MessagingParser.Transform → SubscribeMethodModel
  → Group by class → SubscribeClassModel
  → MessagingEmitter.Emit
```

Emits per-class:
- Private adapter classes implementing `IEventRecipient<T>` or `IAsyncEventRecipient<T>`
- `OnActivateCascading()` override: create adapter, call `EventBusProvider.Current.Subscribe(adapter, options)`
- `OnDeactivateCascading()` override: dispose subscription tokens

> **📌 Note:** For `MessagingHost` subclasses, the generator wires subscriptions into `OnHostStarting()` instead of `OnActivateCascading()`, and calls `RegisterSubscription()` with each token. Disposal is handled by `MessagingHost.DisposeAsync()`.

### [Live] Pipeline

```
ForAttributeWithMetadataName("YFex.Messaging.LiveAttribute")
  → LiveParser.IsCandidateSyntax
  → LiveParser.Transform → LiveMethodModel
  → Group by class → LiveClassModel
  → LiveEmitter.Emit
```

Emits per-method:
- `ILiveState<T>` backing field
- Computed property (returns `_liveState?.Value`)
- `IsXLoading` / `XError` / `IsXStale` companion properties
- `RefreshXAsync()` method
- Activation lifecycle: create `ILiveState<T>` on activate, dispose on deactivate
- DependsOn wiring: subscribe to named observable properties, call `RecomputeAsync` on change
- Poll support: `Task.Delay(PollMs)` loop during activation

> **⚠️ Warning:** The `DependsOn` wiring subscribes to observable property changes via `[ReactsTo]`-equivalent generated code. If a `DependsOn` property name is misspelled, the generator emits `YFLIV0001` — but only if the property doesn't exist at all. A property that exists but isn't `[Observable]` will silently fail to trigger refreshes.
