# 10 — Phases & acceptance

Order principle: **contract → engine → implicit wave 1 → shipping → server → audit → alerts → wave 2 → UI last**. Each phase
ends with green tests, a native-AOT smoke publish with zero warnings, and a log section appended to this file (deviations,
gotchas), the same protocol as the validation design.

| Phase | Scope | Loads | Acceptance |
|---|---|---|---|
| **0 — Contract** (⚑L1, ⚑L7, ⚑L12 confirmed) | YFex core `YFex.Diagnostics`: `LogLevel`, `LogChannel`, `OperationOutcome`, `OperationFlags`, `OperationKind`, `OperationDescriptor(<T>)`, `OperationDescriptorFor<T>`, `OperationRegistry`, `Operations` (no-operation), `ActiveOperation`, `TraceId/OperationId/TraceContext/OperationLink`, `LogWriter`, `ILogValue<T>`, `DataSensitivity`, attributes (§04-3). `ILogValue` on core value types (Money, Percentual, Quantity, Mass, Length, FieldPath) | README, 02, 04 §2–§3 | `Operations.*` with no engine: 0 B and ≤ 1 ns (benchmark); W3C parse/format round-trip tests; core still has no dependencies |
| **1 — Engine** (⚑L2, ⚑L3) | repo `YFex.Logging`: slot pool + Vyukov rings, consumer, thresholds, scoped overrides, flight recorder, enrichment, Console/RollingFile(.ylog/.jsonl/.txt)/Memory/LiveTap sinks, `ILog<T>` + `LogHandler`, `[Log]` generator, MEL provider bridge, Activity bridge, Meter export, crash hooks, fluent DI + config binding, profiles, `YFex.Logging.Format` reader/writer, `YFex.Logging.Testing`, benchmarks, AotSmoke | 02, 03, 06 | 03 §11 budgets met; 1 M records → 0 B on producers; `.ylog` survives `kill -9` (torn-tail test); config reload swaps thresholds under load without loss |
| **2 — Implicit wave 1** | Cqrs `LoggingStage` + generator descriptors/captures/`.Logging` parsing; Messaging `[Subscribe]`/bus/`[Live]`; Rpc proxies + `CallContextMiddleware` propagation + `TracingHandlerInvoker` + outbox context/links; State `[StateCommand]` + `StateChangeRole` inference + setter hooks + `IUndoHistoryObserver` seam and edit ring (12); NavigatR; console `Layout.Tree` (13 §4); Validation/Rules annotations; shared `DiagnosticsEmit.cs` | 04, 05 §1–§4 | doc-12 sale scenario produces the 04 §6 trace (client+server, one trace id across the in-memory RPC bridge of `Messaging.Rpc.Tests`); offline replay test shows a `Replay` link; generator golden + caching tests per generator; existing test counts unchanged |
| **3 — Shipping & correlation** | spool, two-lane shipper, idempotency, clock offset, relay mode, YFex.Web `TracingHandler`, ASP.NET Core middleware + trust policy, control channel client | 05 | network-cut chaos test: no loss of Audit/≥Error, bounded Diagnostic loss counted; relay forwards with original node identity |
| **4 — Server core** (⚑L4, ⚑L5) | ingest + auth + quotas, `ILogStore` (Postgres partitions, SQLite; `IBulkWriter` in YFex.Data; row security; 14), query messages, search language, `activities` + `search_keys` + support codes + `yfex-log` CLI (13), live tail, issues/fingerprints/regressions, retention jobs, OTLP receiver + in-house OTLP exporter | 08 §1–§4, §7 | 50 k rec/s ingest on a dev box (Postgres); tenant isolation tests (a tenant admin never sees another tenant); OTLP round-trip to the Aspire dashboard |
| **5 — Audit** (⚑L11) | Audit channel, write-through, hash chain + anchors, adapters (`IDecisionAuditSink`, `IRuleAuditSink`, `ITenantImpersonationAuditor`, Authorization, Payment), crypto-shredding, retention tiers | 07 | tamper test (edit one row → verification fails at the right index); erasure test (key deleted → field unreadable, chain still valid); audit gap → Critical incident |
| **6 — Alerts & control** (⚑L6, ⚑L10) | evaluator, rule types, incidents, routing tables, escalation, digests, notifiers (Telegram, Slack/Discord/Teams, SMTP, ntfy, webhook), ack links, remote scoped-debug grants with TTL | 08 §5–§6, 06 §4 | crash loop on 300 simulated nodes → 1 incident, 1 notification + escalation; TTL grants auto-expire; every grant audited |
| **7 — Implicit wave 2** | EventSourcing, Orchestration, Jobs observer adapter, Resilience, Persistence, Data (`TracingDbConnection`, QueryBuilder), Payment migration, Devices, Automation, CustomLogic, Search, Avalonia hooks | 04 §5 | each library: golden generated output, an end-to-end trace test, no new reference to `YFex.Logging` |
| **8 — LoggY v2 (UI last)** | Avalonia console: live, search, waterfall, issues, incidents, nodes, tenants, audit explorer, control; local `.ylog` mode | 08 §8 | dogfooding: LoggY is instrumented and visible in itself |

