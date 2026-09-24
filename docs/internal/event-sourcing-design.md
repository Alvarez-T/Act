# YFex.EventSourcing — Design & Plan

**Status:** Proposed (design/plan). Builds on the shipped `YFex.Cqrs` (commands/events, dispatcher, handler seam), `YFex.Messaging.Rpc` (`ICommandHandler<>`/`IHandlerInvoker`, offline outbox, `IConflictResolver`, `ISyncFailureLog`), `YFex.Persistence` (`IKeyValueStore`, `IDurableQueue<T>`), and mirrors the provider-seam pattern of `YFex.Resilience` (+`.Polly`) and `YFex.Orchestration` (+`.Rpc`/`.Wolverine`). Nothing here is built yet.

> Illustrative API — names are placeholders pending implementation.

---

## 0. Goals & non-negotiable principles

Add an **event-sourcing write model** to YFex: aggregates whose state is an append-only stream of domain events, foldable to rebuild, with a provider-neutral store (in-house for client/offline/WASM/AoT; Marten for the server). It integrates with the existing CQRS dispatcher, feeds projections (read models) and process managers, and syncs offline streams back to the server on reconnect.

**Principles (apply to every type):**

- **AoT-first.** `YFex.EventSourcing` sets `<IsAotCompatible>true>`. Zero runtime reflection on any path — **no `Expression.Compile`, `MakeGenericMethod`/`MakeGenericType`, `Activator`, `DynamicInvoke`.** The event fold, the event-type ↔ name map, serialization, and the aggregate command handlers are all **source-generated** typed code.
- **Source generators over reflection.** `[EventSourced]`, `[AggregateCommandHandler]`, and `[Projection]` drive an incremental generator (`ForAttributeWithMetadataName`), mirroring `YFex.Orchestration.Generator` / `YFex.Cqrs.SourceGenerator`.
- **Zero/low allocation.** Events are `union`s + `[MemoryPackable]`; envelopes and identities are `readonly record struct`s; stream reads use an ordinal prefix-scan (the `es:{stream}:{version:D19}` trick from the delay-scheduler); append buffers pool; the `When` fold does no boxing (`union.Switch`).
- **Own primitives.** Introduce framework/domain value types (`StreamId`, `AggregateId`, `EventId`, `StreamVersion`, `GlobalPosition`, and domain `Money`/`Sku`/`Quantity`/…) rather than loose `string`/`long`/`decimal` — the same convention as `SagaId`/`StepIndex`/`PropertyBitmap64`, and a place to hang syntax sugar.
- **`union` where it fits** (net11 native `union`): the event set, decision outcomes, sync results, and domain errors are exhaustive unions; closed payload-less sets stay enums.
- **Provider seam, minimal libraries.** One abstractions+in-house library, one generator, one server provider (Marten, which directly involves Wolverine). Do not pre-split transports.

**Locked framing:** the **aggregate is the domain model** (write model / consistency boundary). Read models (projections), queries/DTOs, and coordination (PMs/sagas) live *outside* it. Event sourcing is only *how the aggregate is persisted*.

---

## 1. Layering & projects

```
L1  YFex.EventSourcing            abstractions + the built-in in-house store (over IKeyValueStore) + offline sync client.
                                  AoT. refs YFex.Cqrs, YFex.Persistence, YFex.Messaging.Rpc, MemoryPack. NO Marten.
Gen YFex.EventSourcing.Generator  netstandard2.0 incremental generator: [EventSourced] fold/serialization,
                                  [AggregateCommandHandler] → CQRS ICommandHandler, [Projection] subscriptions.
Aux YFex.EventSourcing.Marten     server provider. Wraps Marten (IDocumentSession) + Wolverine directly:
                                  authoritative transactional store, projection engine, transactional outbox, sync sink.
```

**Dependency direction (the CQRS check):**

```
YFex.EventSourcing  ──►  YFex.Cqrs (ICommand/IEvent/IDispatcher), YFex.Persistence (IKeyValueStore/IDurableQueue),
                         YFex.Messaging.Rpc (ICommandHandler<> seam + outbox + IConflictResolver + ISyncFailureLog), MemoryPack
YFex.Cqrs           ──►  (UNCHANGED — knows nothing about event sourcing)
```

> **DECIDED — CQRS does not reference ES.** The handler interfaces (`ICommandHandler<TCommand,TResult>` etc.) already live in `YFex.Messaging.Rpc` and are resolved via `IHandlerInvoker` + the generated `LocalHandlerInvokerRegistry` (no reflection). An event-sourced aggregate command handler is therefore just an *ordinary* `ICommandHandler<>` — the existing `LocalDispatcher` routes to it unchanged, and it inherits validation, authorization, resilience, invalidation, **and the offline outbox** for free. ES plugs in at the handler layer; the dispatcher never learns a new concept.

Two providers behind one DI switch (mirrors `UseInHouseSagas()`/`UseWolverineSagas()`):

