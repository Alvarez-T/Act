# 04 — Implicit instrumentation (generators + library choke points)

## 1. The rule: generators describe, choke points record

Developers never call logging for framework operations. Two mechanisms cover the whole ecosystem:

1. **Generators emit descriptors and capture code, not wrapping code, wherever a runtime choke point exists.** The CQRS
   generator already sees every nested `IQuery<T>`/`ICommand`/`IEvent` record, so it emits an `OperationDescriptor<TMessage>` with its
   capture function and registers it into `OperationDescriptorFor<TMessage>` from the module initializer it already has. The **dispatcher**
   (one generic method, `TCommand` known, no boxing) records the operation. Generated code stays tiny, and every library has
   exactly one place where timing/outcome logic lives.
2. **Generators emit inline wrapping only where no choke point exists**: `[StateCommand]` wrappers, `[Subscribe<T>]` adapters,
   NavigatR `OnNavigation` bridges, `[AggregateCommandHandler]` bodies, saga/process-manager steps, `[Job]` invocations, Fusion
   RPC proxies. Those generators *already* emit the wrapper method; logging adds the canonical shape of §2 to it.

All generated code references **only `global::YFex.Diagnostics.*`** (YFex core). No library gains a reference to `YFex.Logging`.

## 2. Canonical emitted shapes (normative)

```csharp
// async, with result
var __operation = global::YFex.Diagnostics.Operations.Start(in message);            // or Operations.Start(__Desc.X) when no message
try
{
    var __r = await next(message, ct).ConfigureAwait(false);
    __operation.Complete(__r.ToOutcome(), __r.OutcomeCode());                    // per-library mapping (§5); code interned once per site
    return __r;
}
catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested) { __operation.Complete(global::YFex.Diagnostics.OperationOutcome.Cancelled); throw; }
catch (global::System.Exception __ex) { __operation.Fail(__ex); throw; }
```

- No `using` (CS1657 trap with `ref` structs, and the outcome must be explicit). No lambdas, no closures.
- When `Operations` is not installed, `Start` returns an inactive token after one static load, and `Complete`/`Fail` return immediately.
- Sync shapes are identical without `await`.

## 3. What gets captured: automatic, decided by TYPES, reviewed by a snapshot (⚑L8 revised)

Developers do **not** annotate every member. Capture is automatic (`LogCaptureMode.Auto`, the default) and the generator
decides per member **by its type**:

| Member type | Captured as | Why |
|---|---|---|
| numbers, `bool`, enums, dates, `Guid`, `TimeSpan` | value | safe and useful |
| core value types (`Money`, `Percentual`, `Quantity`, `Mass`, `Length`) | value (via `IRawValue<T>`) | safe |
| value objects with `[LogValue]` (`VendaId`, `ChaveNFe`, `Ncm`) | value; `SearchKey = true` ones are also indexed (13 §4) | declared once on the struct |
| value objects with `[Sensitive]` (`Cpf`, `Email`, `Telefone`, `Rg` in YFex.Entity.Public) | redacted per policy (`#h:` hash / mask / encrypt) | declared once on the struct, applies everywhere |
| `Secret` / `Financial` types or members | **erased** (`<redacted:Secret>`), never even hashed | cannot be relaxed |
| raw `string` | value, **truncated** (256 chars) and **scrubbed** on the consumer (CPF/e-mail/phone/card/JWT patterns, 07 §3.3) | strings are where accidents happen |
| raw `string` whose **name** looks secret (`senha`, `password`, `token`, `secret`, `pin`, `apikey`, `cvv`) | **erased** + info diagnostic YFL0031 suggesting a value object | defence in depth |
| raw `string` whose name looks personal (`nome`, `email`, `telefone`, `endereco`, `cpf`, `documento`) | treated as `Personal` (masked/hashed) + YFL0032 suggesting the value object that already exists | nudges toward `Cpf`/`Email` types |
| collections | `count` only (`[LogItems(5)]` opts into the first N items) | volume |
| nested records/DTOs | their generated **log shape** (03 §1.5), depth 2 | structure without reflection |
| `byte[]`, `Stream`, `ReadOnlyMemory<byte>` | length only | volume |

