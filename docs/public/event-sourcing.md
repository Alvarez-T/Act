# Event Sourcing

**Libraries:** `YFex.EventSourcing`, `YFex.EventSourcing.Generator`, `YFex.EventSourcing.Marten`
**Repo:** sibling `YFex.EventSourcing` (`../../../YFex.EventSourcing`)

An **event-sourcing write model**: an aggregate's state is an append-only stream of domain events, folded to
rebuild. The store is provider-neutral — an in-house `IKeyValueStore`-backed store for client/offline/WASM/AoT,
and Marten for the authoritative transactional server. Event-sourced aggregate commands are *ordinary* CQRS
commands, so they inherit validation, authorization, resilience, cache invalidation, and the offline outbox with
no new dispatcher concept. Committed events feed projections (read models) and process managers.

> The aggregate **is** the domain model (write model / consistency boundary). Read models, queries/DTOs, and
> cross-aggregate coordination live *outside* it. Event sourcing is only *how the aggregate is persisted*.

## Setup (in-house / client)

```csharp
services.AddSingleton<IKeyValueStore, MemoryKeyValueStore>();   // or a durable backend (SQLite / IndexedDB)
services.AddSingleton<IEventBus, DefaultEventBus>();
services.AddSingleton<ICache>(sp => new KeyValueCache(sp.GetRequiredService<IKeyValueStore>()));

services.AddYFexEventSourcing().UseInHouse();
services.AddGeneratedEventSourcing();   // generated: aggregate command handlers, projections + host, snapshot cadences
```

`AddGeneratedEventSourcing()` also has an overload on `LocalHandlerInvokerRegistryBuilder` so the ES aggregate
commands join the reflection-free handler registry the `LocalDispatcher` uses:

```csharp
var registry = new LocalHandlerInvokerRegistryBuilder()
    .AddGeneratedEventSourcing()   // event-sourced aggregate commands
    .Build();
```

## Defining an aggregate

Aggregates are discovered by **deriving the base class** — no `[EventSourced]` marker (projections likewise, by
implementing `IProjection`). There are three event-modeling styles, all with the same per-case, name-keyed
serialization (never `[MemoryPackUnion]`):

- **Union** `EventSourcedAggregate<OrderEvent>` — closed set, compiler-checked exhaustive `When`, fastest (recommended).
- **Marker interface** `EventSourcedAggregate<ICartEvent>` — open set, one anchor interface.
- **`When`-convention** `EventSourcedAggregate` (non-generic) — no common event type; discovered from the
  `When(TSpecificEvent)` methods. Events can be shared across aggregates.

The union example below; the others differ only in the base and how the fold is written.

Events are `[MemoryPackable]` records grouped into a native `union`; the aggregate derives
`EventSourcedAggregate<TEvent>` and is `partial`.

```csharp
[MemoryPackable] public sealed partial record OrderStarted(AggregateId Id, CustomerId Customer, Currency Currency);
[MemoryPackable] public sealed partial record ItemAdded(AggregateId Id, Sku Sku, Quantity Qty, Money UnitPrice);
[MemoryPackable] public sealed partial record OrderPlaced(AggregateId Id, Money Total);
[MemoryPackable] public sealed partial record OrderShipped(AggregateId Id, string Tracking);

public readonly union OrderEvent(OrderStarted, ItemAdded, OrderPlaced, OrderShipped /* … */);
public enum OrderStatus : byte { Draft, Placed, Shipped, Cancelled }

[Snapshot(every: 100)]                         // optional; see "Snapshots"
public sealed partial class Order : EventSourcedAggregate<OrderEvent>
{
    // STATE — folded from events; only When mutates it
    public OrderStatus Status { get; private set; } = OrderStatus.Draft;
    private readonly Dictionary<Sku, LineItem> _lines = new();
    public Money AmountPaid { get; private set; }

    // COMPUTED QUERIES — not stored
    public Money Total => _lines.Values.Aggregate(Money.Zero(Currency), (a, l) => a + l.LineTotal);
    public bool  IsPaidInFull => !(Total - AmountPaid).IsPositive;

    // DECISIONS — business rules → guard → Emit
    public void Place()
    {
        Ensure(Status == OrderStatus.Draft, new DomainError.NotDraft(Status));
        Ensure(_lines.Count > 0, new DomainError.EmptyOrder());
        Emit(new OrderPlaced(Id, Total));
    }

    public OrderShipped Ship(string tracking)
    {
        Ensure(Status == OrderStatus.Placed, new DomainError.NotDraft(Status));
        Ensure(IsPaidInFull, new DomainError.NotPaidInFull(Total - AmountPaid));
        var e = new OrderShipped(Id, tracking);
        Emit(e);
        return e;
    }

    // THE FOLD — pure, no side effects, the ONLY place setters run (compiler-checked switch over the union)
    protected override void When(OrderEvent e)
    {
        switch (e)
        {
            case OrderStarted s:    Id = s.Id; Customer = s.Customer; Currency = s.Currency; break;
            case ItemAdded a:       _lines[a.Sku] = new LineItem(a.Sku, a.Qty, a.UnitPrice);  break;
            case OrderPlaced:       Status = OrderStatus.Placed;                               break;
            case OrderShipped:      Status = OrderStatus.Shipped;                              break;
            // … remaining cases …
        }
    }
}
```

