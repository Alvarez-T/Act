# 03 — Runtime pipeline (`YFex.Logging`)

## 1. Producer APIs

Three entry points. All of them end in the same slot and record format.

### 1.1 `Operations` — the probe hub (YFex core, used by generated code and libraries)

```csharp
namespace YFex.Diagnostics;

public static class Operations
{
    // Fast checks: one static load + compare. No engine installed → always false.
    public static bool IsEnabled(OperationDescriptor d, LogLevel level);
    public static bool IsEnabled<T>(LogLevel level);                 // via OperationDescriptorFor<T>

    // Operation scope. Captures fields at Start (message is in hand), writes ONE record at Complete.
    public static ActiveOperation Start<T>(in T message, OperationKind kindIfUnregistered = default);
    public static ActiveOperation Start(OperationDescriptor d);
    public static ActiveOperation StartLinked(OperationDescriptor d, in OperationLink link);   // outbox replay, ES catch-up (05 §4)

    // Instant events: breadcrumb (navigation), point-in-time facts (cache miss, circuit opened)
    public static void Mark(OperationDescriptor d, LogLevel level, OperationOutcome outcome = OperationOutcome.Ok, int code = 0);

    public static TraceContext Current { get; }                       // ambient (05 §1)
    public static void Install(IOperationEngine engine, bool allowReinstall = false);   // composition roots only (Phase 0: public, see 10 log)
    public static bool Uninstall(IOperationEngine engine);
}

/// Struct token. Safe to copy: Complete/Fail are idempotent (slot generation CAS), so a double Complete writes one record.
public readonly struct ActiveOperation
{
    public bool IsActive { get; }
    public void Complete(OperationOutcome outcome = OperationOutcome.Ok, int code = 0, OperationFlags flags = OperationFlags.None);
    public void Complete(OperationOutcome outcome, string code, OperationFlags flags = OperationFlags.None);   // code interned (cached per call site by generator)
    public void Fail(global::System.Exception error);                // Faulted + exception kept by reference in the slot
    public void Annotate(ushort field, long value);                  // late facts (rows affected, items count)
}
```

`ActiveOperation` is **not** a `ref struct`, because generated async code holds it across `await`. Generated code always uses the
`try/catch` shape (04 §2), never `using`, so a missed `Complete` cannot happen in generated code. Manual code uses
`log.Operation(...)`, which returns an `IDisposable`-shaped struct that ends as `Unknown` if you forget `Complete` (analyzer YFL0020
warns).

### 1.2 `ILog<T>` — manual logging (⚑L12)

```csharp
namespace YFex.Logging;

public interface ILog
{
    bool IsEnabled(LogLevel level);
    void Write(LogLevel level, [InterpolatedStringHandlerArgument("", "level")] ref LogHandler message);
    void Write(LogLevel level, global::System.Exception error, [InterpolatedStringHandlerArgument("", "level")] ref LogHandler message);
    OperationScope Operation(string name, LogLevel level = LogLevel.Information);   // ad-hoc span; name interned once per call site
    void Audit(string action, [InterpolatedStringHandlerArgument("")] ref AuditHandler details);   // Audit channel
}
public interface ILog<out TCategory> : ILog { }   // category = TCategory, index cached in a static generic

// Extension sugar (full level names, no abbreviations): log.Trace/Debug/Information/Warning/Error/Critical($"…")
```

Usage:

```csharp
public sealed class CatalogSync(ILog<CatalogSync> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var operation = log.Operation("Catalog.Sync");             // span + duration + outcome
        var count = await PullAsync(ct);
        log.Information($"Pulled {count} items from {source} in {sw.Elapsed}");   // zero-alloc; hole names = "count", "source", "sw.Elapsed"
        operation.Complete(OperationOutcome.Ok);
    }
}
```

**`LogHandler`** is a `ref struct` interpolated-string handler (the ZLogger technique):

- ctor `(int literalLength, int formattedCount, ILog log, LogLevel level, out bool shouldAppend)`: when disabled, `shouldAppend
  = false` and **the arguments are never evaluated**.
- `AppendLiteral(string s)`: stores the *reference* (literals are interned constants) in the slot's ref table. Nothing is copied.
  The consumer derives the template id by hashing the literal sequence once and caching it.
- `AppendFormatted<T>(T value, [CallerArgumentExpression(nameof(value))] string name = "")`: the hole name comes for free.
  Values dispatch through `typeof(T) == typeof(int)` checks (specialized away per instantiation by the JIT and ILC, so no
  boxing), then the constrained `value is IUtf8SpanFormattable` pattern (also no boxing for structs), and as a last resort
  `ToString()` on the caller (allocates; analyzer YFL0012).

### 1.3 `[Log]` partial methods (generator)

