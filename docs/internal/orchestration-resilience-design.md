# YFex.Resilience + YFex.Orchestration — Design & Plan

**Status:** Proposed (design/plan). Builds on the shipped `YFex.Cqrs.Compensation` (command journal + `ReverseAsync`), reversible-command declaration in `YFex.Cqrs`, and the wired conflict resolvers in `YFex.Messaging.Rpc`. Nothing here is built yet.

> Illustrative API — names are placeholders pending implementation.

---

## 0. Goals & non-negotiable principles

Two cooperating library families:

1. **`YFex.Resilience` (+ `.Polly`)** — one declarative policy vocabulary (retry, timeout, circuit breaker, rate limiter, bulkhead, fallback, hedging) usable from CQRS commands, saga steps, and message handlers. Polly is an internal execution detail behind a seam.
2. **`YFex.Orchestration` (+ `.Rpc`)** — sagas **and** process managers: correlated multi-step transactions with reverse-order compensation, async event waits, durable timeouts, dead-lettering, and (in `.Rpc`) a broker seam with ack/nack.

**Principles (apply to every type in both families):**

- **AoT-first.** Every library sets `<IsAotCompatible>true>`. Zero runtime reflection on any hot path; **no `Expression.Compile`, no `MakeGenericMethod`/`MakeGenericType`, no `Activator`, no `DynamicInvoke`.** (This is *why* orchestration uses plain `Func<>` delegates, never `Expression<>` — see §2.1.)
- **Source generators over reflection.** All discovery/wiring (saga steps, event subscriptions, correlation, reverse dispatch, timeout dispatch) is emitted at compile time as typed code. Incremental generators (`ForAttributeWithMetadataName`), mirroring `YFex.Cqrs.SourceGenerator` / `YFex.Messaging.Rpc.Generator`.
- **Zero/low allocation.** `readonly record struct` value objects; `FrozenDictionary` frozen lookups; `PropertyBitmap64`-style bitmask primitives; struct enumerators; `ArrayPool`/`Span<T>` on rollback and routing paths; pooled scopes. Mirrors the `YFex.State` engine ethos.
- **Own primitives.** Introduce framework-specific value types (`SagaId`, `CorrelationKey`, `StepIndex`, `StepBitmap64`, `ResilienceSpec`, `ReverseSpec`) rather than reusing loose strings/tuples — like `YFex.State`'s `PropertyBitmap64`.
- **`union` where it fits** (net11 native `union`): payload-bearing exhaustive outcomes; closed payload-less sets stay enums. Catalog in §13.

**Locked review requirements:** **R1** dual step identity (string label or command type). **R2** step verbs match the command builder; step wins per-knob, unset inherits (no `Override*` methods). **R3** reverse declarable on the command, a standalone registry, or the step — most-specific wins. **R4** `OnEvent`/`WaitFor` accept multiple event types **and** event groups.

---

## 1. Layering & projects

```
L1  YFex.Resilience              abstractions: policy structs, ResilienceSpec (overlay), IResiliencePipeline[Factory], resource registry. AoT. NO Polly.
L2  YFex.Cqrs                    refs Resilience; command builder gains policy verbs → ResilienceSpec on CommandPolicy. Owns ReverseSpec (§14).
L3  YFex.Cqrs.Compensation       (existing) + IReverseRegistry + ReverseExecutor overload taking an explicit ReverseSpec (§14).
L4  YFex.Messaging.Rpc           dispatcher wraps invoke in the resilience pipeline; outbox replay uses it.
L5  YFex.Orchestration           saga + process-manager runtime, step/handler builders, state store, step ledger, delay-scheduler. AoT.
L6  YFex.Orchestration.Rpc       DLQ integration, IMessageBroker seam (+ack/nack), inbox, Wolverine provider.
Gen YFex.Orchestration.Generator netstandard2.0 incremental generator: saga/PM registration, typed event subscriptions, correlation, reverse & timeout dispatch.
Aux YFex.Resilience.Polly        implements IResiliencePipelineFactory with Polly v8. Polly never leaks past here.
```

Dependencies point **down only**; `YFex.Cqrs`/`YFex.Cqrs.Compensation` never reference Orchestration.

---

## 2. AoT, source-generation & performance strategy