**The safety net is a snapshot, not attributes.** The generator exposes every message's captured schema
(`OperationDescriptor.Fields`: name, type, sensitivity, how it is written), and `YFex.Logging.Testing` ships a one-line golden test:

```csharp
[Fact] public Task Log_schema_is_reviewed() => LogSchemaSnapshot.Verify(typeof(Venda).Assembly);   // writes/compares LogSchema.verified.txt
```

```
# LogSchema.verified.txt (committed; a diff shows up in code review)
Venda.Finalizar (cqrs.command)
  id        VendaId     Internal           value      search-key
  cliente   Cpf         SensitivePersonal  hash
  total     Money       Internal           value
  itens     List<Item>  —                  count
  obs       string      Internal           scrubbed, max 256
```

Adding `string Senha` to a command changes this file, so the reviewer sees `senha string Secret erased` in the PR. Nobody has
to remember an attribute.

**The few attributes that remain are exceptions** (YFex core, so `*.Public` contract projects can use them):

| Attribute | On | Effect |
|---|---|---|
| `[NoLog]` | member / type | Never captured. On a type: no descriptor (still counted in metrics) |
| `[Sensitive(...)]` (`YFex.Privacy`, 02 §5.2) | value-object type (normal) / raw member (exception) | sensitivity for every consumer, not only logging |
| `[LogValue(SearchKey = true)]` | value-object type | how the type writes itself, and whether it is indexed for search |
| `[LogName("Venda.Finalizar")]`, `[LogId(0x5A1E0013)]` | type | stable name/id across renames (02 §8) |
| `[Audited]`, `[LongRunning]`, `[LogLevel(...)]` | type | behaviour; prefer the aggregate configuration below |

Capture mode can be narrowed per assembly or aggregate: `[assembly: LogCapture(LogCaptureMode.KeysOnly)]` (only search keys,
numbers and outcome) or `LogCaptureMode.None` (shape only).

**Behaviour** (level, sampling, audit, slow threshold, member exclusions) can also be set in the aggregate configuration, keeping
YFex's "configuration over attributes for CQRS" convention. Data classification stays on the type, because it is a property
of the data:

```csharp
b.Command<FinalizarVendaCommand, VendaFinalizada>()
 .Validation(v => v.Field(c => c.Total).Positive())
 .Logging(l => l.Audit().SlowAfter(TimeSpan.FromMilliseconds(500)).Ignore(c => c.Observacao));
```

`.Logging(...)` lambdas are parsed **syntactically by the CQRS generator** (same mechanism as `.Validation` D27): `Ignore`
removes the member from the compiled capture code, and the runtime only applies flags and thresholds. No `Expression<>` at run time.

**Opt-out:** `[NoLog]`, or the MSBuild property `<YFexInstrumentation>false</YFexInstrumentation>` (generators skip emission and
runtime stages see `OperationDescriptor<T>.Disabled`).

**Shared generator code:** `YFex/src/Shared/Generators/DiagnosticsEmit.cs` (descriptor emission, capture resolution per 02 §5.1,
attribute parsing) is **linked** into each generator project (`<Compile Include=… Link=…>`), the same strategy as the per-generator
`EquatableArray<T>`. Diagnostics: YFL0001–YFL0099 (logging generator), plus each generator's own prefix for its instrumentation
diagnostics.

## 4. Span ownership (one span per boundary, no duplicates)