### Open questions to settle before Phase 0

1. ⚑ decisions still open in the README (L1–L3, L5–L17 except confirmed ones, L20).
2. ✅ **Alert recipients / quiet hours** (2026-10-01): nothing hard-coded. Contacts, teams, schedules and routing are data;
   channels are plug-ins (`INotificationChannel`, `ISmsProvider`), with e-mail/Telegram/webhooks/ntfy built in and SMS/WhatsApp as
   seams (08 §5.3).
3. ✅ **First app** (2026-10-01): none yet. **Library-first.** Validation happens through `samples/` (an AotSmoke app, a
   sample desktop + server pair using Cqrs/Rpc/State) and the test suites, not a production app.
4. ✅ **Volume** (2026-10-01): unknown, so **start small, extensible**. Defaults sized for a single Postgres (or SQLite) box;
   quotas configurable; partitioning and the `ILogStore` seam keep the ClickHouse/Timescale path open (14).
5. ✅ **Hosting** (2026-10-01): deliver **only the libraries** needed to host the server (`AddYFexLoggingServer`,
   `UseYFexLoggingServer` endpoints, store, alerts) inside any ASP.NET Core app, plus a minimal `samples/YFex.Logging.Server.Host`.
   No infrastructure/deployment work now.

## Implementation log

### Decisions (2026-10-01, start of implementation)

All open ⚑L decisions confirmed by the user **as recommended**: L1, L2, L3, L5, L6, L7, L8, L9, L10, L12, L13 (minus
`[LogValue(DataSensitivity, Mask)]` + `LogMasks`: masks belong to YFex.Privacy), L15, L16, L17, L21 (BenchmarkDotNet 0.15.8 in
`benchmarks/` only). L11 delegated to the YFex.Audit design (L20). **L14:** `[Sensitive]` is general purpose and is **created by
the YFex.Privacy session**, not by logging. Logging creates no sensitivity type and adds sensitivity to `FieldSpec`/`LogWriter` once
`YFex.Privacy` lands in core (privacy proposal: 7 values incl. `PersonalIdentifier`, attribute argument `Sensitivity`,
`ILogValue<T>.Sensitivity` replaced by `IClassifiedData<T>`).

### Phase 0 — contract (2026-10-01) ✅

**Done:** `YFex/src/YFex/Diagnostics/` (namespace `YFex.Diagnostics`, no new dependency in core):