### 2.1 Why `Func<>` not `Expression<>` (the AoT dividing line)
`YFex.Cqrs` uses `Expression<Func<…>>` for `Invalidates`/`Optimistic`/`Reverse` because those are *inspected/transported*, and compiling them needs `Expression.Compile` → `IL3050` (RequiresDynamicCode) — that is exactly why enabling `<IsAotCompatible>` on `YFex.Cqrs` is not free. **Orchestration executes in-process and never inspects its lambdas**, so it uses plain **`Func<>` delegates** (command factories `s => new(...)`, correlation `(s,e) => ...`, `apply`, step-level reverse maps). Plain delegates are compiled by the C# compiler — **no `Expression.Compile`, AoT-clean**. This is a deliberate, load-bearing decision.

### 2.2 What the source generator emits (no reflection)
`YFex.Orchestration.Generator` discovers `[Saga]`/`[ProcessManager]` partial classes (`ForAttributeWithMetadataName`) and each saga's `Configure`/handler methods, and emits a **frozen registration** per saga:

- **Step table** — ordered `StepDefinition[]` with typed command factories and effective `ResilienceSpec`/`ReverseSpec` references (all typed; no `MakeGenericType`).
- **Event routing map** — for every `On<TEvent>` / `WaitForAny<…>` / `OnEventGroup<TGroup>`: a typed `IEventBus` subscription (`IEventRecipient<TEvent>`), so an inbound event dispatches through a generated `switch`/`FrozenDictionary<Type, handler>` — **no reflective subscription**. **Group expansion happens at compile time** by resolving `IEventGroup` members (same mechanism as CQRS `FindImplementers`/`EventGroupRegistrationMetadata`).
- **Correlation accessors** — typed `Func<TState,TEvent,bool>` / key extractors, captured as static delegates.
- **Reverse dispatch** — typed `(IDispatcher, object, ct) => dispatcher.CommandAsync((TReverse)cmd, ct)` per reverse command (same technique the shipped reverse feature uses — reflection-free).
- **Timeout dispatch** — typed delivery of the scheduled timeout message to the saga (no reflective payload path — the flaw that ruled out reusing `YFex.Jobs`).

Runtime routing/lookup is `FrozenDictionary` built once at startup from the generated tables.

