# Orchestration Internals

This document covers the internal architecture of `YFex.Orchestration` (+ `.Generator`, `.Rpc`, `.Rpc.Wolverine`): the saga forward-run and reverse-order compensation, the `StepBitmap64` committed-set, the R3 reverse resolver, the object-typed process-manager runtime, the durable delay-scheduler key scheme, the generator's saga/PM emission and compile-time group expansion, and the broker/inbox/DLQ stack. Read this when modifying the runtime, the generator, or the cross-service layer. For the public API, see [Orchestration](../public/orchestration.md); for the original rationale, see [Orchestration + Resilience Design](orchestration-resilience-design.md).

---

## Why `Func<>`, never `Expression<>`

`YFex.Cqrs` uses `Expression<Func<…>>` for `Invalidates`/`Optimistic`/`Reversible` because those are *inspected and transported*, and compiling them needs `Expression.Compile` (`IL3050`). **Orchestration executes in-process and never inspects its lambdas**, so it uses plain `Func<>` delegates — command factories, correlation key extractors, `apply`, step reverse maps. Plain delegates are compiled by the C# compiler, so the whole runtime is AOT-clean by construction. This is the load-bearing decision behind every value type and generated artifact below.

---

## Saga runtime

### Forward run

`SagaOrchestrator.RunAsync` is a synchronous forward loop (no parking): for each `StepDefinition` it builds the command via the erased `Factory`, computes the effective `ResilienceSpec` (`command.Overlay(step)`), dispatches through `ExecuteAsync`, and on success records a live `LedgerEntry { Command, Result, Reverse }` and sets the step's bit. A snapshot is persisted per transition.

```
for each step i:
  command = step.Factory(state)
  effective = ResilienceSpec.None.Overlay(step.Resilience)
  res = ExecuteAsync(step, command, effective)         // through the pipeline if a factory is registered
  if !res.IsSuccess:
     Continue      → skip, advance
     Escalate      → persist DeadLettered, dead-letter, return
     CompensateAll → CompensateAsync(committed, ledger); persist Cancelled|DeadLettered
  else:
     ledger[i] = (command, result, ResolveReverse(step)); committed = committed.Set(step.Index); persist Running
persist Completed
```

`ExecuteAsync` wraps the dispatch in `IResiliencePipelineFactory.Get(spec).ExecuteAsync(...)` when a factory is present and the spec is non-empty; otherwise it dispatches directly.

### Reverse-order compensation

`CompensateAsync` walks the `StepBitmap64` enumerator (which yields committed steps **high→low** with zero allocation — no pooled plan buffer needed) and calls `ReverseExecutor.ReverseAsync(cmd, result, effectiveReverseSpec, dispatcher)` per step. A compensation that itself throws halts the walk and returns `CompensationResult.PartiallyFailed(FailedAt, Error)` → terminal status `DeadLettered` → auto-DLQ.

> **⚠️ Note:** The step-level reverse dispatch delegate (`CompensateWith`) awaits `IDispatcher.CommandAsync` but does **not** inspect the returned `QueueableResult` — a reverse that returns `Fail` (rather than throwing) is treated as reversed. Compensation only becomes `PartiallyFailed` when a reverse *throws*.

### R3 reverse resolver

`ResolveReverse(step) = step.Reverse ?? ReverseResolution.ForCommandType(cmdType, registry, cqrsRegistry)`, where the shared `ReverseResolution.ForCommandType` tries the standalone `IReverseRegistry` then the command's CQRS `ReversePolicy` (materializing a `ReverseSpec` from `ReverseCommandType`/`Map`/`DispatchReverse`). The **effective** reverse is stored in the live ledger, so a step- or registry-declared reverse works even when the command has no CQRS reverse policy.

### StepBitmap64

A `readonly struct` over a `ulong`, mirroring `YFex.State.PropertyBitmap64`. `Set(index)`, `!= 0` for "anything to compensate?", and a struct enumerator that scans set bits high→low. Capacity is 64; `SagaBuilder.NewDraft` throws at build time on the 65th step (the pooled `StepIndex[]` fallback is deferred).

---

## Process-manager runtime

The PM runtime is **object-typed** — every `TState`/PM-type-specific operation is delegated to a generated `IProcessManagerModule`, so the runtime itself is reflection-free.