```csharp
public static partial class SefazLog
{
    [Log(LogLevel.Warning, "SEFAZ {Uf} rejeitou {Chave}: {Codigo} {Motivo}", Kind = "fiscal.sefaz")]
    public static partial void Rejeitada(this ILog log, UF uf, ChaveNFe chave, int codigo, string motivo);
}
// generated:  if (!log.IsEnabled(Warning)) return;  rent slot; header; w.WriteEnum(0, uf); w.Write<ChaveNFe>(1, chave); …; commit
```

Parameters marked `[Sensitive]` or whose type has `ILogValue<T>.Sensitivity >= SensitivePersonal` are redacted per policy. The
template is validated at compile time (YFL0001 placeholder/parameter mismatch).

### 1.4 Inbound bridges

- **`ILogger` → engine:** `YFexLoggerProvider : ILoggerProvider` (framework MEL abstractions, no package). It is how ASP.NET Core,
  ActualLab.Fusion, Npgsql and others reach the pipeline. `TState` that is `IReadOnlyList<KeyValuePair<string, object?>>` is
  read as structured fields. This path boxes, and that is accepted because it is not our hot path.
  `BeginScope` maps to an annotation frame.
- **`Activity` → engine:** an `ActivityListener` for configured sources (`Microsoft.AspNetCore`, `System.Net.Http`, `Npgsql`,
  …). It adopts their trace/span ids into the ambient context and turns stopped activities into `OperationEnd` records (kind
  `HttpServer`/`HttpClient`/`Sql`).

### 1.5 Passing whole objects: how this differs from `Microsoft.Extensions.Logging`

MEL lets you pass any object: `logger.LogInformation("Venda {Venda} finalizada", venda)`. This is what happens under the hood:

| Step in MEL | Cost / risk |
|---|---|
| `params object?[] args` | allocates an array on **every** call, even when the level is disabled (unless you add `IsEnabled` checks by hand) |
| value types (`decimal`, `Guid`, `Money`, structs) | **boxed** into the array |
| default providers call `venda.ToString()` | records print **every member**: `Venda { Id = V-88123, Cliente = 123.456.789-09, … }`, so the **CPF leaks** |
| Serilog `{@Venda}` destructuring | reflection over properties (not AOT/trim-safe, slow), and the CPF still leaks |
| `[LoggerMessage]` generator | fixes boxing for primitives, but a complex object is still passed as `object` and `ToString()`-ed |

**YFex.Logging accepts objects too**, through the interpolated handler, without those costs:

```csharp
log.Information($"Venda finalizada {venda}");            // venda : VendaDto (a record)
```

1. `AppendFormatted<T>(T value, …)` is generic, so nothing is boxed.
2. It looks up `LogShapeFor<T>.Write`, a static generic slot (one static load, no dictionary, no reflection), filled by the
   generator's module initializer.
3. The **log shape** is generated for every record/class the logging generator sees used in a log call, for every CQRS
   message/event/DTO, and for every `[Observable]` view model. It writes each member with its typed writer and applies
   `[Sensitive]` from the member's type.
4. The result is structured fields (`venda.id=V-88123 venda.total=R$ 149,90 venda.cliente=#h:9f2c… venda.itens.count=4`),
   not a string. In `.jsonl` and on the server they are filterable.

```csharp
// (sketch) generated for VendaDto
file static class __VendaDtoLogShape
{
    [ModuleInitializer] internal static void Register() => global::YFex.Diagnostics.LogShapeFor<VendaDto>.Write = Write;

    static void Write(in VendaDto v, ref global::YFex.Diagnostics.LogWriter w, ushort prefix)
    {
        w.Write<VendaId>(w.Field(prefix, "id"), v.Id);
        w.WriteRawValue<global::YFex.Money>(w.Field(prefix, "total"), v.Total);
        if (v.Cliente is { } c) w.Write<Cpf>(w.Field(prefix, "cliente"), c);            // redacted (Cpf is [Sensitive])
        w.Write(w.Field(prefix, "itens.count"), v.Itens.Count);                          // collections: count only by default
    }
}
```

Rules for shapes: depth limit 2, collections as `count` (opt-in first N with `[LogItems(5)]`), `byte[]`/streams as length,
cycles cut, `Secret`/`Financial` members erased. A type with no shape (from a 3rd-party assembly) falls back to
`IUtf8SpanFormattable`/`ISpanFormattable`, and only as a last resort to `ToString()` on the caller (analyzer YFL0012).

## 2. Producer fast path (what one enabled call costs)