```csharp
services.AddYFexEventSourcing().UseInHouse();   // IKeyValueStore-backed — client/offline/WASM/AoT (default)
services.AddYFexEventSourcing().UseMarten();    // YFex.EventSourcing.Marten — server, transactional
```

---

## 2. AoT, source-generation & performance strategy

### 2.1 What the generator emits (no reflection)

`YFex.EventSourcing.Generator` discovers `[EventSourced]` aggregates, `[AggregateCommandHandler]` deciders, and `[Projection]` read models, and emits per type:

- **Fold dispatch** — the `When` fold is a plain C# `switch` over the native `union`'s case types (written by the user; no `.Switch`/`.Match` helper — see §5.6). The generator emits the **event-type ↔ short-name map** and the MemoryPack (de)serialization dispatch keyed by name (a typed `switch`, no `Type.GetType`).
- **Stream identity** — `StreamId.For<TAggregate>(id)` and the aggregate's default stream prefix (`order-{id}`).
- **Aggregate command handler** — an `ICommandHandler<TCommand,TResult>` that opens an `IAggregateSession`, `FetchForWriting`, invokes the decision, appends, commits — registered into the generated `LocalHandlerInvokerRegistry` so the CQRS dispatcher finds it.
- **Projection subscriptions** — typed `IEventRecipient<TEvent>` / handler dispatch, so a committed event routes to a projection's `On(...)` without reflection.
- **Snapshot IO** — `[Snapshot]` fold + serialize/deserialize of the folded state.

Runtime lookup (event-name → deserializer, aggregate-type → handler) is `FrozenDictionary`, built once at startup.

### 2.2 Performance primitives

- `readonly record struct` identities (`StreamId`, `AggregateId`, `EventId`, `StreamVersion(long)`, `GlobalPosition(long)`), `ISpanFormattable`, no implicit string conversion.
- Events are native `union` + `[MemoryPackable]`; the `When` fold is a plain C# `switch`/`is` on the case types — **zero boxing**, and a `switch` *expression* is compiler-checked for exhaustiveness.
- Stream keys `es:{stream}:{version:D19}` sort by version via ordinal prefix scan (same mechanism as `sched:{ticks:D19}`); batch reads into pooled buffers.
- **MemoryPack** for events, snapshots, and sync payloads (same as journal/outbox/saga store).
- Snapshots bound rehydration cost to O(tail) instead of O(stream).

### 2.3 Allocation budget (targets)

- Appending N events: N `[MemoryPackable]` serializations + one head-version write; no boxing of events.
- Rehydrating an aggregate: 1 snapshot read (if any) + a pooled span of tail events; fold is allocation-free per event.
- Dispatching an aggregate command: 0 allocations beyond the command instance, the loaded aggregate, and the awaited state machine.

---

## 3. Own primitives & value types

### 3.1 Framework identities

```csharp
public readonly record struct StreamId(string Value) : ISpanFormattable;      // "order-1042"
public readonly record struct AggregateId(string Value) : ISpanFormattable;
public readonly record struct EventId(Guid Value);                            // idempotency key on sync
public readonly record struct StreamVersion(long Value) : IComparable<StreamVersion>;  // per-stream, optimistic concurrency
public readonly record struct GlobalPosition(long Value);                     // server-assigned total order

// generated sugar per [EventSourced] type
StreamId s = StreamId.For<Order>("1042");     // → new StreamId("order-1042")
```

### 3.2 Event envelope

```csharp
[MemoryPackable]
public sealed partial record EventRecord(
    EventId Id, StreamVersion Version, string EventTypeName, byte[] Payload, DateTimeOffset At,
    GlobalPosition? GlobalPos = null);   // null until the authoritative server assigns it
```

### 3.3 Domain value objects (the "avoid raw primitives" convention + sugar)

```csharp
public enum Currency : byte { BRL, USD, EUR }

public readonly record struct Money(decimal Amount, Currency Currency) : IComparable<Money>
{
    public static Money Zero(Currency c) => new(0m, c);
    public static Money operator +(Money a, Money b) { EnsureSame(a, b); return new(a.Amount + b.Amount, a.Currency); }
    public static Money operator -(Money a, Money b) { EnsureSame(a, b); return new(a.Amount - b.Amount, a.Currency); }
    public static Money operator *(Money m, Quantity q) => new(m.Amount * q.Value, m.Currency);
    public bool IsPositive => Amount > 0m;
    public int CompareTo(Money o) { EnsureSame(this, o); return Amount.CompareTo(o.Amount); }
    private static void EnsureSame(Money a, Money b)
        { if (a.Currency != b.Currency) throw new DomainException(new DomainError.CurrencyMismatch(a.Currency, b.Currency)); }
}

public readonly record struct Sku(string Value) : ISpanFormattable;
public readonly record struct Quantity(int Value) { public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value); }
public readonly record struct CustomerId(string Value) : ISpanFormattable;
public readonly record struct LineItem(Sku Sku, Quantity Qty, Money UnitPrice) { public Money LineTotal => UnitPrice * Qty; }
```