### 2.3 Performance primitives (new `readonly` value types)
- `readonly record struct SagaId(string Value)`, `CorrelationKey`, `StepIndex(byte Value)` — pointer-sized identities, `ISpanFormattable`, no implicit string conversion (D4 convention).
- **`StepBitmap64`** — a `readonly struct` over a `ulong` (mirrors `YFex.State.PropertyBitmap64`): tracks which of ≤64 steps have committed. Compensation = iterate **set bits high→low** (reverse order) with zero allocation; "anything to compensate?" is one `!= 0` test. Sagas >64 steps fall back to a pooled `StepIndex[]` (rare).
- **`ResilienceSpec`**, **`ReverseSpec`** — `readonly record struct`s (§3.2, §14), overlay-able, no heap alloc.
- **Frozen tables** — saga registry, event→saga routing, named resources, step lookup: all `FrozenDictionary`.
- **Struct enumerators** for step iteration; **`ArrayPool<T>`/`Span<T>`** when building a compensation plan; event routing avoids boxing (typed `IEventRecipient<T>`).
- **MemoryPack** for saga state + step ledger + scheduled-timeout payloads (same as journal/outbox).
- **ref-struct scopes** are used only where execution is synchronous (e.g. building a compensation plan, recording a step transition). Saga *execution* is async, so ref-struct scopes cannot span awaits — we do **not** force them there (honest limit vs. `YFex.State`'s sync `BatchScope`).

### 2.4 Allocation budget (targets)
- Dispatching a saga step: 0 allocations beyond the command instance and the awaited state machine.
- Routing an inbound event to a parked saga: 1 `FrozenDictionary` lookup + typed correlation call; 0 boxing.
- Compensation of an N-step saga: 1 pooled buffer (or none, via `StepBitmap64`); N reverse dispatches.
- Scheduler tick: batch-claim due entries into a pooled span; 0 per-entry alloc beyond deserialized payloads.

---

## 3. `YFex.Resilience` (abstractions — no Polly)

### 3.1 Policy taxonomy (two scopes)
| Policy | Scope | Declared |
|---|---|---|
| **Retry** (+backoff, jitter), **Timeout**, **Fallback**, **Hedging** | per-operation | inline verb on command/step |
| **Circuit breaker**, **Rate limiter**, **Bulkhead** (concurrency) | per-**resource** (shared state) | named resource, referenced via `.UseResource("name")` |

Stateful policies are per **named resource** (one `"sefaz"` breaker shared by all callers), never per operation instance.

### 3.2 Records + overlay (R2), all value types
```csharp
public enum BackoffStrategy : byte { Constant, Linear, Exponential }

public readonly record struct RetryPolicy(int MaxAttempts, BackoffStrategy Backoff, TimeSpan InitialDelay, TimeSpan MaxDelay, bool Jitter);
public readonly record struct TimeoutPolicy(TimeSpan Timeout);
public readonly record struct HedgingPolicy(int MaxAttempts, TimeSpan Delay);
public readonly record struct FallbackPolicy(int Token);                  // handle into a typed fallback table (no boxing)
public readonly record struct CircuitBreakerPolicy(double FailureRatio, TimeSpan SamplingDuration, int MinThroughput, TimeSpan BreakDuration);
public readonly record struct RateLimiterPolicy(int Permits, TimeSpan Window, int QueueLimit);
public readonly record struct BulkheadPolicy(int MaxConcurrent, int QueueLimit);

// Overlay-able: every knob nullable so "unset" falls through (R2). Pointer-light struct.
public readonly record struct ResilienceSpec(
    RetryPolicy? Retry = null, TimeoutPolicy? Timeout = null,
    FallbackPolicy? Fallback = null, HedgingPolicy? Hedging = null, string? ResourceName = null)
{
    public ResilienceSpec Overlay(in ResilienceSpec over) => new(          // step wins per-knob
        over.Retry ?? Retry, over.Timeout ?? Timeout,
        over.Fallback ?? Fallback, over.Hedging ?? Hedging, over.ResourceName ?? ResourceName);
}
```

### 3.3 Execution seam + resource registry
```csharp
public interface IResiliencePipelineFactory { IResiliencePipeline Get(in ResilienceSpec spec); } // cached per (spec, resource) in a FrozenDictionary
public interface IResiliencePipeline { ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> op, CancellationToken ct); }
```
```csharp
services.AddYFexResilience(r =>
{
    r.Resource("sefaz")
     .CircuitBreaker(failureRatio: .5, samplingDuration: 30.Seconds(), minThroughput: 20, breakDuration: 60.Seconds())
     .RateLimiter(permits: 5, per: 1.Seconds(), queueLimit: 10)
     .Bulkhead(maxConcurrent: 10);
});
```
No `Expression<>`, no reflection → the resilience layer is AoT-clean with no generator needed. Named resources compile to a `FrozenDictionary<string, ResilienceSpec>`; stateful pipelines are built **once per resource** and shared.

---

## 4. `YFex.Resilience.Polly`

Translates a `ResilienceSpec` (+ resolved named resource) into a Polly **v8 `ResiliencePipeline`**, composed in order: **rate limiter → bulkhead → circuit breaker → retry → timeout → hedging → fallback** (outermost to innermost; the timeout sits inside the retry so it caps each attempt, matching the Timeout policy below). Which outcomes count as transient failures (results such as HTTP 503 included, with a `Retry-After` hint) is decided per call by an optional `IFailureClassifier<T>`; without one, every exception except cancellation is transient. Caches one pipeline per `(spec, resource)`; stateful strategies built once per named resource. `<IsAotCompatible>true>`; validated like the existing `YFex.Messaging.Rpc.AotSample`. Polly types never appear on any public YFex surface — only inside this package.

### The six policies (detail)
- **Retry** — re-attempt transient failures; backoff (constant/linear/exponential) + jitter; caps attempts and total delay. Per-operation.
- **Timeout** — cap one attempt; cooperative `CancellationToken`. Per-operation.
- **Circuit breaker** — after `FailureRatio` over `SamplingDuration` (min throughput), open for `BreakDuration`; fail fast; half-open probe. **Per-resource** (shared state) — e.g. stop hammering SEFAZ.
- **Rate limiter** — cap throughput (token/window) to a downstream (SEFAZ per-CNPJ limits); optional queue. **Per-resource.**
- **Bulkhead** — cap concurrent in-flight calls to protect a slow dependency; optional queue. **Per-resource.**
- **Fallback** — substitute a degraded result on failure (cached/last-known/contingency). Per-operation; the delegate is registered in a typed table referenced by a `FallbackPolicy` token (no boxing).
- **Hedging** — fire parallel attempts after a delay, take the first success (latency-sensitive reads). Per-operation.

---

## 5. `YFex.Orchestration` — saga model

### 5.1 Step identity — R1
```csharp
saga.Step("reserve-stock").Invoke<ReserveStockCommand>(s => new(s.SaleId, s.Items)); // string label + typed
saga.Step<ReserveStockCommand>(s => new(s.SaleId, s.Items));                           // typed; Id = nameof(TCommand)
```
`StepDefinition` (value type): `{ string Id, Type CommandType, StepIndex Index, Func<TState,ICommand> Factory, ResilienceSpec? Resilience, ReverseSpec? Reverse, EventHandling Events, StepFailurePolicy Failure }`. Duplicate command type ⇒ string form required (build-time diagnostic).

### 5.2 Config precedence — R2 (same verbs; step wins per-knob)
Steps use the **same verbs** as the command builder (`.Timeout`, `.Retry`, `.UseResource`, `.Fallback`, …) — **no `Override*` methods.** Effective spec = `command.Resilience.Overlay(step.Resilience)`; a knob set on the step wins, unset knobs inherit, then framework defaults.
```csharp
saga.Step<ChargePaymentCommand>(s => new(s.SaleId, s.Total))
    .Timeout(8.Seconds())   // wins over the command's timeout
    .Retry(1);              // wins; UseResource("payments") still inherited from the command
```

### 5.3 Reverse — R3 (declare in 3 places, most-specific wins)
Precedence: **step `.CompensateWith` > standalone `IReverseRegistry` > command `.Reversible`**.
```csharp
b.Command<ChargePaymentCommand>().Reversible<RefundPaymentCommand>(c => new(c.SaleId, c.Total));      // (a) command
services.AddYFexReversals(r => r.Reverse<ArrangeShipmentCommand, CancelShipmentCommand>(c => new(c.SaleId))); // (b) registry
saga.Step<ChargePaymentCommand>(s => new(s.SaleId, s.Total))
    .CompensateWith<RefundToStoreCreditCommand>((c, r) => new(c.SaleId, c.Total));                    // (c) step override
```
Mechanics in §14. The saga's **step ledger** stores each committed step's *effective* `ReverseSpec`, so a step- or registry-declared reverse works even when the command has no CQRS `ReversePolicy`.

### 5.4 Events — R4 (multiple + group)
```csharp
saga.Step<SubmitNfeCommand>(s => new(s.SaleId))
    .ThenWaitForAny<NfeAuthorized, NfeRejected>(correlate: (s,e)=>Correlates(s,e), timeout: 15.Minutes(), onTimeout: OnFailure.Escalate)
    .OnEvent<NfeAuthorized>(apply: (s,e)=> s.NfeAccessKey = e.AccessKey, then: Continue)
    .OnEvent<NfeRejected>(OnFailure.CompensateAll)
    .OnEventGroup<FiscalRejectionEvents>(OnFailure.CompensateAll);   // IEventGroup — members expanded at compile time
```
`EventHandling` holds a small set of `(eventType|group → reaction)`; the generator expands groups and emits typed subscriptions. First correlated match resumes the parked saga.

### 5.5 Compensation (reverse order, `StepBitmap64`)
The saga keeps a **correlated step ledger** (`saga:{type}:{corr}` on `IKeyValueStore`, MemoryPack) + a `StepBitmap64` of committed steps. `CompensateAll` iterates set bits **high→low** (reverse order), calling `ReverseExecutor.ReverseAsync(cmd, result, effectiveReverseSpec, dispatcher)` per step. A compensation that itself fails → `ISyncFailureLog` + escalate. `CompensationResult` is a `union` (§13).

### 5.6 Saga state store
`ISagaStore` over `IKeyValueStore` (`saga:` prefix), MemoryPack-serialized `TState` + ledger + `StepBitmap64`, written per transition. Works client (IndexedDb) + server (SQLite/FS). Concurrency: optimistic version stamp per saga instance (reject stale writes).

---

## 6. Process managers — full detail

A **process manager (PM)** is a persisted, correlated state machine that reacts to **many independent events over time** and emits commands. A linear saga is a PM whose flow is a fixed sequence; the PM form is for long-running, event-driven flows.

### 6.1 Definition
```csharp
[ProcessManager]
public sealed partial class OrderProcess : ProcessManager<OrderState>
{
    protected override void Correlate(ICorrelation<OrderState> c)
    {
        c.StartsOn<OrderPlaced>(e => e.OrderId, (s,e) => { s.OrderId = e.OrderId; s.Total = e.Total; });
        c.On<PaymentConfirmed>(e => e.OrderId);
        c.On<ShipmentDispatched>(e => e.OrderId);
        c.OnGroup<PaymentFailureEvents>(e => e.OrderId);       // R4 group
    }

    public async ValueTask Handle(PaymentConfirmed e)          // handlers are plain async methods (AoT-safe)
    {
        State.Status = AwaitingShipment;
        await Send(new ShipOrderCommand(State.OrderId));       // reversible command via IDispatcher
        CancelTimeout("payment");
    }

    public ValueTask Handle(OrderPlaced e)
    {
        State.Status = AwaitingPayment;
        RequestTimeout("payment", 24.Hours(), new PaymentTimedOut(State.OrderId));  // durable deadline (§8)
        return default;
    }

    public async ValueTask Handle(PaymentTimedOut e) { await Send(new CancelOrderCommand(State.OrderId)); Complete(); }
    public ValueTask Handle(ShipmentDispatched e) { Complete(); return default; }
}
```

### 6.2 Runtime semantics
- **Correlation & rehydration.** An inbound event routes (generated `FrozenDictionary<Type, PmRoute>`) to the PM type; the correlation delegate extracts the key; `ISagaStore` loads (or `StartsOn` creates) the instance; the typed `Handle` runs; state is persisted. No reflection — the generator emits the event→handler dispatch as a typed `switch`.
- **Concurrency.** Per-correlation single-writer (optimistic version stamp / per-key async lock) so two events for the same instance don't race.
- **Timeouts.** `RequestTimeout(name, delay, message)` schedules a durable, cancellable one-shot (§8); `CancelTimeout(name)` removes it; if it fires, the message is delivered as a normal event to `Handle`.
- **Compensation.** A PM may `CompensateAll()` (reverse its committed reversible sends) or send explicit cancel commands — its choice, since a PM's "steps" aren't necessarily a strict sequence.
- **Completion / expiry.** `Complete()` marks terminal and prunes state (optionally after a retention TTL for idempotent late events). A dedup guard drops events arriving after completion.
- **Idempotency.** Inbound events carry a message id; a small per-instance seen-set (bounded) drops duplicates (at-least-once delivery from a broker).

### 6.3 Saga vs process manager
| | Linear saga (fluent steps) | Process manager (`Handle` methods) |
|---|---|---|
| Flow | fixed ordered sequence | arbitrary, event-driven |
| Trigger | orchestrator advances steps | each inbound event |
| Best for | atomic multi-step transaction | long-running workflow, external async |
| Compensation | automatic reverse-order ledger | explicit or `CompensateAll()` |
Same runtime: same `ISagaStore`, ledger, scheduler, compensation, generator.

---

## 7. Durable delay-scheduler (timeouts/deadlines)

Not `YFex.Jobs` (server/Hub-only, recurring, no per-correlation cancel, stubbed payload) and not `IDurableQueue<T>` (FIFO, no due-time). A **new lightweight scheduler on `IKeyValueStore`**:

- **Key scheme:** `sched:{dueUtcTicks:D19}:{scheduleId}` → ordered by due time via prefix range scan; `sched-idx:{corr}:{name}` → `scheduleId` for O(1) cancel.
- **Entry (MemoryPack):** `{ scheduleId, dueUtc, correlationKey, sagaType, messageTypeName, payload }`.
- **Claim-due loop:** a hosted service ticks (adaptive interval; sleeps until the next due time when idle → not a busy 5s poll); `ClaimDue(now, batch)` reads the due prefix, atomically claims (delete-on-claim / lease), delivers each timeout message to its saga via the **generated typed dispatch** (no reflection), batch-claim into a pooled span (low alloc).
- **Cancellation:** `Cancel(corr, name)` deletes via the index — clean expression of "cancel the timeout when the event arrives first."
- **Client + server:** works in Blazor WASM (IndexedDb backing) and on the server (SQLite/FS). On the client the tick can be activity-driven + a coarse timer.

`SchedulerHost.ClaimDueAsync` in `YFex.Jobs` is a good **reference shape** (not a dependency).

---

## 8. DLQ + ack/nack

### 8.1 Dead-letter (extend, don't fork)
Reuse `ISyncFailureLog`/`SyncFailure` (durable, ack + manual retry, `FailureAdded` event). Generalize `SyncFailure` to carry `SagaId`/`StepIndex`/category. **Automatic** dead-lettering: a resilience retry-exhaustion hook (§4) or a saga step's `OnFailure.Escalate` writes to the log — today it's fed only by outbox expiry and retried only manually.

### 8.2 ack/nack (broker seam, `.Rpc`)
For cross-service messaging (§9), delivery is explicit:
```csharp
public interface IMessageBroker
{
    ValueTask PublishAsync(in BrokerEnvelope msg, CancellationToken ct);
    IAsyncDisposable Subscribe(string topic, Func<Delivery, ValueTask> handler);
}
public readonly struct Delivery { public BrokerEnvelope Message { get; } /* … */
    public ValueTask AckAsync(CancellationToken ct);                        // processed → remove
    public ValueTask NackAsync(NackDisposition d, CancellationToken ct);    // fail → requeue or dead-letter
}
public enum NackDisposition : byte { Requeue, DeadLetter }
```
- **ACK** = success → broker removes it (at-least-once until acked).
- **NACK(Requeue)** = transient → redeliver (with backoff via a resilience policy).
- **NACK(DeadLetter)** = poison / retries exhausted → route to DLQ (`ISyncFailureLog` or a broker-native DLQ).
- An **inbox** (dedup of inbound) on `IDurableQueue<T>` guards at-least-once redelivery.
For the in-process/offline path there is no wire ack: success = outbox `RemoveAsync`; rejection = the conflict-resolver path (already shipped, scenario 3).

---

## 9. `YFex.Orchestration.Rpc`

- **DLQ policy** over `ISyncFailureLog` (auto retry-after-N; ack/retry/discard).
- **`IMessageBroker`** seam (§8.2) + inbox; distinct from `IEventBus` (in-process) and `IRemoteEventBus` (Fusion RPC — no ack/nack/consumer-groups).
- **Wolverine provider** (server-only) — §10.

---

## 10. Providers (in-house vs Wolverine)

The orchestration abstractions (`ISagaOrchestrator`, `ISagaStore`, scheduler, DLQ, broker) are **provider-neutral**. Two providers behind one DI switch:

```csharp
services.AddYFexOrchestration().UseInHouseSagas();   // client + server, Wolverine-free (default)
services.AddYFexOrchestration().UseWolverineSagas(); // server only, delegates to Wolverine
```

### In-house provider (default; the only client/offline/AoT path)
Composes YFex's own pieces, all source-gen'd and AoT-clean:
| Concern | Backed by |
|---|---|
| Saga/PM state + ledger | `ISagaStore` over `IKeyValueStore` (MemoryPack) |
| Compensation | `ICommandReverser.ReverseAsync` + effective `ReverseSpec` |
| Timeouts | the `IKeyValueStore` delay-scheduler (§7) |
| Events | `IEventBus` (typed generated subscriptions) |
| Resilience | `IResiliencePipelineFactory` (Polly) |
| DLQ | `ISyncFailureLog` |

### Wolverine provider (opt-in, server)
Maps the same abstractions onto Wolverine's built-ins (**wrap, don't rebuild**). Today `YFex.Messaging.Rpc.Wolverine` uses Wolverine only as a local mediator; the provider would additionally surface:
| YFex abstraction | Wolverine |
|---|---|
| saga persistence | Wolverine sagas (durable) |
| DLQ / ack-nack | Wolverine error handling + DLQ |
| retries | Wolverine retry policies |
| broker transport | Wolverine transports (Rabbit/Kafka/…) |
Never build an in-house broker to compete with Wolverine transports. The in-house engine's reason to exist is the **client/offline/WASM/AoT** world where Wolverine can't run.

