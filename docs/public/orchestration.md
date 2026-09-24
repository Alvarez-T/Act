# YFex.Orchestration

**Libraries:** `YFex.Orchestration`, `YFex.Orchestration.Generator`, `YFex.Orchestration.Rpc`, `YFex.Orchestration.Rpc.Wolverine`

---

## 1. Overview

`YFex.Orchestration` coordinates **multi-step, correlated transactions** on top of CQRS. It gives you two shapes:

- **Sagas** — a fixed, ordered sequence of reversible command steps that either all succeed or roll back in reverse order (the classic distributed-transaction pattern).
- **Process managers (PMs)** — a persisted, correlated state machine that reacts to many independent events over time and emits commands (long-running, event-driven workflows).

Both run on the same runtime: the same durable state store, the same reverse-order compensation, the same timeout scheduler, and the same source generator. The in-house engine is the **client / offline / WASM / AOT** path (where a server broker like Wolverine can't run); a Wolverine provider wraps the same abstractions server-side.

Like the rest of YFex, discovery and wiring are **source-generated** — no runtime reflection. Because orchestration executes in-process and never *inspects* its lambdas, it uses plain `Func<>` delegates (never `Expression<>`), which the C# compiler compiles directly — AOT-clean by construction.

| Package | Role |
|---|---|
| `YFex.Orchestration` | Saga + PM runtime, builders, `ISagaStore`, step ledger, `StepBitmap64`, durable delay-scheduler, compensation, DI. |
| `YFex.Orchestration.Generator` | Incremental generator: saga step-table registration, PM event subscriptions + `Handle` dispatch, group expansion. |
| `YFex.Orchestration.Rpc` | Cross-service: `IMessageBroker` seam (+ack/nack), inbox dedup, DLQ policy over `ISyncFailureLog`. |
| `YFex.Orchestration.Rpc.Wolverine` | Server-only Wolverine provider (broker over Wolverine transports). Not AOT. |

Builds on the shipped [reversible-command](cqrs.md) support (`YFex.Cqrs.Compensation`) and the [Resilience](resilience.md) policy vocabulary.

---

## 2. Core Concepts & Mental Model

### Saga vs process manager

| | Linear saga | Process manager |
|---|---|---|
| Flow | fixed ordered sequence | arbitrary, event-driven |
| Trigger | the orchestrator advances steps | each inbound event |
| Best for | an atomic multi-step transaction | a long-running workflow with external async |
| Compensation | automatic reverse-order ledger | explicit, or `CompensateAll()` |

A linear saga is really a process manager whose flow happens to be a fixed sequence. Reach for a saga when you have "do A, then B, then C, and undo them if any fails"; reach for a PM when you have "when X happens do this, later when Y happens do that."

### Correlation

Every instance is identified by a **correlation key** — a business identifier (order id, sale id) that ties inbound events and timeouts back to the right persisted instance. Sagas derive it from the value you pass to `RunAsync`; PMs extract it from each event via `Correlate`.

### Compensation & the step ledger

A saga records each committed step in a **step ledger** plus a `StepBitmap64` (a `ulong` bitmask, one bit per step). On failure it walks the committed bits **high→low** (reverse order) and reverses each step. The reverse of a step is itself a command, resolved by the R3 precedence: **step `.CompensateWith` > standalone reverse registry > the command's own `.Reversible`**.

### Own value types

`SagaId`, `CorrelationKey`, `StepIndex` are pointer-sized `readonly record struct`s (no loose strings); `StepBitmap64` is the zero-allocation committed-set. Outcomes are net11 `union`s: `StepOutcome`, `CompensationResult`, `WaitOutcome`.

---

## 3. Integration Model & Lifecycle

### DI registration

```csharp
services.AddYFexOrchestration().UseInHouseSagas();  // in-house provider (client + server, AOT)
services.AddGeneratedSagas();                        // generated: registers every [Saga] step table
services.AddGeneratedProcessManagers();              // generated: registers every [ProcessManager] module + runtime
```

Prerequisites you register elsewhere: `IDispatcher` (YFex.Cqrs) and `IKeyValueStore` (YFex.Persistence). Optional: `IResiliencePipelineFactory` (for step resilience), `IReverseRegistry` (for registry-declared reverses), an `IEventBus` (for PMs), and — via `YFex.Orchestration.Rpc` — an `IDeadLetterSink`.

### What runs in the background

- `SchedulerHostedService` — an adaptive claim-due loop that fires durable timeouts (sleeps until the next due entry; not a busy poll).
- `ProcessManagerHost` — subscribes each PM's events on the `IEventBus` at startup (registered only when at least one PM exists).

---

## 4. Step-by-Step Usage

### A linear saga

```csharp
[Saga]
public sealed partial class CheckoutSaga : Saga<CheckoutState>
{
    protected override void Configure(ISagaBuilder<CheckoutState> saga)
    {
        saga.Step<ReserveStockCommand>(s => new(s.SaleId, s.Items))
            .CompensateWith<ReleaseStockCommand>((c, _) => new(c.SaleId));

        saga.Step<ChargePaymentCommand>(s => new(s.SaleId, s.Total))
            .Timeout(TimeSpan.FromSeconds(8))     // step wins per-knob over the command's spec (R2)
            .Retry(1)
            .CompensateWith<RefundPaymentCommand>((c, _) => new(c.SaleId, c.Total));

        saga.Step<ArrangeShipmentCommand>(s => new(s.SaleId));
    }
}

// run it
var orchestrator = provider.GetRequiredService<ISagaOrchestrator>();
var definition   = provider.GetRequiredService<SagaDefinition<CheckoutState>>();
var result = await orchestrator.RunAsync(definition, new CheckoutState { SaleId = "S1", Total = 42m }, new CorrelationKey("S1"));

if (result.Status == SagaStatus.Cancelled)      // a step failed; committed steps were reversed
    // result.Compensation is CompensationResult.AllReversed | PartiallyFailed
```

The saga marked `[Saga]` must be `partial` and derive `Saga<TState>`; the generator emits the frozen step table.

### A process manager

```csharp
[ProcessManager]
public sealed class OrderProcess : ProcessManager<OrderState>
{
    protected override void Correlate(ICorrelation<OrderState> c)
    {
        c.StartsOn<OrderPlaced>(e => e.OrderId, (s, e) => { s.OrderId = e.OrderId; s.Total = e.Total; });
        c.On<PaymentConfirmed>(e => e.OrderId);
        c.On<ShipmentDispatched>(e => e.OrderId);
        c.OnGroup<PaymentFailureEvents>(e => e.OrderId);   // event group (R4) — members expanded at compile time
    }

    public ValueTask Handle(OrderPlaced e)
    {
        State.Status = OrderStatus.AwaitingPayment;
        RequestTimeout("payment", TimeSpan.FromHours(24), new PaymentTimedOut(State.OrderId));  // durable deadline
        return default;
    }

    public async ValueTask Handle(PaymentConfirmed e)
    {
        State.Status = OrderStatus.AwaitingShipment;
        CancelTimeout("payment");                 // "event arrived first" — cancel the deadline
        await Send(new ShipOrderCommand(State.OrderId));
    }

    public ValueTask Handle(ShipmentDispatched e) { Complete(); return default; }   // terminal
    public async ValueTask Handle(PaymentTimedOut e) { await Send(new CancelOrderCommand(State.OrderId)); Complete(); }
}
```

`Handle` methods are plain `async ValueTask` (or `ValueTask`) methods — one per reacted-to event. The generator wires the typed subscription and dispatch. `State`, `Send`, `RequestTimeout`, `CancelTimeout`, `Complete`, and `CompensateAll` are only valid *inside* a `Handle` (the runtime binds a context per event).

---

## 5. Deep Dive: Core API

### `ISagaBuilder<TState>` — declaring steps (R1)

Two ways to identify a step:

```csharp
saga.Step<ReserveStockCommand>(s => new(s.SaleId));           // id = nameof(TCommand)
saga.Step("reserve-stock").Invoke<ReserveStockCommand>(...);  // explicit label — required if the same command type is used twice
```

### Step verbs (R2) — same verbs as the command builder

No `Override*` methods; a knob set on the step wins, an unset knob inherits from the command's spec, then framework defaults. Effective spec = `command.Resilience.Overlay(step.Resilience)`.

| Verb | Effect |
|---|---|
| `.Timeout(TimeSpan)` | Per-attempt timeout. |
| `.Retry(int)` / `.Retry(RetryPolicy)` | Retry count (exponential + jitter default) or an explicit policy. |
| `.UseResource(string)` | Reference a named resilience resource (breaker/limiter/bulkhead). |
| `.Fallback(FallbackPolicy)` | Fallback handle. |
| `.OnFailure(StepFailurePolicy)` | `CompensateAll` (default), `Escalate`, or `Continue`. |
| `.CompensateWith<TReverse>((cmd, result) => new(...))` | Step-level reverse (most specific in R3). |

### Reverse declaration (R3) — three places, most specific wins

```csharp
// (a) on the command (YFex.Cqrs)
b.Command<ChargePaymentCommand>().Reversible<RefundPaymentCommand>(c => new(c.SaleId, c.Total));
// (b) in a standalone registry (YFex.Cqrs.Compensation)
services.AddYFexReversals(r => r.Reverse<ArrangeShipmentCommand, CancelShipmentCommand>(c => new(c.SaleId)));
// (c) on the step (most specific)
saga.Step<ChargePaymentCommand>(...).CompensateWith<RefundToStoreCreditCommand>((c, r) => new(c.SaleId, c.Total));
```

The saga's step ledger stores each committed step's **effective** reverse, so a step- or registry-declared reverse works even when the command has no CQRS reverse policy.

### `ICorrelation<TState>` — process-manager correlation (R4)

| Method | Meaning |
|---|---|
| `StartsOn<TEvent>(key, apply)` | This event may create a new instance and seed its state. |
| `On<TEvent>(key)` | Route this event to an existing instance by correlation key. |
| `OnGroup<TGroup>(key)` | Correlate a whole **event group** (an `IEventGroup` interface); members are expanded at compile time. |

### Durable timeouts

`RequestTimeout(name, delay, message)` schedules a durable, cancellable one-shot; `CancelTimeout(name)` removes it; when it fires, the message is deserialized (typed, no reflection) and delivered to the matching `Handle` like any other event. Backed by a lightweight scheduler on `IKeyValueStore` (works in Blazor WASM over IndexedDb and on the server over SQLite/FS).

### `ISagaOrchestrator` / results

```csharp
ValueTask<SagaRunResult> RunAsync<TState>(SagaDefinition<TState> definition, TState state, CorrelationKey correlation, CancellationToken ct);
public readonly record struct SagaRunResult(SagaStatus Status, CompensationResult? Compensation, Error Error);
```

`SagaStatus`: `Running · Parked · Compensating · Completed · Cancelled · DeadLettered`.

### State store & concurrency

`ISagaStore` persists a MemoryPack snapshot (state + `StepBitmap64` + version + status) under the `saga:` prefix, per transition, with an **optimistic version stamp** — a stale write is rejected with `SagaConcurrencyException`. PMs additionally use per-correlation single-writer locking so two events for the same instance never race.

---

## 6. Common Patterns & Recipes

### Wait for one of several events

Use a PM group or multiple `On<>` handlers:

```csharp
c.On<NfeAuthorized>(e => e.SaleId);
c.On<NfeRejected>(e => e.SaleId);
// or a group:
c.OnGroup<FiscalRejectionEvents>(e => e.SaleId);
public ValueTask Handle(NfeAuthorized e) { /* continue */ ... }
public async ValueTask Handle(FiscalRejectionEvents e) { await CompensateAll(); Complete(); }
```

### Dead-letter a poison step

```csharp
saga.Step<ChargePaymentCommand>(...).OnFailure(StepFailurePolicy.Escalate);   // stops + dead-letters, no compensation
```

Register a sink to route dead-letters into the shared failure log:

```csharp
services.AddYFexOrchestrationDeadLettering();   // YFex.Orchestration.Rpc → writes to ISyncFailureLog
```

### Cross-service messaging with ack/nack (YFex.Orchestration.Rpc)

```csharp
services.UseInMemoryBroker();          // dev/test/offline double (server: UseWolverineBroker())
services.AddYFexBrokerConsumer(maxAttempts: 5);

var consumer = provider.GetRequiredService<BrokerConsumer>();
await using var _ = consumer.Consume("orders", async (envelope, ct) => { /* handle */ });
```

The consumer dedups via the inbox, acks on success, requeues transient failures, and dead-letters a poison message after `maxAttempts` (routing it to the `IDeadLetterSink`).

---

## 7. Testing & Mocking

The in-house engine needs only an `IDispatcher` and an `IKeyValueStore` (both have in-memory doubles), so sagas and PMs test without a host:

```csharp
var orchestrator = new SagaOrchestrator(dispatcher, new KeyValueSagaStore(new MemoryKeyValueStore()));
var result = await orchestrator.RunAsync(new CheckoutSaga().BuildDefinition(), state, new CorrelationKey("S1"));
```

For PMs, resolve the generated module and drive events directly:

```csharp
var runtime = provider.GetRequiredService<ProcessManagerRuntime>();
var module  = provider.GetServices<IProcessManagerModule>().Single();
await runtime.DispatchEventAsync(module, new OrderPlaced("O1", 42m));
```

Fire a timeout deterministically by building a `ScheduleEntry` and calling `runtime.DeliverAsync(entry, ct)`.

---

## 8. Troubleshooting & Gotchas

- **`[Saga]`/`[ProcessManager]` not discovered.** The class must carry the attribute *and* derive the base class *and* (for sagas) be `partial`. The attribute is the generator's discovery key; the base class supplies the runtime API and `TState`. Missing base → `YFORCH002`/`YFORCH010`; non-partial saga → `YFORCH001`; a PM with no `Handle` → `YFORCH012`.
- **"ProcessManager is not bound to a runtime context."** You touched `State`/`Send`/timeouts outside a `Handle`. Those are only valid while the runtime is executing a handler.
- **More than 64 saga steps throws at `BuildDefinition`.** `StepBitmap64` caps a linear saga at 64 steps (the pooled fallback is deferred). Split the flow or use a PM.
- **Late/duplicate events do nothing.** A completed instance is tombstoned (status `Completed`); events arriving after completion are dropped by design.
- **Timeouts never fire.** Ensure the app runs a host so `SchedulerHostedService` is started, and that an `IKeyValueStore` is registered.
- **A step reverse doesn't run.** Check R3 resolution — the step ledger stores the *effective* reverse; if none of step/registry/command declared one, there's nothing to reverse.

---

## 9. Reference Summary

### DI entry points

| Call | Package | Effect |
|---|---|---|
| `AddYFexOrchestration().UseInHouseSagas()` | `YFex.Orchestration` | In-house store + scheduler + orchestrator + scheduler host. |
| `AddGeneratedSagas()` | generated | Registers every `[Saga]` step table. |
| `AddGeneratedProcessManagers()` | generated | Registers every `[ProcessManager]` module + the PM runtime/host. |
| `AddYFexOrchestrationDeadLettering()` | `.Rpc` | Routes dead-letters to `ISyncFailureLog`. |
| `AddYFexBrokerConsumer(maxAttempts)` / `UseInMemoryBroker()` | `.Rpc` | Broker consumer stack + in-process broker. |
| `UseWolverineBroker()` / `UseWolverineSagas()` | `.Rpc.Wolverine` | Broker over Wolverine transports (server only). |

### Enums

- `SagaStatus { Running, Parked, Compensating, Completed, Cancelled, DeadLettered }`
- `OnFailure { Continue, Retry, CompensateAll, Escalate, DeadLetter }`
- `StepFailurePolicy { CompensateAll, Escalate, Continue }`
- `NackDisposition { Requeue, DeadLetter }` · `DlqDecision { Retry, DeadLetter }`

### Unions

`StepOutcome(Committed, Waiting, Failed, Skipped)` · `CompensationResult(AllReversed, PartiallyFailed)` · `WaitOutcome(EventReceived, TimedOut, Cancelled)`

### Generator diagnostics

`YFORCH001` saga not partial · `YFORCH002` saga missing base · `YFORCH010` PM missing base · `YFORCH011` PM needs a public parameterless ctor · `YFORCH012` PM has no `Handle` methods.

For the runtime internals (compensation walk, PM dispatch, scheduler keys, generator emission), see [Orchestration Internals](../internal/orchestration-internals.md). For the original design rationale, see [Orchestration + Resilience Design](../internal/orchestration-resilience-design.md).