### 3.4 Domain errors as a `union`

```csharp
public readonly union DomainError(
    DomainError.NotDraft, DomainError.EmptyOrder, DomainError.Overpaid, DomainError.NotPaidInFull,
    DomainError.AlreadyShipped, DomainError.CurrencyMismatch, DomainError.UnknownSku)
{
    public sealed record NotDraft(OrderStatus Actual);
    public sealed record EmptyOrder;
    public sealed record Overpaid(Money Attempted, Money Outstanding);
    public sealed record NotPaidInFull(Money Outstanding);
    public sealed record AlreadyShipped;
    public sealed record CurrencyMismatch(Currency A, Currency B);
    public sealed record UnknownSku(Sku Sku);
}
```

> **Open — error strategy:** throw a typed `DomainException(DomainError)` (shown here) vs. return a no-throw `union` outcome (`Accepted(events) | Rejected(DomainError)`, matching CQRS `Result`). Pick one framework-wide. Throwing composes with the CQRS `ResultConversionMiddleware`/dispatcher no-throw wrapping already in place; the union outcome is purer. **Recommended: throwing for invariant violations**, since the dispatcher already converts exceptions to `Result.Fail`.

---

## 4. The provider seam

Modeled on Marten's `FetchForWriting` — the load-decide-append unit with built-in optimistic concurrency.

```csharp
public interface IAggregateSession : IAsyncDisposable
{
    ValueTask<IEventStream<T>> FetchForWritingAsync<T>(AggregateId id, CancellationToken ct = default)
        where T : class, IEventSourced, new();
    ValueTask<T?> FetchLatestAsync<T>(AggregateId id, CancellationToken ct = default) where T : class, new();
    ValueTask SaveChangesAsync(CancellationToken ct = default);   // append (optimistic) + publish committed events
}

public interface IEventStream<out T>
{
    T Aggregate { get; }
    StreamVersion Version { get; }
    void Append(object @event);         // buffered; folded into Aggregate immediately
}

// lower-level store the in-house session builds on
public interface IEventStore
{
    ValueTask<StreamVersion> AppendAsync(StreamId stream, StreamVersion expected,
        IReadOnlyList<EventRecord> events, CancellationToken ct = default);   // optimistic on 'expected'
    IAsyncEnumerable<EventRecord> ReadAsync(StreamId stream, StreamVersion from = default, CancellationToken ct = default);
    ValueTask<StreamVersion> GetVersionAsync(StreamId stream, CancellationToken ct = default);
}
```

### 4.1 In-house store (over `IKeyValueStore`)

| Key | Purpose |
|---|---|
| `es:{stream}:{version:D19}` | One event; ordinal prefix scan == version order. |
| `es-head:{stream}` | Current `StreamVersion` for optimistic concurrency. |
| `es-snap:{stream}` | Latest snapshot blob (state + version), when `[Snapshot]` is on. |

> **⚠️ Honest limitation.** A pure `IKeyValueStore` has **no cross-key transaction**, so the in-house append is *optimistic* (read head → write events → CAS head), not atomic under multi-writer contention. This is correct for the single-writer client/offline case. The server provider (Marten) appends a whole stream in one Postgres transaction — the real reason the server provider exists.

---

## 5. The aggregate = the domain model

A full `Order`, split so the ES-plumbing and the business model are visible side by side.

### 5.1 The base class (ES plumbing)

```csharp
public abstract class EventSourcedAggregate<TEvent> : IEventSourced
{
    public AggregateId  Id      { get; protected set; }
    public StreamVersion Version { get; private set; }

    // decision path: fold + buffer + bump version. The generated handler pulls the buffer on SaveChanges.
    protected void Emit(TEvent e) { When(e); _uncommitted.Add(e); Version = new(Version.Value + 1); }

    // load path: fold history (no buffering) — repository / snapshot restore
    public void Replay(TEvent e) { When(e); Version = new(Version.Value + 1); }

    protected abstract void When(TEvent e);                 // the fold — pure, no side effects

    protected static void Ensure(bool ok, DomainError error) { if (!ok) throw new DomainException(error); }

    internal IReadOnlyList<TEvent> DequeueUncommitted();    // consumed by IAggregateSession.SaveChangesAsync
    private readonly List<TEvent> _uncommitted = new();
}
```

### 5.2 The events

```csharp
[MemoryPackable] public sealed partial record OrderStarted(AggregateId Id, CustomerId Customer, Currency Currency);
[MemoryPackable] public sealed partial record ItemAdded(Sku Sku, Quantity Qty, Money UnitPrice);
[MemoryPackable] public sealed partial record ItemRemoved(Sku Sku);
[MemoryPackable] public sealed partial record OrderPlaced(Money Total);
[MemoryPackable] public sealed partial record PaymentReceived(Money Amount);
[MemoryPackable] public sealed partial record OrderShipped(string Tracking);
[MemoryPackable] public sealed partial record OrderCancelled(string Reason);

public readonly union OrderEvent(
    OrderStarted, ItemAdded, ItemRemoved, OrderPlaced, PaymentReceived, OrderShipped, OrderCancelled);

public enum OrderStatus : byte { Draft, Placed, Shipped, Cancelled }
```