### Dispatch flow

`ProcessManagerRuntime.DispatchEventAsync(module, evt)`:

```
correlation = module.Correlation.Resolve(evt)      // exact-type, else group IsInstanceOfType scan
key = correlation.Key(evt)
sagaId = module.PmType.Name + ":" + key
lock (per-sagaId SemaphoreSlim):                   // single-writer per instance
  raw = store.LoadRawAsync(sagaId)
  if raw is null:
     if !correlation.IsStart: return               // no instance, not a starter → drop
     state = module.NewState(); correlation.Apply(state, evt); version = 0
  else:
     if raw.Status == Completed: return            // tombstone → drop late/duplicate events
     state = module.DeserializeState(raw.State); version = raw.Version
  ctx = new PmContext(state, dispatcher, resolveReverse)
  pm = module.NewInstance(); module.Bind(pm, ctx); await module.InvokeAsync(pm, evt)
  status = ctx.Completed ? Completed : Running
  store.SaveRawAsync(sagaId, serialize(ctx.State), version+1, status)
  apply ctx.Cancels (scheduler.CancelAsync) then ctx.Timeouts (scheduler.ScheduleAsync)
```

`PmContext` collects the handler's effects: `Send` dispatches immediately (recording reversibles for `CompensateAll`), while `RequestTimeout`/`CancelTimeout`/`Complete` are **deferred** and applied by the runtime after the state is durable. This is why `RequestTimeout` is a synchronous `void` while `Send`/`CompensateAll` are awaited.

### Raw store path

PMs use `ISagaStore.SaveRawAsync` / `LoadRawAsync` (`RawSagaSnapshot` = pre-serialized `byte[]` state), because the runtime is object-typed and serializes state through the module's typed `SerializeState`/`DeserializeState` — bypassing the generic `SagaSnapshot<TState>` overloads.

### Timeout delivery