---

## 11. Compensation-lib changes (`YFex.Cqrs.Compensation`, enables R3)

- `ReverseSpec` value type in **`YFex.Cqrs`** (beside `ReversePolicy`): `{ Type ReverseCommandType, Func<object,object?,object>? Map (typed-captured), Func<IDispatcher,object,ct,ValueTask>? Dispatch, Type? HandlerType }`.
- `ReverseExecutor.ReverseAsync(object cmd, object? result, in ReverseSpec spec, IDispatcher, ct)` — reverse from an **explicit** spec, not only `CommandPolicy.Reverse` (scenario-1 undo overload unchanged).
- `IReverseRegistry` + `AddYFexReversals(...)` for standalone declarations.
- Resolver: `step.Reverse ?? registry.Get(cmdType) ?? commandPolicy.Reverse`.
Additive; scenario-1 undo unchanged.

---

## 12. AoT verification plan
Each new library sets `<IsAotCompatible>true>` and is exercised by an `IsAotCompatible + TreatWarningsAsErrors` sample (mirroring `YFex.Messaging.Rpc.AotSample`): build a saga + a PM + timeouts + a compensation + each resilience policy; assert **0 `IL2xxx`/`IL3xxx`**. `YFex.Cqrs.Compensation` gets the flag now (clean). `YFex.Cqrs` is a separate task (its `Expression.Compile` config paths — see §0/decisions).