```
1. level = descriptor.Level (or call level)
2. if ((byte)level < s_threshold[d.Index]) → recorder check (§5) or return           // one byte load
3. if (s_hasScopedOverrides) → scoped override lookup (§4.2)                          // volatile bool, normally false
4. slot = pool.TryRent()            → exhaustion strategy if null (§3.2)
5. write header (timestamp, seq = Interlocked.Increment, trace ctx, thread id)
6. scope: n = scopeContext.Resolve(stackalloc ScopeKey[8]) → copy n × (rank, valueId)
7. fields: descriptor.Capture(in msg, ref writer) / handler appends
8. queue.TryEnqueue(slot.Index) + signal consumer if it parked
```

## 3. Slots, pool and queue (ZeroLog technique, own code)

### 3.1 Structures (all allocated once at startup)

- `LogSlot` (pooled class): `byte[] Buffer` (default 512 B), `Exception? Error`, `object?[] Refs` (8, for literal/name refs),
  `int Generation`, `int State`. A second pool of **large slots** (default 8 KiB × 256) handles records that overflow a small one.
  The writer upgrades transparently, once.
- **Free list** and **work queue**: two bounded lock-free rings of `int` slot indexes (Vyukov MPMC algorithm, power-of-two
  capacity, sequence-stamped cells → ABA-safe, no allocation, no locks).
- Default budget: 16 384 × 512 B = **8 MiB** (servers). Profile `Device` = 2 048 × 512 B = 1 MiB (POS, tablets).

### 3.2 Exhaustion strategies (per channel, NLog/ZeroLog vocabulary)

| Strategy | Behaviour | Default for |
|---|---|---|
| `DropNew` | Return immediately; `dropped[reason]++`; the consumer later emits one "N records dropped" Warning | Diagnostic |
| `DropBelow(level)` | Drop if below level, otherwise wait up to N µs | Diagnostic on servers |
| `WaitFor(timeout)` | Spin then park up to timeout, then DropNew | — |
| `Allocate` | Use a temporary unpooled slot (counted; for bursts at startup) | Development profile |
| `WriteThrough` | Encode directly into the durable audit spool on the caller thread (never lost) | **Audit** |

### 3.3 Consumer

- One dedicated background `Thread` started under `ExecutionContext.SuppressFlow()`, so no ambient tenant/trace leaks into it.
- Loop: drain up to `BatchSize` (default 1024) records → process → hand batches to sinks → return slots → park on an
  `AutoResetEvent` with a `FlushInterval` timeout (default 200 ms; 0 for tests in synchronous mode).
- The producer only signals when the consumer is parked (one volatile read), so a busy consumer costs producers nothing.

## 4. Filtering

### 4.1 Static thresholds (compiled at config time into one byte array)

`threshold[descriptor.Index] = min(global, kind override, module/category override, descriptor override)`, recomputed whenever
config changes (copy-on-write array swap). Producers read **one byte**. There are two values per descriptor: **emit level**
(goes to sinks) and **record level** (≤ emit, goes to the flight recorder only).

### 4.2 Scoped dynamic overrides ("debug tenant acme for 30 min")

`Operations` holds `volatile bool s_hasScopedOverrides`. When true, producers resolve the current scope chain (already on the stack
from step 6), principal and session, then probe a `FrozenDictionary<long, LogLevel>` keyed by `(kind of key, interned id)`.
Overrides can only make logging **more verbose**. Each has a TTL and expires on its own. They are pushed from the server (08 §6).

### 4.3 Consumer-side volume control

- **Tail sampling** per kind: e.g. keep 10% of successful queries, **100% of failures and slow operations** (`> p99` or explicit
  threshold). Metrics are updated before sampling.
- **Rate limit per EventId** (token bucket). Once exceeded, a single summary record is emitted every interval: "suppressed 12 431
  × `Vendas.Consultar` in 60 s".
- **Per-tenant quotas** (records/min, bytes/day). A noisy tenant cannot evict another tenant's records.
- **Consecutive duplicate folding** (same EventId + same fields hash within 1 s → counter).

## 5. Flight recorder (tail-based retroactive logging)

You want to know everything about a failure without paying to store everything about successes. The recorder handles this:

- Records with `record level ≤ level < emit level` (e.g. Debug when emitting Information) are encoded as usual but routed to a
  **per-trace ring** (default 64 records) in an open-addressing table keyed by `TraceId` (bounded: 4 096 traces, LRU).
- When the trace's root operation ends **Ok and fast**, its ring is discarded. When any record of the trace is `≥ Error`, ends
  `Faulted`/`TimedOut`, or exceeds its slow threshold, the ring is **flushed to sinks** in order, flagged `OperationFlags.Retro`.
- A **global ring** (last 2 048 records of any trace) is flushed on `Critical` and on crash. That gives you "the last 2 seconds
  before the process died".
- A **breadcrumb ring per session** (navigation, UI actions, commands; `OperationDescriptorFlags.Breadcrumb`) is attached to every
  issue occurrence (08 §4).