`ProcessManagerRuntime` also implements `ITimeoutDispatcher`. When `SchedulerHostedService` fires a due entry, the runtime looks up the module by `entry.SagaType`, calls `module.TryDeserializeTimeout(messageTypeName, payload)` (a generated typed `switch`), and delivers the message on the *known* correlation key — bypassing correlation resolution (a timeout message needn't be correlated).

---

## Durable delay-scheduler

Lightweight scheduler over `IKeyValueStore` (not `YFex.Jobs`, not `IDurableQueue`):

| Key | Purpose |
|---|---|
| `sched:{dueUtcTicks:D19}:{scheduleId}` | Entry, ordered by due time via an ordinal prefix range scan. |
| `sched-idx:{corr}:{name}` | Reverse index → the entry key, for O(1) cancel. |

`ClaimDueAsync` reads the due prefix, sorts (zero-padded ticks → ordinal sort == due-time order) into a **pooled** `ArrayPool<string>` buffer, claims each by delete-on-claim, and delivers. `SchedulerHostedService` is adaptive — after each claim it sleeps until the next due time (clamped to a 30s ceiling), so it's not a busy poll.

> **📌 Note:** Claim is delete-on-claim (at-most-once per host). A store-level CAS lease for multi-host at-least-once is deferred.

---

## Generator

`SagaGenerator` runs two `ForAttributeWithMetadataName` pipelines.

### Sagas

Discovers `[Saga]` partial classes deriving `Saga<TState>`, then emits `AddGeneratedSagas()` which registers `new ConcreteSaga().BuildDefinition()` per saga. The typed factories, step dispatch, and reverse dispatch are captured **inside the saga's own generic `Configure` calls** (where the command types are compile-time generic parameters) — so the generator emits discovery + concrete construction, not re-emitted lambdas. No `MakeGenericType`/`MakeGenericMethod` anywhere.

### Process managers

Discovers `[ProcessManager]` classes, collects their `public ValueTask Handle(TEvent)` methods, and emits one `IProcessManagerModule` per PM plus `AddGeneratedProcessManagers()`. The module carries: the concrete `EventTypes[]`, a correlation map built from `Correlate`, `NewInstance`/`NewState`/`SerializeState`/`DeserializeState`, `Bind`, an `InvokeAsync` typed `switch` over event type, a `TryDeserializeTimeout` `switch` over message-type name, and a `Subscribe` that instantiates one closed-generic `PmEventRecipient<T>` per event on the `IEventBus`.

### Compile-time group expansion (R4)

For a `Handle(TGroup)` where `TGroup : IEventGroup`, the generator scans `compilation.Assembly.GlobalNamespace` for concrete types implementing the group interface and emits one subscription + dispatch case per member, upcasting to the group type (`pm.Handle((TGroup)e)`). A direct `Handle(TConcrete)` wins over a group upcast for the same concrete type.

> **⚠️ Warning:** Group expansion resolves members from the compilation's **source assembly** only (not referenced assemblies). Group-member events defined in another project won't be discovered — keep an `IEventGroup`'s members in the same assembly as its consumers, or declare an explicit `Handle`/`On<>` per member.

Return-kind handling: `Handle` may return `ValueTask` (dispatched directly), `Task` (wrapped in `new ValueTask(...)`), or `void` (called then `return default`). `Task<T>`/`ValueTask<T>` are rejected (a `Handle` returns no value).

### Diagnostics

`YFORCH001` saga not partial · `YFORCH002` saga missing base · `YFORCH010` PM missing base · `YFORCH011` PM needs a public parameterless ctor · `YFORCH012` PM has no `Handle` methods. Each generator defines its own `EquatableArray<T>` (netstandard2.0 analyzers can't share code) so incremental caching stays stable.

---

## Auto-DLQ seam

Core `YFex.Orchestration` defines `IDeadLetterSink` + the `DeadLetter` record (SagaType, Correlation, StepIndex?, CommandTypeName, Reason, Category); the default is a no-op. The orchestrator writes a dead-letter on `StepFailurePolicy.Escalate` (category `"saga-escalate"`) and on a failed compensation (`"saga-compensation"`). `YFex.Orchestration.Rpc.SyncFailureDeadLetterSink` routes these into `IWritableSyncFailureLog`, mapping onto the generalized `SyncFailure` fields (`SagaId`/`StepIndex`/`Category`, added additively — see [Messaging Internals](messaging-internals.md)).

---

## Cross-service broker (YFex.Orchestration.Rpc)

The `IMessageBroker` seam (`PublishAsync` + `Subscribe(topic, Func<Delivery, ValueTask>)`) with a `Delivery` carrying `AckAsync`/`NackAsync(NackDisposition)`. The pieces:

- **`BrokerConsumer`** — the pump: dedup via `IInbox` → invoke handler → **ack** on success (and mark the inbox), **nack(Requeue)** while `IDlqPolicy` says `Retry`, **nack(DeadLetter)** + write to `IDeadLetterSink` (category `"broker"`) when it says give up.
- **`KeyValueInbox`** — dedup over `IKeyValueStore` (`inbox:` prefix, TTL). **Two-phase**: the id is marked only *after* the handler succeeds, so a failed-then-requeued message reprocesses rather than being swallowed. The check+mark is not atomic; the two-phase order makes a lost race redeliver (never drop), preserving at-least-once.
- **`RetryThenDeadLetterPolicy`** — retries until the envelope's `Attempt` reaches `maxAttempts`, then dead-letters.
- **`InMemoryMessageBroker`** — a synchronous dev/test double; `Requeue` redelivers recursively with `Attempt+1` (bounded by the policy). Explicitly **not** a durable transport.

### Wolverine provider (`.Rpc.Wolverine`, server only, not AOT)

`WolverineMessageBroker.PublishAsync` delegates to Wolverine's `IMessageBus.PublishAsync` (routes over Wolverine transports). `Subscribe` throws `NotSupportedException` — Wolverine owns consumption/ack/retry/DLQ via its own handler discovery + error policies, which don't map onto the pull-based `Subscribe` seam ("wrap, don't rebuild"). `UseWolverineSagas()` registers the broker + dead-lettering; saga state persistence continues to use the in-house `ISagaStore` (mapping onto Wolverine-native durable sagas is a deferred follow-up).

---

## RoselYn / build gotcha

The RoselYn MCP server's FileSystemWatcher holds the main-repo generator DLLs open, which breaks `--no-incremental` and sample rebuilds that recopy those generators. Stop that process to free the lock (it respawns on next use). Unrelated to the orchestration code itself.