### 5.3 The aggregate

```csharp
[EventSourced]                    // generator: event-type↔name map, MemoryPack IO, StreamId.For<Order>, snapshot IO
[Snapshot(every: 100)]           // optional: fold a snapshot every 100 events
public sealed partial class Order : EventSourcedAggregate<OrderEvent>
{
    // ── STATE (folded from events; only When mutates it) ────────────────────────────────
    public CustomerId Customer { get; private set; }
    public Currency   Currency { get; private set; }
    public OrderStatus Status  { get; private set; } = OrderStatus.Draft;
    private readonly Dictionary<Sku, LineItem> _lines = new();
    public Money AmountPaid { get; private set; }

    // ── BUSINESS QUERIES (computed, not stored) ─────────────────────────────────────────
    public IReadOnlyCollection<LineItem> Lines => _lines.Values;
    public Money Total              => _lines.Values.Aggregate(Money.Zero(Currency), (a, l) => a + l.LineTotal);
    public Money OutstandingBalance => Total - AmountPaid;
    public bool  IsPaidInFull       => !OutstandingBalance.IsPositive;

    // ── FACTORY (opens the stream) ──────────────────────────────────────────────────────
    public static Order Start(AggregateId id, CustomerId customer, Currency currency)
    {
        var order = new Order();
        order.Emit(new OrderStarted(id, customer, currency));
        return order;
    }

    // ── DECISIONS (business rules → guard → Emit) ───────────────────────────────────────
    public void AddItem(Sku sku, Quantity qty, Money unitPrice)
    {
        Ensure(Status == OrderStatus.Draft, new DomainError.NotDraft(Status));
        Ensure(unitPrice.Currency == Currency, new DomainError.CurrencyMismatch(unitPrice.Currency, Currency));
        Emit(new ItemAdded(sku, qty, unitPrice));
    }

    public void RemoveItem(Sku sku)
    {
        Ensure(Status == OrderStatus.Draft, new DomainError.NotDraft(Status));
        Ensure(_lines.ContainsKey(sku), new DomainError.UnknownSku(sku));
        Emit(new ItemRemoved(sku));
    }

    public void Place()
    {
        Ensure(Status == OrderStatus.Draft, new DomainError.NotDraft(Status));
        Ensure(_lines.Count > 0, new DomainError.EmptyOrder());
        Emit(new OrderPlaced(Total));
    }

    public void ReceivePayment(Money amount)
    {
        Ensure(Status == OrderStatus.Placed, new DomainError.NotDraft(Status));
        Ensure(amount <= OutstandingBalance, new DomainError.Overpaid(amount, OutstandingBalance));
        Emit(new PaymentReceived(amount));
    }

    public OrderShipped Ship(string tracking)        // returns the event — handy for [AggregateCommandHandler]
    {
        Ensure(Status == OrderStatus.Placed, new DomainError.NotDraft(Status));
        Ensure(IsPaidInFull, new DomainError.NotPaidInFull(OutstandingBalance));
        var e = new OrderShipped(tracking);
        Emit(e);
        return e;
    }

    public void Cancel(string reason)
    {
        Ensure(Status != OrderStatus.Shipped, new DomainError.AlreadyShipped());
        Emit(new OrderCancelled(reason));
    }

    // ── THE FOLD (the only place setters run) ───────────────────────────────────────────
    // net11 native `union` → plain C# type-pattern switch. No .Switch()/.Match() helper: the
    // compiler already does case verification (a switch *expression* is checked for exhaustiveness).
    protected override void When(OrderEvent e)
    {
        switch (e)
        {
            case OrderStarted s:    Id = s.Id; Customer = s.Customer; Currency = s.Currency; break;
            case ItemAdded a:       _lines[a.Sku] = new LineItem(a.Sku, a.Qty, a.UnitPrice);  break;
            case ItemRemoved r:     _lines.Remove(r.Sku);                                      break;
            case OrderPlaced:       Status = OrderStatus.Placed;                               break;
            case PaymentReceived p: AmountPaid += p.Amount;                                    break;
            case OrderShipped:      Status = OrderStatus.Shipped;                              break;
            case OrderCancelled:    Status = OrderStatus.Cancelled;                            break;
        }
    }
}
```

Decision methods hold business rules and only *emit*; `When` is the pure fold and the *only* code that mutates state. Rebuilding from history and applying a new command run the same `When`, so state can never diverge.

### 5.4 Syntax-sugar catalog