| File | Types |
|---|---|
| `LogLevel.cs` | `LogLevel` (MEL values), `LogChannel` (`Audit` = correlation marker only) |
| `OperationOutcome.cs` | `OperationOutcome`, `OperationFlags`, `OperationDescriptorFlags` (+ `Disabled` for `[NoLog]`) |
| `OperationKind.cs` | well-known ids 1–34 + 63, `Register`/`TryFind`/`NameOf`/`Count` (copy-on-write name table) |
| `TraceId.cs` | `TraceId`, `OperationId` (hex, bytes, Activity bridge, support code), per-thread xoshiro256** |
| `TraceContext.cs` | `TraceFlags`, `TraceContext` (W3C `traceparent` write/parse, `CreateChild`, `NewRoot`), `OperationLink`, `LinkKind` |
| `OperationDescriptor.cs` | `FieldCapture`, `FieldSpec`, `OperationDescriptor`, `OperationDescriptor<T>`, `OperationCapture<T>`, `OperationDescriptorFor<T>` (+ lazy fallback) |
| `OperationRegistry.cs` | dense indexes, name/event-id lookup, collisions list, `Registered` event, `RegisterMessage` |
| `Operations.cs` | `IOperationEngine`, `Operations` (static hub; engine calls out of line, exceptions swallowed + counted) |
| `ActiveOperation.cs` | `ActiveOperation` (token), `OperationHandle` (generation CAS in core), `OutcomeCodes` (interned code table) |
| `LogWriter.cs` / `LogFieldReader.cs` | `LogWriter`, `LogValueKind`, `LogTextFlags`, `LogOmission`, `LogRecordStorage`; decoder `LogFieldReader` + `LogField` |
| `LogValue.cs` | `ILogValue<TSelf>`, `LogValueAttribute`, `LogShapeWriter<T>`, `LogShapeFor<T>`, `LogEnumNames(<TEnum>)` |
| `LogAttributes.cs` | `NoLog`, `LogName`, `LogId`, `Audited`, `LongRunning`, `LogLevel`, `LogItems`, `LogCapture` + `LogCaptureMode` |

`ILogValue<T>` implemented on `Money`, `Percentual`, `Quantity`, `Mass`, `Length` (raw value) and `FieldPath` (formatted on the
stack).

**Tests** (`src/YFex.Tests/Diagnostics`, 47 new; YFex.Tests 65 → **112**, all green): W3C parse/format round trips + invalid
inputs + future versions, Activity bridge, 0 B for id formatting/adoption; every `LogValueKind` round trip, text caps and prefixes,
growth once then truncation, field limit, shapes with name tables and depth limit, enum name tables, malformed input;
**`Operations.*` with no engine: 0 B over 1 M iterations**; fake engine: routing + capture, idempotent end, stale token after
reuse, 8-thread concurrent end writes once, engine exceptions swallowed, install rules, fallback descriptor names. The ≤ 1 ns
budget is measured by BenchmarkDotNet in `YFex.Logging.Benchmarks` (Phase 1).

**Deviations from the design text (docs updated):**
1. `OperationDescriptor<T>.CaptureFn` → `OperationCapture<T>` delegate (no abbreviation).
2. `FieldSpec(Name, DataSensitivity)` → `FieldSpec(Name, FieldCapture)` + `TypeName`/`IsSearchKey`; `WriteRedacted(field,
   DataSensitivity)` → `WriteErased`/`WriteOmitted(reason)` until the Privacy core contract exists (L14). `ILogValue<T>` has no
   `Sensitivity` (privacy 02 §6).
3. `Operations.Install` is **public** (+ `Uninstall`, `allowReinstall`) instead of internal + InternalsVisibleTo: avoids coupling
   core to engine/test assembly names; tokens stay unforgeable because `ActiveOperation`'s constructor is internal and only
   `OperationHandle.Open()` creates them.
4. Generation compare-and-swap lives in core (`OperationHandle.End`), so every engine gets idempotent ends for free.
5. `OutcomeCodes` (code interning) lives in core, so generated per-call-site caches are valid with or without an engine.
6. `LogWriter` encodes `[field u16][kind u8][payload]`; shapes nest with `BeginObject`/`EndObject` and reference a per-type name
   table (`LogShapeFor<T>.Names`) instead of composing `prefix.name` strings (no per-record name refs). Ad-hoc names use
   `Field(string)` (bit 15 set). Defaults: 512 UTF-16 chars per text (1 KiB), 32 entries per record, growth once.