| Rule | Example |
|---|---|
| **Client side and server side are different spans** (OTel model) | client `Command FinalizarVenda (side=client)` → `RpcClient` → server `Command FinalizarVenda (side=server)` |
| On the server, the **RPC middleware creates no operation**. It only adopts the incoming `traceparent` + baggage. The handler-invoker stage owns the server span | avoids an `RpcServer` + `Command` pair for the same work |
| **Pipeline stages annotate** the enclosing operation (validation result, authorization, rules fingerprint) instead of opening child spans | `violations=2 code=VAL.REQUIRED` on the command record |
| Exception: **rule evaluation** is its own record-only child operation (`Leaf`, Debug) because its timing matters | `RuleEvaluation desconto v12 fp=… 0.4ms` |
| UI-initiated flows: **`[StateCommand]` is the trace root** on the client | click → command → RPC → SQL in one waterfall |
| Nested dispatch inside a handler is a child through the ambient context | `FinalizarVenda` → `BaixarEstoque` |

## 5. Per-library integration

| Library | Choke point (R = runtime, G = generated inline) | Kind(s) | What is recorded (beyond name/duration/outcome/tenant/principal/trace) | Outcome mapping | Audit by default |
|---|---|---|---|---|---|
| **YFex.Cqrs** | R: new outermost `LoggingStage` in `MessagePipeline`, used by `LocalDispatcher` and `FusionMessageBus` (generic `IDispatcher`). G: descriptors + captures for every message, `.Logging` parsing | Command, Query, EventPublish | `side`, validation violation count + first codes (never values), authorization result, rules fingerprint/verdict/approval-required, cache provenance (`CacheableQueryResult` Fresh/Cached/Stale → flags), offline queueing | `QueueableResult` Ok→Ok, Queued→Queued, Error→Rejected(code); `Error.ApprovalRequired`→Rejected(`RULES.APPROVAL`) | Commands of `[Audited]` aggregates; all denials |
| **YFex.Messaging** | G: `[Subscribe<T>]` adapters; R: `IEventBus.Publish` | EventPublish, EventHandle | recipient count, handler type, `KeepAlive` drops (weak refs collected) | exception → Faulted | — |
| | G: `[Live]` refresh loop | Query (`live.refresh`) | poll/refresh errors, stale flag | errors → Faulted at Warning | — |
| **YFex.Messaging.Rpc** | G: generated Fusion proxies (`RpcProxyCodeBuilder`) → `RpcClient`; R: `CallContextMiddleware` adopts trace; R: `IHandlerInvoker` decorator `TracingHandlerInvoker` (descriptor by `message.GetType()` through a frozen map built from the registry) | RpcClient, Command/Query (server) | peer, method, retries, reconnects, CallContext principal/tenant source | Fusion/RPC exceptions → Faulted/TimedOut | permission-version revocations |
| | R: outbox | OutboxEnqueue, OutboxReplay | the enqueued trace context is stored **with the outbox entry**; replay = new trace with `Replay` link (05 §4) | conflict → Conflict | — |
| | R: sync / `ISyncFailureLog` | Sync | conflicts, resolutions | Conflict | — |
| **YFex.State** | G: `[StateCommand]` wrapper | StateCommand (trace root, Boundary) | command name, VM type, `CanExecute` false attempts (Debug) | Cancelled / Faulted | — |
| | G: setter hooks by inferred `StateChangeRole` (12); History observer for `[Undoable]`; `[LogChanges]` becomes an alias of `[StateLog(UserInput)]` | Mark (Debug, Leaf), per-session edit ring | property, coalesced new value per type sensitivity, who changed it (user/command/server/undo) | — | no (shipped on failure/slow/audit/grant) |
| | R: `OnAsyncValidationFaulted` | Mark (Warning) | validator, exception | Faulted | — |
| **YFex.Validation** | R: called by stages → annotations only (too hot for operations) | — | server-side violation on a message the client already validated → `validation.server_mismatch` Warning (tampering / version skew) | — | — |
| **YFex.Rules** | R: `RuleSetHost` evaluate | RuleEvaluation (Leaf, Debug) | rule set, version, tier path, fingerprint, verdict, applied rule count; explanation/trace at Trace level into the flight recorder | blocked → Rejected | **applied adjustments, overrides, approvals** (`IDecisionAuditSink` adapter); Rules.Admin publish/diff (`IRuleAuditSink` adapter) |
| **YFex.Authorization** | R: permission checks; G: `[RequirePermission]` descriptors | Authorization | permission, resource, principal | denied → Denied | **all denials**; grants at Debug |
| **YFex.Tenancy** | R: resolution middleware, `ScopeOverride.Enter` | Mark | strategy used, failures | failure → Denied | **impersonation** (`ITenantImpersonationAuditor` adapter) |
| **YFex.NavigatR** | R: navigator state machine; G: route descriptors (`[Route]`) | Navigation (Breadcrumb) | from → to route, params opt-in (ids), prefetch child operations, guard denials, pool eviction (Debug), time to `OnNavigated` | guard → Denied; exception → Faulted | — |
| **YFex.EventSourcing** | G: `[AggregateCommandHandler]`; R: store append/read, projection host, snapshot, sync | Command (aggregate), EventAppend, EventRead, Projection, Sync | stream key, expected/new version, event count, bytes; projection lag gauge; **trace/causation ids stored in event metadata** | version mismatch → Conflict | appended event types per stream (configurable) |
| **YFex.Orchestration** | G: `[Saga]`/`[ProcessManager]` steps; R: scheduler, broker, DLQ | Saga (LongRunning), SagaStep, Compensation, ProcessManager | instance id, step index, compensation chain, timeouts, DLQ reason | compensated → flag; DLQ → Faulted (Error) | DLQ entries |
| **YFex.Jobs** | R: `LoggingJobObserver : IJobObserver` (adapter, no engine change); G: `[Job]` descriptors | Job (LongRunning) | schedule id, attempt, policy decision, deferred-until, dead-letter reason | retried → Retried flag; dead-lettered → Faulted | dead letters |
| **YFex.Resilience** | R: policy pipeline hooks | Resilience (Mark) | retry attempt/delay, **circuit opened/half-open/closed**, bulkhead rejection, fallback used, hedging | timeout → TimedOut | — |
| **YFex.Persistence** | G: `[Persist]` save/load; R: `IKeyValueStore` | Persistence (Debug, Leaf) | key prefix, bytes, encryption errors | Faulted | — |
| **YFex.Data** | R: QueryBuilder execution + `TracingDbConnection` wrapper for raw ADO.NET (Rules.Data, Web.Data) | Sql | db system, verb + table from QueryBuilder (`db.query.summary`), rows, duration, slow → Warning; **never parameters** | exception → Faulted | DDL; RLS key applied (`[RowLevelSecurity]`) |
| **YFex.Web** | R: `TracingHandler : DelegatingHandler` added by `AddApi<T>`; AspNetCore middleware | HttpClient, HttpServer | API name, status, retries, token refresh, rate-limit waits; credential **name** only | 4xx → Rejected, 5xx/exception → Faulted | — |
| **YFex.Payment** | migrate `PaymentTelemetry` to descriptors | Custom (`payment.*`) | provider, method, attempt outcome | per `AttemptOutcome` | **status transitions, webhooks** |
| **YFex.Devices** | R: device I/O | Device | device id/kind, port, error codes, reconnects | timeouts / IO errors | — |
| **YFex.Automation** | R: `IStepObserver` adapter; AI calls | AutomationStep | step, retry safety, **AI tokens/cost** | per `StepOutcome` | AI agent loop tool calls (already audited via `IRunStore`) |
| **YFex.CustomLogic / Capabilities** | G: `[CustomLogic<T>]` | CustomLogic | **which tenant logic version ran** | Faulted | yes (tenant-authored code) |
| **YFex.Search** | R: query execution | Query | result count, ranking time | — | — |
| **YFex.UI / Avalonia** | R: dialogs, unhandled dispatcher exceptions | UiAction (Breadcrumb) | dialog shown/result, view model type | Faulted | — |

