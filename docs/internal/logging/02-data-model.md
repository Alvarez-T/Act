# 02 — Data model

All types in §1–§6 live in **YFex core, namespace `YFex.Diagnostics`** (⚑L1) unless noted. They are small, blittable where
possible, and have no dependencies.

## 0. Vocabulary (used everywhere; no abbreviations in type names, ⚑L15)

| Term | Meaning | Example | OpenTelemetry name |
|---|---|---|---|
| **Trace** | Everything caused by **one root action**, across threads, processes and machines. One `TraceId` | one click on "Finalizar", one HTTP request, one job run | trace |
| **Operation** | A **timed unit of work** inside a trace: it has a start, an end, an outcome and a parent operation. One `OperationId` | `Venda.Finalizar` on the server, one SQL statement, one saga step | span |
| **Root operation** (also **activity** in the viewer) | The first operation of a trace (no parent). What a user or system *did* | `Caixa.Finalizar (ui)` | root span |
| **Mark** | An **instant fact** inside an operation (no duration) | circuit opened, cache miss, property changed, navigation breadcrumb | span event |
| **Record** | Anything written to the pipeline: a message, an operation end, a mark, a metric snapshot | one line in the console | log record |
| **Operation descriptor** | Compile-time metadata of an operation type (name, kind, fields), generated once | descriptor of `FinalizarCommand` | instrumentation scope + span name |
| **Channel** | Diagnostic, Audit, Metric (different loss/retention guarantees) | | — |

## 1. Levels, channels, outcomes (closed sets: the engine owns them)

```csharp
namespace YFex.Diagnostics;

/// Same numeric values as Microsoft.Extensions.Logging.LogLevel, so the bridge is a cast (⚑L7).
public enum LogLevel : byte { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5, None = 6 }

/// Where a record goes. Audit is NOT a level: an audit entry has a level too (a denied login is Warning + Audit).
public enum LogChannel : byte { Diagnostic = 0, Audit = 1, Metric = 2, Control = 3 }

/// How an operation ended. Detail goes in OutcomeCode (interned string id, e.g. "VAL.REQUIRED", "SEFAZ.539").
public enum OperationOutcome : byte
{
    Unknown = 0,   // still open / orphaned / Dispose without Complete
    Ok,            // success (cache provenance goes in OperationFlags)
    Queued,        // accepted offline (IQueueable → outbox)
    Rejected,      // validation / rule / business Error result (expected failure)
    Denied,        // authorization / tenancy gate
    Conflict,      // concurrency (ES version, sync conflict)
    Cancelled,     // CancellationToken
    TimedOut,      // timeout policy
    Faulted,       // unexpected exception
}

[Flags]
public enum OperationFlags : ushort
{
    None = 0, CacheHit = 1, Stale = 2, Offline = 4, Retried = 8, Replayed = 16 /* outbox/ES replay */,
    Compensated = 32, Sampled = 64, Retro = 128 /* emitted later by the flight recorder */, Truncated = 256,
}
```

## 2. Operation kinds (open set, well-known ids)

The kind drives default level, span policy, metrics and the viewer's icon. **Well-known kinds** have fixed ids
`1..63` so filters are a fixed-size array. Apps and modules register their own (`OperationKind.Register("fiscal.sefaz")` → id ≥ 64,
process-local; the **name** is what ships).

```csharp
public readonly record struct OperationKind(ushort Id)
{
    // Messaging / CQRS
    public static readonly OperationKind Command = new(1), Query = new(2), EventPublish = new(3), EventHandle = new(4);
    // Boundaries
    public static readonly OperationKind RpcClient = new(5), RpcServer = new(6), HttpClient = new(7), HttpServer = new(8);
    // UI
    public static readonly OperationKind Navigation = new(9), StateCommand = new(10), UiAction = new(11);
    // Decisions
    public static readonly OperationKind Validation = new(12), RuleEvaluation = new(13), Authorization = new(14);
    // Data
    public static readonly OperationKind Persistence = new(15), Sql = new(16), EventAppend = new(17), EventRead = new(18),
                                  Projection = new(19), Cache = new(20);
    // Offline
    public static readonly OperationKind OutboxEnqueue = new(21), OutboxReplay = new(22), Sync = new(23);
    // Long-running
    public static readonly OperationKind Job = new(24), Saga = new(25), SagaStep = new(26), ProcessManager = new(27),
                                  Compensation = new(28);
    // Platform
    public static readonly OperationKind Device = new(29), AutomationStep = new(30), CustomLogic = new(31), Resilience = new(32);
    public static readonly OperationKind Custom = new(63);

    public static OperationKind Register(string name);              // idempotent, thread-safe, returns id >= 64
    public static string NameOf(OperationKind kind);                // "cqrs.command", … (consumer side)
}
```