- Cost: encoding below-emit records still costs the producer path. The recorder can be switched off per kind (default on for
  Command/Query/Rpc*/Sql/Job/SagaStep, off for StateCommand/Validation).

## 6. Enrichment (consumer thread)

1. Resolve process-local ids to strings: scope values (`ScopeValueInterner`), principal, session, descriptor, codes.
2. Exceptions: type, **scrubbed** message (07 §3.3), frames via `StackTrace` (Native AOT keeps method names when
   `StackTraceSupport=true`, but has no file/line, see 09 §1.12), inner/aggregate flattening, `Exception.Data` only for
   allow-listed keys. **Fingerprint** = `StableHash64(type + top 5 in-app frames + operation descriptor name)`.
3. Render message templates only for sinks that need text (console/text/jsonl). Binary sinks keep fields.
4. Assign the file/batch string-table ids (02 §9).

## 7. Sinks

```csharp
public interface ILogSink
{
    string Name { get; }
    SinkFilter Filter { get; }                                   // channel(s), min level, kinds, tenants
    void Write(in RecordBatch batch);                            // ref-struct view over consumer buffers; no copies
    ValueTask FlushAsync(CancellationToken ct);
}
```

- Fast in-memory sinks (memory ring, live tap) run on the consumer thread. **I/O sinks get their own bounded queue and worker**,
  so a slow network sink never stalls console or file. Each I/O sink has a circuit breaker, bounded retry and a dead-letter
  counter.

| Sink | Package | Notes |
|---|---|---|
| Console | Logging | ANSI colours, UTF-8 straight to the stdout `Stream` (no `Console.WriteLine`), compact or verbose layout |
| RollingFile | Logging | `.ylog` (binary), `.jsonl`, `.txt` (LoggY legacy format). Roll by size/time, retention by count/age/total bytes, per-app subfolder |
| Memory | Logging | Ring of N records. In-app "diagnostics" screen, tests |
| LiveTap | Logging | In-process subscription (Avalonia diagnostics panel) + named pipe for LoggY local mode |
| EventSource bridge | Logging | Optional `EventSource "YFex-Logging"` so `dotnet-trace`/PerfView see records |
| Spool → Server | Shipping | Durable segment files + batch shipper (05 §6, 08 §2) |
| OTLP | Otlp | OTLP/HTTP protobuf: logs + traces (from operation records, no `Activity` allocations) + metrics |
| Audit store | Audit | Hash-chained, write-through (07 §2) |

## 8. Metrics (derived automatically, never sampled)

- Per descriptor: count by outcome (`long[desc × 9]`) and a log-linear latency histogram (64 buckets), both updated on the
  **producer at `Complete`** with `Interlocked` operations. This happens even when the record is filtered out, as long as metrics are
  enabled for the kind.
- Export: BCL `Meter "YFex.Logging.Operations"` observable instruments (pull model, zero hot-path cost), and `MetricSnapshot` records
  every 10 s to the server (RED dashboards, SLOs, latency alerts).
- Dimensions: operation name, kind, outcome, optionally the **top-level** tenant rung. **Never** user/session ids (cardinality, 09 §1.7).

## 9. Self-observability

`Meter "YFex.Logging"`: produced, dropped by reason, queue depth (max), pool exhaustion, sink latency/failures/breaker state,
spool bytes/age, recorder flushes, rate-limited counts. A **heartbeat** `Node` record every 30 s carries these and feeds the
server's node-health view and the "missing heartbeat" alert. Pipeline errors go to the self-log (stderr, Windows Event Log),
never through the pipeline.

## 10. Crash and shutdown

Hooks: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, Avalonia `Dispatcher.UIThread` unhandled
(via YFex.Avalonia), `ProcessExit`, `IHostApplicationLifetime.ApplicationStopping`. On crash: write a `Critical` record
synchronously (write-through), flush the global recorder ring, drain the queue with a deadline (default 2 s), `fsync` the spool.
The next start ships it ("late critical", 08 §5).

## 11. Performance budgets (targets; P1 benchmarks must prove them)

| Path | Target |
|---|---|
| Disabled check (`IsEnabled`, `LogHandler` when off) | ≤ 1 ns, **0 B** |
| Enabled message, 3 primitive holes | ≤ 60 ns caller, **0 B** |
| `Operations.Start/Complete` record-only (no span) | ≤ 80 ns, **0 B** |
| `Operations.Start/Complete` with ambient span | ≤ 150 ns, ≤ 1 small allocation (`AsyncLocal` write, see 09 §1.3) |
| Consumer → null sink / → `.ylog` on SSD | ≥ 2 M rec/s / ≥ 500 k rec/s |
| Steady-state managed allocations over 1 M enabled records | 0 B on producer threads |