## 6. Concrete before / after — a sale, end to end

**What the developer writes (unchanged from today):**

```csharp
public static partial class Vendas
{
    public static partial class Commands
    {
        [Audited]
        public partial record FinalizarVendaCommand(VendaId Id, Cpf? Cliente, Money Total)   // no member attributes:
            : ICommand<VendaFinalizada>, IQueueable;                                         // Cpf is [Sensitive] on its own struct
    }
}

public partial class CaixaViewModel : PageViewModel
{
    [StateCommand] async Task FinalizarAsync(CancellationToken ct)
        => Resultado = await Dispatcher.FinalizarVenda(new(VendaId, Cliente, Total), ct);
}
```

**What the CQRS generator adds (inside the module initializer it already emits):**

```csharp
// (sketch) generated
global::YFex.Diagnostics.OperationDescriptorFor<Vendas.Commands.FinalizarVendaCommand>.Descriptor =
    global::YFex.Diagnostics.OperationRegistry.Register(new global::YFex.Diagnostics.OperationDescriptor<Vendas.Commands.FinalizarVendaCommand>(
        kind: global::YFex.Diagnostics.OperationKind.Command, name: "Vendas.FinalizarVenda", module: "YFex.Farma.Vendas",
        level: global::YFex.Diagnostics.LogLevel.Information,
        flags: global::YFex.Diagnostics.OperationDescriptorFlags.Audit | global::YFex.Diagnostics.OperationDescriptorFlags.Boundary,
        fields: [new("id", DataSensitivity.Internal), new("cliente", DataSensitivity.SensitivePersonal), new("total", DataSensitivity.Internal)],
        capture: static (in Vendas.Commands.FinalizarVendaCommand c, ref global::YFex.Diagnostics.LogWriter w) =>
        {
            w.Write<VendaId>(0, c.Id);                       // VendaId : ILogValue<VendaId>
            if (c.Cliente is { } cpf) w.Write<Cpf>(1, cpf);  // Cpf.Sensitivity = SensitivePersonal → redacted on consumer per policy
            w.WriteRawValue<Money>(2, c.Total);              // Money : IRawValue<Money>
        }));
```