## 3. Operation descriptors (compile-time metadata, registered once)

Generators emit one descriptor per message / route / rule set / saga / job / `[Log]` method and register it from a
`[ModuleInitializer]`. Nothing on the hot path touches strings.

```csharp
public enum OperationDescriptorFlags : ushort
{
    None = 0,
    Audit = 1,          // every end record is ALSO written to the Audit channel (e.g. commands of an audited aggregate)
    LongRunning = 2,    // emit a Start record too (jobs, sagas): in-progress operations visible, orphan detection
    Boundary = 4,       // starts a span even at Boundaries granularity (RPC, HTTP, command, job)
    Leaf = 8,         // record-only (validation per field, property change)
    Breadcrumb = 16,    // kept in the per-session breadcrumb ring (navigation, UI actions)
}

public readonly record struct FieldSpec(string Name, DataSensitivity Sensitivity);

public abstract class OperationDescriptor
{
    public int Index { get; }                   // dense process-local index → per-operation level/counter/histogram arrays
    public uint EventId { get; }                // stable: (uint)StableHash64(Name) unless [LogId(n)] — collisions self-logged
    public OperationKind Kind { get; }
    public string Name { get; }                 // "Vendas.FinalizarVenda", "Route:/vendas/{id}", "RuleSet:desconto"
    public string Module { get; }               // assembly / module name
    public string? Template { get; }            // message template for [Log] methods, else null
    public LogLevel Level { get; }              // default level (overridable by config at run time)
    public OperationDescriptorFlags Flags { get; }
    public ReadOnlySpan<FieldSpec> Fields { get; }
}

/// Typed descriptor: knows how to capture T's opted-in fields without boxing.
public sealed class OperationDescriptor<T> : OperationDescriptor
{
    public delegate void CaptureFn(in T value, ref LogWriter writer);
    public CaptureFn? Capture { get; }          // null = shape only (⚑L8)
}

/// Static generic slot: the dispatcher finds the descriptor for TCommand with one static load.
public static class OperationDescriptorFor<T>
{
    public static OperationDescriptor<T>? Descriptor;  // set by the generated module initializer; fallback = lazy from type name
}
```

## 4. Trace context (W3C-compatible, own structs)

```csharp
public readonly struct TraceId : IEquatable<TraceId>      // 16 bytes, two ulongs
{
    public static TraceId NewRandom();                    // RandomNumberGenerator-free: thread-local xoshiro seeded once
    public bool IsEmpty { get; }
    public int FormatHex(Span<char> dst32);               // no alloc
    public static bool TryParseHex(ReadOnlySpan<char> s, out TraceId id);
    public global::System.Diagnostics.ActivityTraceId ToActivity();   // bridge, no alloc
    public static TraceId From(global::System.Diagnostics.ActivityTraceId id);
}
public readonly struct OperationId : IEquatable<OperationId> { /* 8 bytes, same API */ }

[Flags] public enum TraceFlags : byte { None = 0, Sampled = 1 /* W3C */, Recording = 2 /* local: flight recorder on */ }

public readonly record struct TraceContext(TraceId TraceId, OperationId OperationId, OperationId ParentOperationId, TraceFlags Flags)
{
    public int WriteTraceParent(Span<char> dst55);        // "00-{trace}-{span}-{flags}"
    public static bool TryParseTraceParent(ReadOnlySpan<char> s, out TraceContext ctx);
}

/// A causal link that is NOT a parent (offline replay, ES catch-up, batch fan-in). See 05 §4.
public readonly record struct OperationLink(TraceId TraceId, OperationId OperationId, LinkKind Kind);
public enum LinkKind : byte { FollowsFrom = 0, Replay = 1, Batch = 2, Retry = 3 }
```

## 5. Field values and data classification

### 5.1 Writing values without boxing

`LogWriter` is a `ref struct` over the slot's byte span. Every overload is generic-constrained or primitive, so nothing boxes.
On overflow it sets `OperationFlags.Truncated` and silently stops. It never throws.