| Sugar | What it does |
|---|---|
| `[EventSourced]` | Event-type ↔ name map + MemoryPack (de)serialization dispatch (AoT, no reflection), `StreamId.For<Order>`, snapshot IO. |
| `[StreamKey]` | Marks/customizes the id → stream name (default `order-{id}`). |
| `[Snapshot(every: N)]` | Snapshot fold + cadence; `FetchForWriting` restores from snapshot + tail. |
| `Emit(evt)` / `Ensure(cond, error)` | The decision vocabulary — append+fold, typed-error guards. |
| `Start(...)` convention | Recognized stream opener (first event). |
| native `switch`/`is` on the `union` | Exhaustive, boxing-free fold via C# type patterns; a `switch` expression is compiler-checked for completeness — **no `.Switch`/`.Match` helper for native unions** (see §5.6). |
| `Order.Rehydrate(history)` (generated) | Test helper: fold events into an aggregate for the arrange phase. |
| `[Emits(typeof(...))]` (optional) | Documents/validates which events a decision may produce (analyzer diagnostic otherwise). |
| Domain primitives with operators | `Total => Σ line.LineTotal`, `amount <= OutstandingBalance`, currency-safe arithmetic. |

### 5.5 What is *not* in the aggregate

- **Read models / DTOs** → projections (§7). The aggregate never returns a "view."
- **Cross-aggregate coordination** → PMs/sagas (§8). The aggregate never calls a service or another aggregate.
- **Infrastructure** → no `IDispatcher`/DB/HTTP inside; decisions are pure, external data is passed in as values.

### 5.6 Branching a union — `switch`/`is` vs. a `.Match` helper

The deciding factor is **whether the union exposes distinct case types**, *not* native-`union`-keyword vs. `[Union]`-attribute. `[Union]`/`UnionAttribute` is only a marker (there is no union generator); the net11 `union` keyword declares the case types for you. Two shapes:

| Shape | Example | How you branch |
|---|---|---|
| **Union with distinct case types** — native `union`, *or* a `[Union]`-attributed type authored over distinct records/classes | `union OrderEvent(...)`, `union CompensationResult(...)`, `union OkCancel(...)` | **Native C# `switch`/`is` type patterns** — `switch (e) { case OrderStarted s: … }`, `e is OrderPlaced`. The compiler verifies the cases (and checks exhaustiveness on a `switch` *expression*). **No helper method.** |
| **Single tag-struct** (a hand-written `[Union]` with a `byte _tag` + inline payload, *no* case types) | `CacheResult<T>`, `CacheValue<T>`, `QueueableResult` | Type patterns don't apply (there are no case *types*). Branch via the hand-written `.Match(...)`, the `Try*`/`Is*` members, or **property patterns** (`result is { IsHit: true }`). The `.Match` exists to give exhaustive, box-free branching for this layout — not because of the attribute. |

> **DECIDED — ES events are case-typed native `union`s, so `When` (and every consumer) uses a plain `switch`/`is`, never a `.Switch`/`.Match` method.** A `.Match` helper is only warranted for the single-tag-struct layout, which ES does not use for its event set. (The generator still emits a *name→deserializer* `switch` for wire (de)serialization — keyed by the string type name off the envelope, a separate concern from folding, since at that point the payload is still `byte[]`.)

---

## 6. `[EventSourced]` + `[AggregateCommandHandler]` ↔ the CQRS dispatcher

The command is a normal CQRS command (`IQueueable` ⇒ offline support for free); the decision is declared with the attribute:

```csharp
public sealed record ShipOrderCommand(AggregateId OrderId, string Tracking) : ICommand<OrderShipped>, IQueueable;

[AggregateCommandHandler]
public static class ShipOrder
{
    public static OrderShipped Decide(ShipOrderCommand cmd, Order order) => order.Ship(cmd.Tracking);
}
```

The generator emits an ordinary CQRS handler, registered into the generated `LocalHandlerInvokerRegistry` and dispatched by the existing `LocalDispatcher`:

```csharp
// generated into the consumer assembly
internal sealed class ShipOrderHandler(IAggregateSession session) : ICommandHandler<ShipOrderCommand, OrderShipped>
{
    public async Task<OrderShipped> HandleAsync(ShipOrderCommand cmd, CancellationToken ct)
    {
        var stream = await session.FetchForWritingAsync<Order>(cmd.OrderId, ct);
        var evt    = ShipOrder.Decide(cmd, stream.Aggregate);
        stream.Append(evt);
        await session.SaveChangesAsync(ct);      // optimistic append + publish committed events
        return evt;
    }
}
```

Full path: `dispatcher.CommandAsync(new ShipOrderCommand(...))` → validation → authorization → resilience pipeline → **generated handler** → aggregate append. The dispatcher is unchanged; ES is a handler flavor that inherits every CQRS cross-cutting concern, including the offline outbox.

> **Open — decision styles.** Two supported and interchangeable: (a) **instance behavior** (`order.Ship(tracking)`) — richest domain model; (b) **pure static decider** (`Decide(cmd, order) => event`) — thin/functional. The generated handler adapts to either (call the instance method then pull uncommitted events, or append the returned event).

---

## 7. Projections & read models