7. Added kinds `Message` (33, `log.message`) and `SystemAction` (34, `system.action`); `Disabled` descriptor flag.
8. `TraceId.ToActivity()`/`OperationId.ToActivity()` allocate the BCL hex string (the BCL type stores text); adopting
   (`From(...)`) is allocation-free. Random ids: per-thread xoshiro256** seeded once per thread from `RandomNumberGenerator`.

**Gotchas:** span parameters of `LogWriter` methods must be `scoped` (CS8350 when callers pass `stackalloc` buffers into the
ref struct). `Money.TryFormat` with the invariant culture prints `¤`.

### Phase 1 — engine (2026-10-01) ✅ functionally, ⚠ producer latency budgets open

**Repo** `C:\Dev\YFex\YFex.Logging` (`logging.slnx`, shared `Directory.Build.props`: net11, preview, nullable,
`TreatWarningsAsErrors`, `NuGetAudit=false`). Not a git repository yet (nothing committed).

| Project | Contents |
|---|---|
| `src/YFex.Logging` | **Engine/**: `LoggingEngine` (+`.Producer`, `.Consumer`), `LogSlot` (pooled record = `OperationHandle`), `SlotPool`, `IndexStack` (LIFO free list), `IndexRing` (Vyukov MPMC work queue), `ThresholdTable` (copy-on-write), `OperationMetrics`, `FlightRecorder`, `ScopedOverride`, `UtcClock`, `ExceptionFingerprint`, `LoggingStatistics`, `SelfLog`, `ILogContextProbe`. **Api/**: `ILog`, `ILog<T>`, `Log<T>`, `NamedLog`, `LogCategory<T>`, `LogCategories`, `LogHandler` + `LogTraceHandler`…`LogCriticalHandler`, `LogExtensions` (`Trace/Debug/Information/Warning/Error/Critical`), `OperationScope`, `LogRecords` + `PendingLogRecord` (generated `[Log]` bodies), `LogAttribute`, `LogValueDispatch`. **Records/**: `LogRecord` (sink view), `RecordFormatter`, `CapturedRecord`. **Sinks/**: `ILogSink`, `SinkFilter`, `ConsoleSink` (Compact / Verbose / Tree), `RollingFileSink` (`.ylog` / `.jsonl` / LoggY `.txt`, roll by date+size, retention), `MemorySink`, `LiveTapSink`. **Bridges/**: `YFexLoggerProvider` (MEL), `ActivityBridge`, `MeterExport`, `CrashHooks`. **Configuration/**: `LoggingOptions` + profiles, `LoggingBuilder` (fluent), `LoggingConfigurationReader`, `AddYFexLogging` |
| `src/YFex.Logging.Format` | `.ylog`: `YlogWriter`, `YlogReader` (+`YlogEntry`), `YlogStringTable`, `PortableFields` (+reader), `Crc32C`, `BinaryBufferWriter`, `RecordKind`, `ILogTextScrubber` + `InterimLogTextScrubber` |
| `src/YFex.Logging.Generator` | `[Log]` methods, `[LogValue]` implementations, log shapes, `CaptureResolver` (type-based capture of 04 §3), YFL0001–0005, 0010, 0011, 0031 |
| `src/YFex.Logging.Testing` | `LogCapture` (isolated per async flow), `OperationAssertions`, `TraceView` (`ToTreeString`, `ShouldContainKinds`, `ShouldBeOneTrace`), `LogSchemaSnapshot.Verify` |
| `samples/YFex.Logging.AotSmoke` | every Phase 1 feature + `.ylog` read-back; **native AOT publish: 0 warnings, exit 0** |
| `benchmarks/YFex.Logging.Benchmarks` | BenchmarkDotNet (in-process, ShortRun): probes, producer, consumer |

Core additions during Phase 1 (YFex `Diagnostics/`): `NodeIdentity`/`NodeKind`, `LogValueWriterFor<T>` + `LogValueWriters`
(core value types registered by a module initializer, CA2255 suppressed with justification), `OperationDescriptor.IsCreatedAtRunTime`
(fallbacks and ad-hoc operations stay out of reviewed schemas), `Operations.InternalError` event (engine → self-log),
`DiagnosticRandom` with one thread-local access per number.

**Tests:** YFex.Logging.Tests **55**, YFex.Logging.Generator.Tests **15**, YFex.Tests **112** (unchanged), all green.
Covered: format round trip, CRC-32C vector, **torn tail at every byte offset of the last batch** (+ deterministic corruption);
operations/fields/outcomes, nesting and ambient restore (sync and across `await`), leaf operations, messages with hole names,
value objects/shapes/enums/exceptions, arguments not evaluated when disabled, threshold precedence, **reconfiguration under load
from 4 threads × 20 000 records with zero loss**, flight recorder (discard on success, retro flush on failure, crash flush), slow →
Warning, principal and tenant verbose grants (+ TTL validation), rate limit + summary, sampling, metrics while filtered, long-running
start records, links, Activity adoption, Activity bridge, MEL bridge, ad-hoc operations, fingerprint stability, pool exhaustion,
`.ylog`/`.jsonl`/`.txt` sinks (LoggY regex), rolling + retention, console compact and tree, configuration reader + reload + DI,
`LogCapture` isolation, `LogSchemaSnapshot` (committed `tests/YFex.Logging.Tests/LogSchema.verified.txt`), generated `[Log]`/
`[LogValue]` at run time, live tap, meters; **1 M enabled messages and 1 M record-only operations → 0 B on the producer thread**.
Generator: emission compiles, shapes, `[LogValue]`, diagnostics, caching (`LogMethods`/`LogValues` steps cached), secret tokens.

**Benchmarks** (BenchmarkDotNet 0.15.8, ShortRun, in-process, one producer + the consumer thread, dev laptop):

| Path (03 §11) | Measured | Budget | |
|---|---|---|---|
| No engine: `Start`+`Complete` / `IsEnabled` | 0.003 ns / 0.55 ns, 0 B | ≤ 1 ns, 0 B | ✅ |
| Disabled message, engine installed | 1.3 ns, 0 B | ≤ 1 ns, 0 B | ≈ |
| Enabled message, 3 primitive holes | 197 ns, 0 B | ≤ 60 ns, 0 B | ❌ latency |
| `Start`/`Complete` record-only | 162 ns, 0 B | ≤ 80 ns, 0 B | ❌ latency |
| `Start`/`Complete` with span | 256 ns, 136 B | ≤ 150 ns, ≤ 1 small alloc | ❌ |
| Produce + drain, null sink / `.ylog` | 155 ns (6.4 M rec/s) / ~800 ns (1.25 M rec/s) | ≥ 2 M / ≥ 500 k rec/s | ✅ |

Optimizations already applied (before → after): consumer spins before parking (enabled message 558 → 270 ns), global level floor
(disabled 5.3 → 1.3 ns), UTC derived on the consumer + fewer interlocked operations + one thread-local access per random number
(→ 219 ns), LIFO free list (span 524 → 255 ns), batched slot return (record-only → 162 ns). The remaining producer cost is
dominated by cross-core cache traffic of the slot and the queues in a tight loop (see Phase 2 handoff, item P-1).

**Phase 1 acceptance (10 table):** 1 M records → 0 B on producers ✅ · `.ylog` survives a torn tail ✅ · reload swaps thresholds
under load without loss ✅ · native AOT smoke with zero warnings ✅ · 03 §11 budgets: probes and consumer ✅, producer latency ❌
(open item P-1).

**Deviations from the design text (docs updated where names changed):**
1. Free list = lock-free LIFO `IndexStack` (Treiber, versioned head) instead of a Vyukov ring: reuses cache-hot slots; the work
   queue stays a Vyukov MPMC ring. The consumer returns a whole batch with one compare-and-swap.
2. Tenant enrichment copies `IScopeContext.Current` (a `ScopeIdentity` struct holding the existing rung array, 0 B) instead of
   `Resolve` ids: `Resolve` returns `Unknown` for values never interned and the interner has no reverse lookup. Default context:
   `AsyncLocalScopeContext`; a DI-registered `IScopeContext`/`ILogContextProbe` is bound when the container first resolves the engine.
3. Configuration: hand-written AOT-safe `LoggingConfigurationReader` + `ChangeToken.OnChange` reload instead of the binder source
   generator + `IOptionsMonitor` (fewer packages, every problem collected). Keys of later phases (`Server`, `Redaction`, `Audit`) are
   self-logged as unused. Packages: `Microsoft.Extensions.{DependencyInjection,Logging,Configuration}.Abstractions` 11.0.0-preview.5
   (first party, already used across the ecosystem; NU1510 only fires together with the ASP.NET Core framework reference).
4. Names: `log.Information/Warning` (not `Info/Warn`), `ForOperation` (not `ForOp`), `OnExhausted(ExhaustionPolicy)`,
   `engine.Verbose(OverrideTarget, value, level, ttl)` + `RevokeVerbose` (not `LoggingControl.Verbose`), `ConsoleLayout.Tree` via
   `.Console(c => c.Tree())`, `capture.Operation<T>()` (not `Op<T>`).
5. Threshold precedence is **most specific wins** (operation → longest module/category prefix → kind → global), not `min(...)`:
   with `min`, `ForModule("YFex.Data", Warning)` could never quiet a module.
6. Producers read one clock (`Stopwatch`); UTC is derived on the consumer (`UtcClock`, re-anchored every 60 s).
7. `LoggingEngine.s_levelFloor`: one static byte rejects levels no descriptor accepts before any interface call.
8. The flight recorder holds **copies** (bounded by `RecorderOptions.Capacity`), never pool slots; untraced records go to the global ring.
9. `[Log]` descriptors are registered at module load (`__YFexLogDescriptors`) when every containing type is internal/public;
   private nested containers register on first use. Default event ids are computed at run time (the generator does not replicate
   `StableHash64`); YFL0002 checks names and explicit ids.
10. Raw strings in `[Log]` parameters and shapes are captured as `WriteScrubbed` (fail closed, 04 §3); scrubbing uses the
   `ILogTextScrubber` seam (in `YFex.Logging.Format`) with `InterimLogTextScrubber` (e-mails, runs of ≥ 8 digits) until YFex.Privacy's
   scrubber exists. Scrubbing happens in text sinks and **before** `.ylog` portable fields (text never leaves the process unscrubbed).
11. `LiveTapSink` is in-process only; the LoggY named pipe moves to phase 8. MEL `BeginScope` returns a no-op scope (gap G3).
12. Shapes are generated for types passed to `[Log]` methods; interpolated holes use a shape only if one was generated elsewhere
   (Phase 2 CQRS shapes), else `ISpanFormattable`, else `ToString()` (gap G4).
13. `RecordKind` lives in `YFex.Logging.Format`; heartbeats are `Control`-channel records (hidden by the console by default).

**Bugs found by tests/smoke and fixed:** long-running start record only emitted on the capture path; object placeholders in `[Log]`
templates not rendered; schema snapshot depended on test order (lazy descriptor registration); a throwing capture leaked its slot and
left the operation as ambient parent (now unwound); console tree concatenated `[Log]` names to messages.

**Gotchas:** `YFex.Money`'s static constructor loads `pt-BR` → AOT apps need `InvariantGlobalization=false` (the smoke sets it);
bash heredocs still mangle `\n` and quotes (write edit scripts to `C:\Temp\yfex-edits\*.py`); RS1032 wants multi-sentence diagnostic
messages to end with a period; XML comments in `.csproj` cannot contain `--`; `dotnet test` of the engine tests needs the generator
as analyzer (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`).

## Phase 2 handoff (implicit instrumentation, wave 1)

**Start protocol**
1. Read README (decisions table), this file's Phase 0/1 logs, 04 (all), 05 §1–§4, 12, 13 §1–§2, and 11 §2/§4/§7. Re-read
   `../privacy/` and `../audit/` (they may have landed core types: `YFex.Privacy.DataSensitivity`, `[Sensitive]`,
   `IClassifiedData<T>`; audit contracts) before touching `FieldSpec` or capture code.
2. Baseline before coding: YFex.Tests 112, YFex.Logging.Tests 55, YFex.Logging.Generator.Tests 15, plus the counts of every library
   you will touch (Cqrs, Messaging, Messaging.Rpc, State, NavigatR, Validation, Rules; see the validation log for their last counts).
   Phase 2 acceptance requires those counts unchanged.
3. Build with `--artifacts-path /c/Temp/yfex-art` (RoselYn locks), `-p:NuGetAudit=false` for YFex projects; do not modify YFex.Data.
4. Libraries reference **only YFex core** (`YFex.Diagnostics`), never `YFex.Logging`. Generated code uses `global::`.

**Decisions to ask the user first**
- **Q-1 Shared capture code:** move `YFex.Logging.Generator/CaptureResolver.cs` to `YFex/src/Shared/Generators/DiagnosticsEmit.cs`
  and link it into the Cqrs, State, Messaging and NavigatR generators (recommended, like `EquatableArray`), or let those generators
  reference the logging generator assembly.
- **Q-2 CQRS configuration API:** `.Logging(l => l.SlowAfter(...).Ignore(c => c.X).Level(...).SampleSuccess(...))` parsed
  syntactically like `.Validation` (D27); `l.Audit()` now belongs to YFex.Audit (L20): keep it as a correlation flag only, or drop it.
- **Q-3 Outcome mapping:** confirm `QueueableResult`/`CacheableQueryResult`/`Error` → `OperationOutcome` + codes (04 §5 table) and
  where `ToOutcome()`/`OutcomeCode()` extension methods live (Cqrs, not core).
- **Q-4 RPC propagation:** header names on Fusion calls (`traceparent`, `tracestate`, `baggage`: `yfex.session`, `yfex.node`) and the
  trust policy for incoming traces (05 §2).
- **Q-5 Sensitivity:** ✅ **answered 2026-10-01: wait for YFex.Privacy** (still under development). Phase 2 keeps the current
  behaviour (secret names erased, raw strings scrubbed); `FieldSpec.Sensitivity`, `LogWriter.WriteRedacted`, the `[Sensitive]`/
  `IClassifiedData<T>` step in `CaptureResolver.Resolve` and the sensitivity column of `LogSchema.verified.txt` come after Privacy lands.
- *2026-10-01:* the user asked for concrete examples of Q-1, Q-2, Q-3, Q-4, Q-6 and Q-7 before answering (given in chat; Q-2 shown
  on the real `ShopAggregate` of `Messaging.Rpc.Tests`). Q-1 leaning: "referencing Logging is OK" — still to confirm whether that
  means generator→generator reference (B) or the linked file (A).
- **Q-6 Producer latency (P-1):** spend Phase 2 time on it or schedule it separately (options below).
- **Q-7 State roles (L16):** confirm the emitted surface of `StateChangeRole` inference and the `IUndoHistoryObserver` seam (12).

**Gaps carried over**
- **P-1 producer latency** (197/162/256 ns vs 60/80/150): hypotheses in order of expected gain: (a) per-thread slot magazines (a small
  thread-local stack refilled/returned in batches) to remove the shared free-list head from the producer path; (b) reset slots on
  the producer at rent instead of on the consumer (consumer only reads → cheaper ownership transfer); (c) shrink a slot's hot lines
  (literals and references share one 32-entry table; header fields packed into one cache line); (d) spans: keep the ambient stack in
  a thread-static for synchronous scopes and only write the `AsyncLocal` when an `await` is possible (generated async wrappers know
  it), cutting the 3 allocations (136 B) to 1. Measure each with `benchmarks/YFex.Logging.Benchmarks` (`--filter "*Producer*"`).
- **G1** sensitivity redaction (waits for YFex.Privacy; Q-5). **G2** scrubber is interim (`InterimLogTextScrubber`).
- **G3** MEL `BeginScope` frames are not recorded. **G4** shapes for interpolated holes of record types without a generated shape.
- **G5** not implemented yet from 03/06: per-tenant quotas, consecutive-duplicate folding, `EventSource` bridge, Activity
  *parenting* (`Bridge.ActivityParenting`: create an `Activity` per boundary span), `[LogItems(n)]` and `[LogCapture(mode)]`
  honoured by the generator, `OperationDescriptorFlags.Breadcrumb` session ring.
- **G6** hole names are the raw expression (`"erp"`, `TimeSpan.FromMilliseconds(31.9)`); consider normalizing non-identifier
  expressions to `value{n}` (ask the user).
- **G7** `[LogName]`, `[LogId]`, `[LongRunning]`, `[LogLevel]`, `[Audited]`, `[NoLog]` on types exist in core but are consumed only by
  the Phase 2 generators.
- **G8** no git repository for `YFex.Logging` yet; nothing committed anywhere (ask before committing).

**Order of work**
1. Q-1 shared emit file (+ caching tests unchanged for the existing generators).
2. Cqrs: descriptors + captures for every message in the module initializer the generator already emits; `.Logging(...)` parsing;
   `LoggingStage` as outermost `MessagePipeline` stage (`LocalDispatcher`, `FusionMessageBus`); golden + caching tests.
3. Messaging.Rpc: client proxies (`RpcProxyCodeBuilder`), `CallContextMiddleware` trace propagation, server `TracingHandlerInvoker`
   (descriptor by message type through a frozen map); outbox stores the `TraceContext`, replay = `StartLinked(..., Replay)`.
4. Messaging: `[Subscribe<T>]` adapters, `IEventBus.Publish`, `[Live]` refresh.
5. State: `[StateCommand]` wrapper as trace root, `StateChangeRole` inference + setter hooks, `IUndoHistoryObserver` + edit ring (12).
6. NavigatR: route descriptors + navigator operations (breadcrumbs). 7. Validation/Rules annotations (`violations`, rule evaluation leaf).
8. The doc-12 sale scenario end to end through the in-memory RPC bridge of `Messaging.Rpc.Tests`: one trace id client → server,
   `capture.Trace.ToTreeString()` golden; offline replay test shows a `Replay` link.
9. Append the Phase 2 log here (deviations, counts) and the Phase 3 handoff.

### Privacy integration (2026-10-02, YFex.Privacy Tier 1 session)

Touched by the privacy session (see `../privacy/11-phases-and-acceptance.md`, "Tier 1"): YFex core `LogWriter` gained
`WriteClassified<T>`, `WriteMaskedText`, `WriteSensitive`, `LogTextFlags.Masked`, `LogOmission.Sensitive`; `CaptureResolver.Resolve` gained
optional `member` and `floor` parameters and resolves `[Sensitive]` types/members/records (replaces the "plugs in once that contract
exists" placeholder); `RecordFormatter` renders `<redacted:sensitive>`; `YFex.Logging.Tests` references `YFex.Privacy.Generator` as an
analyzer and has `PrivacyLogTests` (3) + `LogSchema.verified.txt` updated; `YFex.Logging.Generator.Tests` has `PrivacyCaptureTests` (1).
Counts after: Logging.Tests 58, Generator.Tests 16. Phase 2 generators that link `CaptureResolver` get the privacy behaviour for free.