```csharp
public ref struct LogWriter
{
    public void Write(ushort field, int v);  /* long, ulong, double, decimal, bool, Guid, DateTime, DateTimeOffset, TimeSpan */
    public void Write(ushort field, ReadOnlySpan<char> v);           // UTF-16 memcpy, capped (default 1 KiB), transcoded on consumer
    public void Write(ushort field, string? v);
    public void WriteRaw(ushort field, in global::YFex.RawValue v);  // core RawValue (Money/Percentual/Quantity/… via IRawValue<T>)
    public void Write<T>(ushort field, in T v) where T : ILogValue<T>;
    public void WriteRawValue<T>(ushort field, in T v) where T : global::YFex.IRawValue<T>;   // v.ToRaw() – struct, no box
    public void WriteEnum<TEnum>(ushort field, TEnum v) where TEnum : unmanaged, global::System.Enum; // underlying + EnumTable id
    public void WriteFormatted<T>(ushort field, in T v) where T : global::System.IUtf8SpanFormattable; // formats on caller (cost!)
    public void WriteRedacted(ushort field, DataSensitivity cls);          // records "a value existed" without the value
}

/// How a value object writes itself. Normally GENERATED from [LogValue] on the struct (11 §3); hand-written when you need
/// full control. Static abstract → no boxing, no virtual call.
public interface ILogValue<TSelf> where TSelf : ILogValue<TSelf>
{
    static abstract void Write(in TSelf value, ref LogWriter w, ushort field);
    static virtual DataSensitivity Sensitivity => DataSensitivity.Internal;      // Cpf → SensitivePersonal
    static virtual bool IsSearchKey => false;                                    // VendaId, ChaveNFe → true (13 §4)
}

/// Placed ON THE VALUE OBJECT (once), never on every member that uses it.
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class LogValueAttribute : Attribute
{
    public bool SearchKey { get; init; }        // index it on the server: "find everything that touched V-88123"
    public string? Format { get; init; }        // log-specific format, e.g. "R" or a custom pattern
}
```

Resolution order the generators use for a captured member of type `T`: `ILogValue<T>` → `IRawValue<T>` → primitive/string/enum
overload → `IUtf8SpanFormattable` (flagged info diagnostic YFL0010: "formats on caller") → a **generated log shape** for records
and classes (03 §1.5) → **not capturable** (YFL0011 warning).

### 5.2 Data sensitivity (general purpose, not log-only, ⚑L14)

Sensitivity is a property **of the data**, so it is declared once on the type that carries it and is used by every library
that handles data. That is why the attribute is `[Sensitive]` and not `[SensitiveLog]`. It lives in **YFex core,
namespace `YFex.Privacy`** (not `YFex.Diagnostics`):

| Consumer | What it does with `[Sensitive]` |
|---|---|
| YFex.Logging | redact per policy (mask / hash / encrypt / erase) |
| YFex.UI / Avalonia | mask in grids and read-only views (`***.456.789-**`), reveal on permission |
| YFex.Persistence.Encryption / Data | encrypt at rest by default for `SensitivePersonal`+ |
| YFex.Brain / Automation / Capabilities (AI) | strip or pseudonymize before sending to an LLM |
| LGPD tooling | data inventory ("where do we store CPF?"), subject export and erasure (07 §4) |
| Validation JSON Schemas | emit `x-sensitivity` so AI/forms tooling treats the field correctly |

```csharp
namespace YFex.Privacy;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SensitiveAttribute(DataSensitivity level) : Attribute
{
    public DataSensitivity Level { get; } = level;
    public string? Mask { get; init; }          // display/log mask, e.g. "***.###.###-**" ('#' = keep char, '*' = hide)
}

// Normal use: on the value object, once.
[Sensitive(DataSensitivity.SensitivePersonal, Mask = "***.###.###-**"), LogValue]
public readonly partial record struct Cpf { … }

// Exception: a raw primitive member that cannot be a value object.
public partial record ImportarClienteCommand(string Codigo, [Sensitive(DataSensitivity.Personal)] string NomeCompleto) : ICommand;
```

```csharp
public enum DataSensitivity : byte
{
    Public = 0,             // product codes, NCM, CFOP, counts
    Internal = 1,           // ids, amounts, tenant ids (default)
    Personal = 2,           // name, e-mail, phone, address
    SensitivePersonal = 3,  // CPF, RG, health, biometrics (LGPD art. 5 II)
    Financial = 4,          // card PAN, bank account: NEVER logged, even hashed
    Secret = 5,             // passwords, tokens, API keys, certificates: NEVER logged
}
```

The default policy per class (`Keep`/`Mask`/`Hash`/`Encrypt`/`Erase`) is configurable (07 §3). `Financial` and `Secret` are
hard-wired to `Erase`, and config cannot relax them.

## 6. Node identity (sent once per connection / file, never per record)