A projection folds the *same events* into a queryable shape and plugs into CQRS `ICacheable` — so the read side never touches the stream at query time.

```csharp
[MemoryPackable]
public sealed partial record OrderSummary(string OrderId, string Customer, decimal Total, string Status, int Lines);

[Projection]                                     // generator: subscribe to the handled event types
public sealed partial class OrderSummaryProjection : IProjection
{
    private readonly IClientCache _cache;         // or a projection store; ICacheable query reads from here
    public OrderSummaryProjection(IClientCache cache) => _cache = cache;

    private static string Key(string orderId) => $"query:GetOrderSummaryQuery:{orderId}";

    public async ValueTask On(OrderStarted e, CancellationToken ct)
        => await _cache.SetAsync(Key(e.Id.Value),
               new OrderSummary(e.Id.Value, e.Customer.Value, 0m, nameof(OrderStatus.Draft), 0), ct);

    public ValueTask On(ItemAdded e, CancellationToken ct)
        => UpdateAsync(e, s => s with { Lines = s.Lines + 1 }, ct);

    public ValueTask On(OrderPlaced e, CancellationToken ct)
        => UpdateAsync(e, s => s with { Total = e.Total.Amount, Status = nameof(OrderStatus.Placed) }, ct);

    public ValueTask On(OrderShipped e, CancellationToken ct)
        => UpdateByStreamAsync(s => s with { Status = nameof(OrderStatus.Shipped) }, ct);

    public ValueTask On(OrderCancelled e, CancellationToken ct)
        => UpdateByStreamAsync(s => s with { Status = nameof(OrderStatus.Cancelled) }, ct);

    // helpers omitted: resolve the summary key from the event's stream id, apply, write back
}
```

The matching CQRS query is a plain `ICacheable` served from the projection:

```csharp
public sealed partial record GetOrderSummaryQuery(string OrderId) : IQuery<OrderSummary>, ICacheable;
// b.Query<GetOrderSummaryQuery, OrderSummary>().Cacheable();  → read-through hits the projection's cache entry
```

### 7.1 Projection modes (inspired by Marten)

| Mode | Where it runs | Consistency |
|---|---|---|
| **Live** | folded on read (`FetchLatest`) | always current, no stored read model |
| **Inline** | in the same append transaction | read-your-writes, strong (Marten server) |
| **Async** | a background daemon subscribed to committed events | eventually consistent, scalable |

In-house: projections run **async** off the outbox → `IEventBus` (eventually consistent), or **live** by folding on demand. Marten: the projection engine offers all three; `[Projection]` maps onto `options.Projections.Add(...)` with the chosen `ProjectionLifecycle`.

---

## 8. Process managers & sagas over event-sourced aggregates

The PM/saga runtime is **unchanged** — it still `Correlate`s and `Handle`s `IEventBus` events (see [Orchestration](../public/orchestration.md)). ES simply becomes a new *source* of those events: committed domain events flow to the bus (in-house outbox, or Marten+Wolverine bridge, §9/§10). A PM coordinates *across* aggregates and never becomes one:

```csharp
[ProcessManager]
public sealed class FulfillmentProcess : ProcessManager<FulfillmentState>
{
    protected override void Correlate(ICorrelation<FulfillmentState> c)
    {
        c.StartsOn<OrderPlaced>(e => /* order id from stream */ ..., (s, e) => s.Total = e.Total);
        c.On<PaymentReceived>(e => ...);
        c.On<OrderShipped>(e => ...);
    }

    public async ValueTask Handle(OrderPlaced e) => await Send(new RequestPaymentCommand(...));
    public async ValueTask Handle(PaymentReceived e) { if (/* paid in full */) await Send(new ShipOrderCommand(...)); }
    public ValueTask Handle(OrderShipped e) { Complete(); return default; }
}
```