- **`Emit(evt)`** folds the event, buffers it as uncommitted, and bumps `Version`. **`Ensure(cond, error)`** throws
  a typed `DomainException(error)` when an invariant is violated — which the CQRS dispatcher turns into a failed
  `Result`, so throwing composes with the pipeline.
- **`When`** is the fold — pure, and the only code that mutates state. Rebuilding from history and applying a new
  command run the same `When`, so state can never diverge.
- Invariant errors are a native `union` (`DomainError`); guard with the case records.

## Commands & handlers

An aggregate command is a normal CQRS command (add `IQueueable` for offline support). The decision is a static
`Decide(command, aggregate)` on an `[AggregateCommandHandler]` class:

```csharp
public sealed record ShipOrderCommand(AggregateId OrderId, string Tracking) : ICommand<OrderShipped>, IQueueable;

[AggregateCommandHandler]
public static class ShipOrder
{
    public static OrderShipped Decide(ShipOrderCommand cmd, Order order) => order.Ship(cmd.Tracking);
}
```

The generator emits an ordinary `ICommandHandler<ShipOrderCommand, OrderShipped>` that opens an
`IAggregateSession`, fetches the aggregate for writing (by the command's `AggregateId` property), runs `Decide`,
and saves. Dispatch through the existing dispatcher:

```csharp
var result = await dispatcher.CommandAsync<ShipOrderCommand, OrderShipped>(new ShipOrderCommand(orderId, "TRACK-1"));
```

`void` deciders map to `ICommandHandler<TCommand>` (a command that returns nothing) — e.g. a `StartOrderCommand`
whose `Decide` calls an `Open(...)` decision on the freshly-fetched aggregate.

## Domain vs integration events

The aggregate's **domain events** (its internal facts) are kept separate from **integration events** (the public
contract). A committed domain event always publishes on the bus (for projections/PMs); an integration event is
produced only if you ask, two ways:

```csharp
// (a) Translator — automatic at commit; the domain event never references the integration event.
public sealed record OrderDispatched(string OrderId, string Tracking, DateTimeOffset At) : IEvent;

[EventTranslator]
public static class OrderTranslators
{
    public static OrderDispatched Translate(OrderShipped e) => new(e.Id.Value, e.Tracking, DateTimeOffset.UtcNow);
}

// (b) Raise — explicit, staged in a decision, published at commit; no bus in the aggregate's constructor.
public void Close(string resolution)
{
    Emit(new TicketClosed(Id, resolution));
    if (resolution == "unresolved") Raise(new TicketEscalated(Id.Value, "closed unresolved"));
}
```

A domain event with no translator publishes as a domain event only. Suppress all integration notification for a
commit (replay, import, bulk seeding) with `await session.SaveChangesAsync(notifyIntegration: false)` — the domain
events still publish so projections stay current. Keeping the two separate means the outside world couples to the
stable integration contract, not the churny internal event shape.

## Reading directly (live)

For a live read without a stored projection, fold on demand:

```csharp
var session = provider.GetRequiredService<IAggregateSession>();
Order? order = await session.FetchLatestAsync<Order>(orderId);
```

## Projections & read models