```csharp
public sealed record NodeIdentity(
    string App, string Version, string Environment,        // "farma-pdv", "1.4.2+sha", "prod"
    Guid InstanceId,                                       // stable per install (persisted), survives restarts
    string Host, int ProcessId, DateTimeOffset StartedAt,
    NodeKind Kind, string? Region, string? DeviceId);       // DeviceId joins YFex.Profiling's HardwareFingerprint
public enum NodeKind : byte { Server = 0, Desktop = 1, Mobile = 2, Browser = 3, Worker = 4, Device = 5 }
```

## 7. The record (engine-internal layout, `YFex.Logging`)

One **record** per message, per operation end (it carries start and duration, so begin+end costs one record), per breadcrumb,
per metric snapshot. Start records are only emitted for `LongRunning` operations.

```csharp
// (sketch) fixed 80-byte blittable header, followed by variable sections in the same slot
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RecordHeader
{
    public ushort Size; public byte Version; public LogChannel Channel;
    public LogLevel Level; public RecordKind Kind;          // Message, OperationStart, OperationEnd, Mark, MetricSnapshot, Node
    public OperationOutcome Outcome; public byte Reserved;
    public OperationFlags Flags; public ushort FieldCount;
    public long TimestampUtcTicks;                          // DateTime.UtcNow.Ticks at record time (begin for OperationEnd)
    public long DurationTicks;                              // Stopwatch-based, OperationEnd only
    public ulong Sequence;                                  // per node, monotonic: server detects gaps / loss
    public TraceId TraceId; public OperationId OperationId; public OperationId ParentOperationId;
    public int DescriptorIndex;                             // or category index for plain messages
    public int OutcomeCodeId;                               // interned code string
    public int PrincipalId; public int SessionId;           // interned (consumer resolves)
    public int ThreadId;
}
// variable: [scope count u8][(rank u8, valueId i32) × n]  — from IScopeContext.Resolve(stackalloc ScopeKey[8])
//           [fields: (fieldId u16, LogValueKind u8, payload) × FieldCount]
//           [links: count u8, OperationLink × n]
// object refs (Exception) live in the pooled slot object, never in the byte span.
```

## 8. Event ids & semantic conventions

`EventId` is stable across releases (hash of name), so dashboards and alert rules survive refactors that keep names. Renames
are explicit (`[LogId(0x1234)]` or `[LogName("Old.Name")]` on the message).

| Our attribute | OTLP / semconv mapping |
|---|---|
| Node `App`/`Version`/`InstanceId`/`Environment`/`Host`/`ProcessId` | `service.name`, `service.version`, `service.instance.id`, `deployment.environment.name`, `host.name`, `process.pid` |
| `yfex.operation.kind` / `yfex.operation.name` / `yfex.operation.outcome` / `yfex.operation.code` | span name = operation name; `error.type` = code when not Ok |
| tenant scope (canonical `ScopeIdentity` text) | `yfex.tenant` (+ `yfex.scope.<rank>` optional) |
| principal / session / device | `enduser.id`, `session.id`, `device.id` |
| RPC | `rpc.system=fusion`, `rpc.service`, `rpc.method` |
| SQL | `db.system.name`, `db.operation.name`, `db.query.summary` (never the parameters) |
| exception | `exception.type`, `exception.message` (scrubbed), `exception.stacktrace` |
| issue | `yfex.issue.fingerprint` |

## 9. `.ylog` — binary file & wire format (`YFex.Logging.Format`)

The same framing is used for local rolling files, the durable spool and the HTTP batch to the server. It is append-only and
self-describing, and a crash loses at most the torn tail frame.

```
file   = "YLOG" u16 version u16 flags  NodeFrame  { frame }
frame  = u8 type  u32 length  u32 crc32c  payload
types  : 1 Node (NodeIdentity TLV)
         2 Strings (delta: u32 id, u16 len, utf8 bytes)…  — descriptor names, field names, codes, tenant values, principals
         3 Records (RecordHeader + sections, strings by id, text fields UTF-8)
         4 Checkpoint (last sequence, audit chain hash)    — 07 §2
         5 Descriptors (index → name/kind/module/eventId/fields) so a reader needs no app assemblies
```

- **Process-local ids never leave the process unresolved.** `ScopeValueInterner` ids, descriptor indexes and interned codes are
  re-mapped into the file/batch string table by the consumer (pitfall 09 §1.10).
- Text fields are UTF-8 in files (consumer transcodes the UTF-16 copied by the producer).
- Reader is versioned: the server accepts N-2 format versions (rolling client upgrades, offline devices).
- A JSON-lines sink (`.jsonl`) exists for humans/grep. A text sink keeps LoggY's legacy line format for the existing parser.