> **Note — correlation key from a domain event.** Domain events carry business ids inside the payload (or the envelope's `StreamId`). The PM's `Correlate` extractor reads whichever the event exposes; if a domain event doesn't carry the aggregate id, include it (e.g. `OrderPlaced(AggregateId Id, Money Total)`), since the correlation key must be derivable from the event alone.

PMs/sagas stay **snapshot-persisted** (via `ISagaStore`) — they're coordinators, not the source of truth. Event-sourcing a PM is possible but rarely worth it (open item).

---

## 9. Offline-first & sync

The write-model triangle (aggregate → projection → PM) must work offline and reconcile with the server on reconnect. Two models; the first reuses existing infrastructure and is the default.

### 9.1 Model A — command-replay (reuse the outbox) — *recommended*

The local in-house stream is an **optimistic cache**; the server (Marten) is the source of truth. Because the aggregate command is an `IQueueable` CQRS command, the existing `OutboxReplayer` + `IConflictResolver` machinery already carries it (see [Messaging Internals](messaging-internals.md)).

```
OFFLINE (client, in-house store):
  handle ShipOrderCommand locally:
     append event to local stream (StreamVersion = localHead+1, EventId = new)   → instant read model
     enqueue ShipOrderCommand to IOutbox (idempotency key = EventId)             → durable

RECONNECT (existing OutboxReplayer, oldest-first):
  replay ShipOrderCommand → server dispatcher → Marten FetchForWriting<Order> + append (authoritative version)
     success  → server publishes canonical OrderShipped (GlobalPos assigned) via Wolverine outbox
                client receives it → REBASE local stream: match by EventId, adopt the server version/position
     conflict (server version moved) → IConflictResolver<ShipOrderCommand>:
                Escalate  → ISyncFailureLog (Category = "es-conflict") — same DLQ surface as outbox + sagas
                RetryLater→ re-decide against the rebased aggregate, re-enqueue
                Discard   → drop the optimistic local event, rebase to server truth
```

Guards, both reusing existing patterns:
- **Server idempotency** = the inbox pattern from `YFex.Orchestration.Rpc` (`IInbox` dedup by `EventId`) — a re-sent command/event appends once.
- **Dead-lettering** = the shared `ISyncFailureLog` (`SyncFailure.Category` distinguishes `"es-conflict"` from `"outbox"`/`"saga-*"`/`"broker"`), so ES conflicts surface in the same ack/retry UI.

### 9.2 Model B — event-shipping (true ES sync) — *alternative*

Ship *events*, not commands. A dedicated `IDurableQueue<EventRecord>` drains to a server ingest sink that appends batches under server version assignment and returns canonical `GlobalPosition`s; the client rebases. More ES-native (offline can append many events, server rebases on conflict) but the one place that needs *new* infra beyond the outbox: a client event-queue, a server ingest endpoint, and a rebase step.

```csharp
public interface IEventSyncClient           // provider: in-house(outbox-command) | Marten/Wolverine(event ingest)
{
    ValueTask<SyncResult> PushAsync(StreamId stream, StreamVersion sinceServerVersion,
                                    IReadOnlyList<EventRecord> local, CancellationToken ct);
}
public readonly union SyncResult(SyncResult.Applied, SyncResult.Rebased, SyncResult.Conflicted)
{
    public sealed record Applied(GlobalPosition Through);
    public sealed record Rebased(IReadOnlyList<EventRecord> Canonical);   // client replaces local tail
    public sealed record Conflicted(DomainError Error);
}
```

> **DECIDED — don't pre-split the transport.** Following the orchestration/resilience precedent, keep the in-house store + the `IEventSyncClient` seam inside `YFex.EventSourcing`, ship the command-replay implementation there, and let `YFex.EventSourcing.Marten` provide the server sink. Add a transport package (`.Http`, `.Fusion`) only if a second transport actually appears.

### 9.3 How this compares to sibling libraries

| Concern | Messaging.Rpc | Orchestration(.Rpc) | EventSourcing |
|---|---|---|---|
| Offline write | outbox (`IQueueable` commands) | commands via CQRS | **reuses the outbox (Model A)** |
| Redelivery dedup | idempotency key | `IInbox` (broker) | `IInbox` by `EventId` |
| Conflict | `IConflictResolver` | — | `IConflictResolver` + stream rebase |
| Dead-letter | `ISyncFailureLog` | `IDeadLetterSink` → `ISyncFailureLog` | `ISyncFailureLog` (`Category="es-conflict"`) |
| Server push / resync | Fusion event stream | `IEventBus`/broker | canonical events → rebase |

---

## 10. Marten provider (server)

`YFex.EventSourcing.Marten` wraps Marten's `IDocumentSession` behind the same seam and involves Wolverine directly for the transactional outbox.

```csharp
sealed class MartenAggregateSession(IDocumentSession session) : IAggregateSession
{
    public async ValueTask<IEventStream<T>> FetchForWritingAsync<T>(AggregateId id, CancellationToken ct)
        where T : class, IEventSourced, new()
        => new MartenStream<T>(await session.Events.FetchForWriting<T>(id.Value, ct));   // aggregate + version

    public ValueTask<T?> FetchLatestAsync<T>(AggregateId id, CancellationToken ct)
        => new(session.Events.FetchLatest<T>(id.Value, ct).AsTask());                    // live/inline projection

    public ValueTask SaveChangesAsync(CancellationToken ct)
        => new(session.SaveChangesAsync(ct));   // atomic append; IntegrateWithWolverine() ⇒ transactional outbox publish
}
```

```csharp
services.AddMarten(o => o.Projections.Add<OrderSummaryProjection>(ProjectionLifecycle.Inline))
        .IntegrateWithWolverine();               // committed events published after commit
services.AddYFexEventSourcing().UseMarten();
```

A thin bridge forwards Wolverine-published domain events onto YFex's `IEventBus`, so PMs/projections react identically to the in-house path — **the only glue**, and the PM code stays untouched:

```csharp
public sealed class DomainEventBridge<TEvent>    // Wolverine cascading handler
{
    public ValueTask Handle(TEvent e, IEventBus bus, CancellationToken ct) => bus.PublishAsync(e, ct);
}
```

> **DECIDED — reuse, don't rebuild.** Marten gives transactional append, a projection daemon, and snapshots; Wolverine gives the transactional outbox and transports. Wrap them behind `IAggregateSession`/`IEventStore`; never reimplement them. The in-house store is the reason to keep the abstraction (client/offline/WASM/AoT, where Marten can't run) — Marten is opt-in, never required.