A projection folds the *same* committed events into a queryable shape (typically an `ICache` entry a `ICacheable`
query reads back). Mark it `[Projection]` + `partial` and add `On(TEvent, CancellationToken)` methods:

```csharp
[MemoryPackable]
public sealed partial record OrderSummary(string OrderId, string Customer, decimal Total, string Status, int Lines);

[Projection]
public sealed partial class OrderSummaryProjection(ICache cache) : IProjection
{
    public static string Key(string orderId) => $"query:GetOrderSummaryQuery:{orderId}";

    public ValueTask On(OrderStarted e, CancellationToken ct)
        => cache.SetAsync(Key(e.Id.Value),
            new OrderSummary(e.Id.Value, e.Customer.Value, 0m, nameof(OrderStatus.Draft), 0), ct: ct);

    public ValueTask On(OrderPlaced e, CancellationToken ct) => /* update Total + Status */ default;
    // … On(ItemAdded), On(OrderShipped), … …
}

public sealed record GetOrderSummaryQuery(string OrderId) : IQuery<OrderSummary>, ICacheable;   // reads the cache entry
```

The generator subscribes a typed recipient per handled event to the `IEventBus`, so a committed event routes to
the right `On(...)` without reflection. In-house projections run **async** off the committed-event bus; the Marten
server additionally offers **inline** (read-your-writes) and **live**.

## Snapshots

`[Snapshot(every: N)]` bounds rehydration to O(tail): the session persists a snapshot every *N* events and
restores from it (then folds only the tail). Implement `ISnapshottable` to capture/restore the aggregate's folded
state (a small `[MemoryPackable]` record). Snapshots are purely an optimization — correctness never depends on
them, since they are a fold of the same events.

## Offline & sync

The default offline path (**Model A**) needs no ES-specific transport: an aggregate command is an `IQueueable`
CQRS command, so the existing outbox enqueues it when disconnected and replays it on reconnect, with
`IConflictResolver` and `ISyncFailureLog` handling conflicts and dead-letters. On top of that, ES adds:

- **`IEventInbox`** (`KeyValueEventInbox`) — dedups by `EventId` so a re-sent event appends exactly once.
- **`LocalEventIngestSink`** — the in-house authoritative sink (`IEventSyncClient`): appends pushed events under
  server version + `GlobalPosition` assignment, idempotent by `EventId`, returning `Applied` or `Rebased`.
- **`StreamReconciler.AdoptCanonicalAsync`** — the client rebase: matches local optimistic events to the server's
  canonical events by `EventId`, adopts their version + `GlobalPosition`, and re-bases any local-only tail after.

```csharp
services.AddInHouseEventIngest();   // IEventInbox + GlobalPositionSequence + LocalEventIngestSink + StreamReconciler
```

The event-shipping path (**Model B**) uses the same `IEventSyncClient` seam; `StreamRebaser` pushes the local tail
and reconciles — adopting the server's canonical events on a rebase, or dead-lettering a conflict into the shared
`ISyncFailureLog` under `Category = "es-conflict"` (the same ack/retry surface as the outbox and sagas). The Marten
server sink slots into the same seam.

## Server provider (Marten)

For the authoritative server, swap the provider — the aggregate, commands, projections and process managers are
unchanged:

```csharp
services.AddMarten(o => o.Projections.Add<OrderSummaryProjection>(ProjectionLifecycle.Inline))
        .IntegrateWithWolverine();          // committed events published via the transactional outbox
services.AddYFexEventSourcing().UseMarten();
```

Marten gives an atomic per-stream append (one Postgres transaction), a projection daemon, and snapshots; a thin
`DomainEventBridge` forwards Wolverine-published domain events onto YFex's `IEventBus` so PMs/projections react
identically to the in-house path. `YFex.EventSourcing.Marten` is opt-in and never required — the in-house store is
the reason the abstraction exists (client/offline/WASM/AoT, where Marten cannot run).

## See also

- [CQRS](cqrs.md) — the dispatcher, commands/queries, offline queueing that ES plugs into.
- [Orchestration](orchestration.md) — process managers/sagas that coordinate across event-sourced aggregates.
- [Event Sourcing Internals](../internal/event-sourcing-internals.md) — store keys, generator emission, session mechanics.