---

## 13. `union` catalog (net11 native `union`)
Payload-bearing exhaustive outcomes → `union`; closed payload-less sets → `enum`.
```csharp
public readonly union StepOutcome(Committed, Waiting, Failed, Skipped);
public record Committed(object? Result);
public record Waiting(/* which events */);
public record Failed(Error Error);
public record Skipped();

public readonly union WaitOutcome(EventReceived, TimedOut, Cancelled);
public record EventReceived(object Event);
public record TimedOut();
public record Cancelled();

public readonly union CompensationResult(AllReversed, PartiallyFailed);
public record AllReversed();
public record PartiallyFailed(StepIndex FailedAt, Error Error);
```
**Enums (no payload):** `SagaStatus { Running, Parked, Compensating, Completed, Cancelled, DeadLettered }`, `BackoffStrategy`, `NackDisposition`, `OnFailure { Continue, Retry, CompensateAll, Escalate, DeadLetter }`. (`OnFailure` is a directive enum; if a variant needs data later it can promote to a union.)

---

## 14. Decisions & open items
1. **DECIDED:** `ReverseSpec` lives in **`YFex.Cqrs`** (beside `ReversePolicy`; reused by command config, `IReverseRegistry`, saga steps).
2. **DECIDED:** saga step verbs **match the command builder**; no `Override*` — step wins per-knob, unset inherits.
3. **DECIDED:** orchestration uses **`Func<>` not `Expression<>`** → AoT-clean by construction; all discovery/wiring **source-generated**, no reflection.
4. **DECIDED:** `<IsAotCompatible>` on all new libs + `YFex.Cqrs.Compensation` (clean). **`YFex.Cqrs`** is a *separate task* — its `Expression.Compile`/`DynamicInvoke` config paths (Optimistic/Reverse/Invalidates maps) are `IL3050`; closing them needs interpreted expressions or source-gen'd maps.
5. Open: step ledger reuses `StorageBackedCommandJournal` (+ correlation index) vs. sibling `saga:` store — **recommended sibling store** (keep the global journal for standalone undo).
6. Open: does P0 replace `RetryPolicy`/`BackoffStrategy` execution outright, or run alongside during migration.
7. Open: client scheduler tick strategy (activity-driven + coarse timer vs. always-on hosted service) under Blazor WASM.

---

## 15. Phasing
- **P0 — `YFex.Resilience` + `.Polly`.** Policy structs, `ResilienceSpec` overlay, factory seam, named-resource `FrozenDictionary`, Polly v8 impl; wire into CQRS dispatch + outbox backoff. AoT sample.
- **P1 — `YFex.Orchestration` core + generator.** Linear saga runtime, `StepBitmap64`, step ledger + reverse-order compensation, `ISagaStore`, delay-scheduler; `ReverseSpec`/`IReverseRegistry` (R3); step verbs/overlay (R2); generator for step tables + typed reverse dispatch. AoT sample.
- **P2 — process managers + events (R4) + auto-DLQ.** PM runtime + `Handle` dispatch generation; multi/group `OnEvent`/`WaitFor` via CQRS group expansion; auto dead-letter into `ISyncFailureLog`.
- **P3 — `YFex.Orchestration.Rpc`.** `IMessageBroker` + ack/nack + inbox; Wolverine provider.