The State generator wraps `FinalizarAsync` with the §2 shape (`OperationKind.StateCommand`, trace root). The dispatcher's
`LoggingStage`, the generated RPC proxy and the server `TracingHandlerInvoker` do the rest.

**What you get (text sink, one trace):**

```
14:02:11.470 INF [ui ] Caixa.Finalizar                 ok      31.9ms  acme/loja7 u:joao s:3fa2 ┐ trace 4bf92f35…
14:02:11.471 INF [cmd] Vendas.FinalizarVenda client    ok      30.8ms  id=V-88123 total=R$ 149,90 cliente=#h:9f2c… │
14:02:11.472 DBG [rpc] IVendasRpc.FinalizarVenda       ok      29.7ms  peer=srv-b                                    │
14:02:11.476 INF [cmd] Vendas.FinalizarVenda server    ok      24.1ms  rules=desconto@v12 fp=77ac… verdict=Allow     │  node srv-b
14:02:11.481 DBG [sql] INSERT venda                    ok       3.2ms  rows=1                                         │
14:02:11.489 DBG [sql] UPDATE estoque                  ok       6.0ms  rows=4                                         ┘
         AUD   Vendas.FinalizarVenda by joao @ acme/loja7  total=R$ 149,90  (chain #18234 h=c01d…)
```

**Offline variant:** the dispatcher returns `Queued`, so the client record shows `queued`. The outbox entry stores the trace
context. Two hours later the replay creates a new trace `OutboxReplay` with a `Replay` link back to `4bf92f35…`, and the viewer
shows both, connected (05 §4).

**Failure variant:** the server throws inside `UPDATE estoque`. Every Debug record of that trace is already in the flight
recorder, so it flushes as `Retro`. The issue `DbUpdateException @ Estoque.Baixar` (fingerprint `…`) records occurrence #3
for `acme/loja7` on release `1.4.2` with the breadcrumbs `Caixa → Pagamento → Finalizar`, and the alert rule "new issue in
prod" sends a Telegram message.