---

## 11. Snapshotting

`[Snapshot(every: N)]` generates a fold of the aggregate's state into a `[MemoryPackable]` snapshot plus a cadence. In-house: written to `es-snap:{stream}`; `FetchForWriting` reads the snapshot then `ReadAsync(from: snapshot.Version)`. Marten: maps onto Marten's inline/snapshot lifecycle. Purely an optimization — correctness never depends on snapshots (they're a fold of the same events).

---

## 12. `union` catalog

Payload-bearing exhaustive outcomes → `union`; closed payload-less sets → `enum`.

- **Domain events:** `OrderEvent(OrderStarted, ItemAdded, …)` — one per aggregate.
- **Domain errors:** `DomainError(NotDraft, EmptyOrder, Overpaid, …)`.
- **Sync outcome:** `SyncResult(Applied, Rebased, Conflicted)`.
- **Decision outcome (if no-throw chosen):** `Decision(Accepted(events), Rejected(DomainError))`.
- **Enums (no payload):** `OrderStatus`, `Currency`, `ProjectionLifecycle { Live, Inline, Async }`.

---

## 13. AoT verification plan

`YFex.EventSourcing.AotSmoke` sets `<IsAotCompatible>true>` + `<TreatWarningsAsErrors>true>` and exercises: an `[EventSourced]` aggregate (start → decisions → fold), an `[AggregateCommandHandler]` routed through a real `LocalDispatcher`, an in-house `IEventStore` append/read/rehydrate over `MemoryKeyValueStore`, a `[Projection]` fold, and a `[Snapshot]` restore — asserting **0 `IL2xxx`/`IL3xxx`**. `YFex.EventSourcing.Marten` is server-only and *not* AoT (isolated behind the seam, like `.Rpc.Wolverine`).

---

## 14. Decisions & open items

1. **DECIDED:** three projects — `YFex.EventSourcing` (abstractions + in-house + sync client), `YFex.EventSourcing.Generator`, `YFex.EventSourcing.Marten` (Marten + Wolverine). No separate `.InHouse`/`.Marten.Wolverine`.
2. **DECIDED:** CQRS does not reference ES; ES aggregate handlers are ordinary `ICommandHandler<>`s discovered by the existing generated registry — the dispatcher is unchanged and ES inherits validation/authz/resilience/invalidation/outbox.
3. **DECIDED:** provider seam `IAggregateSession`/`IEventStore` (Marten `FetchForWriting`-inspired); in-house over `IKeyValueStore` is the AoT/offline default, Marten opt-in server.
4. **DECIDED:** offline sync reuses the outbox (Model A) by default; the `IEventSyncClient` event-shipping path (Model B) is behind a seam, not a separate library, until a second transport appears.
5. **DECIDED:** dead-letters/conflicts land in the shared `ISyncFailureLog` (`Category = "es-conflict"`); redelivery dedup reuses `IInbox` by `EventId`.
6. **Open:** error strategy — throwing `DomainException(DomainError)` vs. a no-throw `Decision` union (recommended: throw, to compose with the dispatcher's exception→Result wrapping).
7. **Open:** decision authoring — instance methods (`order.Ship`) vs. static deciders (`Decide(cmd, order)`); support both, pick a house default.
8. **Open:** should a PM/saga ever be event-sourced (audit of coordination decisions), or always snapshot-persisted (recommended snapshot).
9. **Open:** in-house multi-writer atomicity — the KV-store append is optimistic-only; a store-level CAS/lease (or steering multi-writer scenarios to the Marten server) is the mitigation.

---

## 15. Phasing

- **P0 — `YFex.EventSourcing` core + generator.** Own primitives, `EventSourcedAggregate<TEvent>`, in-house `IEventStore`/`IAggregateSession` over `IKeyValueStore`, `[EventSourced]` (fold/serialization maps) + `[AggregateCommandHandler]` (→ CQRS `ICommandHandler`), snapshots. AoT smoke.
- **P1 — Projections + CQRS read integration.** `[Projection]` (live/async), read models feeding `ICacheable` queries; the `OrderSummary` example end-to-end.
- **P2 — Offline sync (Model A).** Command-replay over the existing outbox, `EventId` idempotency via `IInbox`, conflict via `IConflictResolver`, dead-letter into `ISyncFailureLog`, client rebase on server push.
- **P3 — `YFex.EventSourcing.Marten`.** Marten `FetchForWriting`/`FetchLatest`, projection lifecycles, Wolverine transactional outbox + `DomainEventBridge` to `IEventBus`; optional Model B event-ingest sink.
