# YFex.Logging — Observability, Audit & Alerting — Design

> **Status:** PROPOSAL (2026-09-30). Nothing implemented. Decisions marked **⚑L#** need user confirmation
> before coding (list at the end of this file). No 3rd-party package is added without explicit approval.
> **Audience:** the user, then AI sessions that will implement it. Read this file first, then only the files your phase needs
> (see [10-phases-and-acceptance.md](10-phases-and-acceptance.md)).
> **Normative vs sketch:** type/member names in code blocks are **normative** unless the block is marked `// (sketch)`.

---

## Why this exists

The ecosystem has ~30 repos and ~20 source generators, but **no common observability layer**. Today
(details: [01-current-state-and-landscape.md](01-current-state-and-landscape.md)):

- Logging is ad hoc: `ILogger<T>` in Jobs/Orchestration/Data/Payment/Messaging.Rpc, `[LoggerMessage]` + OTel
  `ActivitySource`/`Meter` only in Payment, Serilog only in Security.Service, nothing in Cqrs/State/NavigatR/Rules/Validation.
- Five unrelated "observer/audit" seams already exist and do not talk to each other: `IJobObserver`, `ICommandExecutedObserver`,
  `IDecisionAuditSink`, `IRuleAuditSink`, `ITenantImpersonationAuditor`.
- `YFex.LoggY` is an Avalonia **viewer prototype** that tails text files (`[ts] [LVL] [T:n] [C:corr] [Src@Host] msg | k=v`).
- `YFex.Profiling`/`YFex.System` track **user behaviour** (consent-gated, privacy-scrubbed, offline queue). Nothing tracks **how
  the system operates**.

Goal: **know everything that happens, start to end, across every app/instance/tenant**, with zero developer effort.
The generators already emit the glue for commands, queries, events, RPC, navigation, rules, validation, persistence. They
will emit the **instrumentation** too, so logging is *implicit*. Developers can still log manually, cheaply.

## The mental model — one event stream, three channels, two speeds

```
           ┌────────────── producers (any thread, zero-alloc) ──────────────┐
generated  │ Operations.Start/Complete (Cqrs, Rpc, Bus, ES, Data, Rules, Nav, Jobs, …)   │
manual     │ ILog<T>.Info($"…{x}") · [Log] partial methods · log.Operation()│
3rd party  │ ILogger bridge · ActivityListener bridge                        │
           └───────────────┬─────────────────────────────────────────────────┘
                           ▼  fixed-size pooled slots → MPSC queue (bounded)
                 ┌───────────────────────┐
                 │ consumer thread       │ enrich (tenant/user/node strings) · flight recorder ·
                 │ (formatting happens   │ sampling · metrics roll-up · issue fingerprint · route
                 │  HERE, not on caller) │
                 └───┬─────────┬─────────┘
       Diagnostic    │  Audit  │   Metrics
     (lossy allowed) │(lossless│ (aggregated, never sampled)
                     ▼ durable)▼
   console · rolling file (.ylog/.jsonl) · local spool → YFex.Logging.Server · OTLP · memory (tests) · LoggY live
```

| Channel | Question it answers | Loss policy | Sampling | Retention |
|---|---|---|---|---|
| **Diagnostic** | What did the system do, and why did it fail? | May drop under pressure (counted and reported) | Allowed (tail-based: failures always kept) | Days to weeks |
| **Audit** | Who did what, to which data, under which tenant, and was it allowed? | **Never drops.** Durable spool, hash-chained | **Never** | Years (fiscal: ≥ 5) |
| **Metrics** | How much, how fast, how often? | Aggregates, tiny | Computed **before** sampling | Rolled up |

**Principle A — describe at compile time, record at run time.** The generators emit an immutable `OperationDescriptor` per message, route,
rule set, saga, job, etc. (name, kind, module, stable id, field capture). At run time a record holds only **ids and raw values**.
Strings are resolved on the consumer thread.

**Principle B — the caller pays a branch, the consumer pays the rest.** A disabled log costs one static load and one compare.
An enabled log costs a slot rent, a memcpy of raw values, and an enqueue. Formatting, enrichment, I/O, compression and
network all happen off the calling thread.

**Principle C — libraries are instrumented against a contract, not the engine.** Every library emits probes against tiny
types in YFex core (`YFex.Diagnostics`, ⚑L1). With no engine registered the probes are no-operations. This is how the BCL uses
`ActivitySource`/`EventSource` without depending on OpenTelemetry.

**Principle D — correlation is ambient and crosses every boundary.** A W3C-compatible `TraceContext` (trace id, span id),
plus tenant scope, principal, session and node, flows through RPC, HTTP, the outbox, the event bus, jobs and sagas. The
same trace id ties a click in Avalonia to the SQL statement on server B.

## Files

| # | File | Contents |
|---|---|---|
| 01 | [current-state-and-landscape](01-current-state-and-landscape.md) | Ecosystem inventory, NuGet survey (ZLogger, ZeroLog, Serilog, NLog, MEL, M.E.Telemetry, OpenTelemetry, Sentry, Seq…), what we borrow, dependency decisions |
| 02 | [data-model](02-data-model.md) | `LogLevel`, `OperationKind`, `OperationDescriptor`, `OperationOutcome`, `TraceContext`, `NodeIdentity`, field model (`RawValue`/`ILogValue`), data classification, event ids, semantic conventions, `.ylog` binary format |
| 03 | [runtime-pipeline](03-runtime-pipeline.md) | Producer APIs (`Operations`, `ILog<T>`, interpolated handler, `[Log]`), slot pool + MPSC, filtering, flight recorder, enrichment, routing, sinks, backpressure, self-observability, perf budgets |
| 04 | [implicit-instrumentation](04-implicit-instrumentation.md) | Probe contract, generator emission rules, span ownership, per-library integration table, concrete before/after |
| 05 | [correlation-and-distribution](05-correlation-and-distribution.md) | Propagation across RPC/HTTP/outbox/bus/jobs/sagas/ES, offline links, tenancy/principal/session, multi-instance ordering, clock skew |
| 06 | [configuration](06-configuration.md) | Fluent DI API, appsettings binding (AOT), profiles, per-tenant/user overrides, quotas, remote/dynamic control |
| 07 | [audit-privacy-compliance](07-audit-privacy-compliance.md) | Audit channel, hash chain + anchors, existing audit seams, redaction, LGPD (crypto-shredding), consent, retention |
| 08 | [server-alerts-viewer](08-server-alerts-viewer.md) | `YFex.Logging.Server` (ingest, storage, query, live tail, issues), alerts & notifications, remote control, LoggY v2, external backends |
| 09 | [pitfalls-and-suggestions](09-pitfalls-and-suggestions.md) | Pitfalls, what is missing from the brief, further integrations across the ecosystem |
| 10 | [phases-and-acceptance](10-phases-and-acceptance.md) | Phases, tasks, acceptance criteria (alloc/latency/AOT), what each session loads |
| 11 | [usage-examples](11-usage-examples.md) | What the dev writes vs what is generated vs what you see: setup per app type, CQRS, value objects, view models, manual logs, audit, sagas/jobs/ES/SQL, bridges, live control, alerts, tests, opt-out |

## Projects (⚑L2: new sibling repo `C:\Dev\YFex\YFex.Logging`, `logging.slnx`, like Tenancy/Jobs/Resilience)

```
YFex/src/YFex/Diagnostics/            (YFex core) probe contract: TraceContext, TraceId, OperationId, OperationKind, OperationDescriptor,
                                       OperationOutcome, LogLevel, Operations (static hub, no-operation default), LogWriter, ILogValue<T>, DataSensitivity
YFex.Logging/src/
  YFex.Logging                  engine: slot pool, MPSC, consumer, filters, flight recorder, enrichers, router, sinks (console,
                                file, memory), ILog<T>, interpolated handler, MEL ILoggerProvider bridge, ActivityListener bridge,
                                BCL Meter export, crash hooks, fluent DI          (→ YFex core, YFex.Tenancy)
  YFex.Logging.Generator        netstandard2.0: [Log] partial methods, automatic type-based capture + log shapes, [LogValue] impls, descriptor registry
                                for app types; shared emit helpers for other generators (see 04 §3)
  YFex.Logging.Format           .ylog binary record format: writer/reader/string tables (client, server, LoggY share it)
  YFex.Logging.Shipping         durable local spool + shipper to the server (HTTP batch; Fusion RPC control channel)
  YFex.Logging.Audit            durable audit channel, hash chain, crypto-shredding, adapters for the existing audit seams
  YFex.Logging.Otlp             in-house OTLP/HTTP protobuf exporter + decoder (no Google.Protobuf, no OpenTelemetry SDK)
  YFex.Logging.Server           ingest, storage abstraction, query messages (CQRS), live tail (Fusion), issues, retention
  YFex.Logging.Server.AspNetCore endpoints, node auth, OTLP receiver endpoint     (→ YFex.Web.AspNetCore, YFex.Authorization)
  YFex.Logging.Server.Data      SQL storage over YFex.Data (Postgres partitions; SQLite for dev/single box)
  YFex.Logging.Alerts           alert rules, incidents, routing, escalation, notifier channels (via YFex.Web clients)
  YFex.Logging.Testing          LogCapture sink + assertions, deterministic clock, synchronous mode
  samples/YFex.Logging.AotSmoke · benchmarks/YFex.Logging.Benchmarks · tests/*
YFex.LoggY/                      becomes the desktop console of the server + local .ylog reader (phase 8, UI last)
```

Thin integrations live **inside the owning libraries** (Cqrs dispatcher stage, Rpc middleware, NavigatR navigator…). They
reference **only YFex core** probes, never `YFex.Logging`. The app's composition root references `YFex.Logging` and calls
`AddYFexLogging(...)`.

**Dependency layers**

```
Layer 0   : YFex core  — YFex.Diagnostics probe contract (no deps)
Layer 1+  : every instrumented library (Cqrs, Messaging, Rpc, NavigatR, State, Rules, Validation, ES, Orchestration, Jobs, Data…)
            → probes only
Engine    : YFex.Logging (→ YFex core, YFex.Tenancy) — must NOT reference Cqrs/Messaging/State (they are probed, not referenced)
Shipping  : YFex.Logging.Shipping (→ Logging, Format)       Audit: YFex.Logging.Audit (→ Logging, Format)
Server    : YFex.Logging.Server (→ Format, Otlp, Cqrs, Messaging.Rpc) · .AspNetCore (→ YFex.Web.AspNetCore, Authorization)
            · .Data (→ YFex.Data) · Alerts (→ Server, YFex.Web, YFex.Jobs)
```

## Hard constraints (apply to every file)

1. **AOT & trimming:** `IsAotCompatible=true`, trim analyzers on, `TreatWarningsAsErrors`. No reflection, no `Expression`, no
   `dynamic`, no `Enum.ToString()` on hot paths (generated name tables). JSON only via source-generated contexts.
2. **Own types first.** BCL primitives only where they are the interop standard (`Activity` for 3rd-party correlation,
   `Meter` for metric export, `ILogger` as an *inbound* bridge). The ecosystem's own API never asks for `ILogger`.
3. **Zero allocation in steady state** on the producer path: no boxing (generic constrained writers, `RawValue`,
   `IUtf8SpanFormattable`), no closures, no params arrays, no LINQ, no string building. Own iterators over spans.
   `[Union]` + `IUnion` tagged structs, not the native `union` keyword (it boxes).
4. **Never block the caller** on the Diagnostic channel. Bounded memory everywhere. The Audit channel may block or spill to disk,
   per its policy.
5. **Never log through yourself.** The pipeline's own failures go to a self-log (stderr / Windows Event Log), guarded by a
   `[ThreadStatic]` re-entrancy flag.
6. **Privacy by default.** Message payload fields are **not** captured unless opted in (⚑L8). `[Sensitive]`/`DataSensitivity`
   values are redacted before they leave the process. Exception messages are scrubbed too.
7. **net11 preview, `LangVersion=preview`.** `Microsoft.Extensions.Logging.Abstractions` comes from the framework reference.
   Never add it as a PackageReference (NU1510 under warnings-as-errors).
8. **`YFex.System` shadows `System`.** Generated code always uses `global::` qualified names.

## Decisions to confirm (⚑)

| # | Proposal | Recommendation |
|---|---|---|
| ⚑L1 | Where the probe contract lives | **YFex core `YFex.Diagnostics` namespace** (~12 small types). Alternative: new `YFex.Diagnostics` assembly that every library must add |
| ⚑L2 | Repo | New sibling repo `C:\Dev\YFex\YFex.Logging` (`logging.slnx`). `YFex.LoggY` stays the viewer app |
| ⚑L3 | 3rd-party dependencies in the engine | **None.** In-house OTLP exporter. Optional future adapter `YFex.Logging.OpenTelemetry` (refs `OpenTelemetry`, Apache-2.0) only if you want the OTel SDK pipeline |
| ⚑L4 | Server storage | YFex.Data SQL first (Postgres daily partitions, SQLite single-box). Forward to SigNoz/OpenObserve/Grafana via OTLP for heavy analytics. A ClickHouse adapter would need `ClickHouse.Client` (MIT); later, only if you approve |
| ⚑L5 | Compression | BCL `BrotliEncoder`/`ZLibStream` (no dep). `K4os.Compression.LZ4` (MIT) only if CPU profiling demands it |
| ⚑L6 | Alert rule engine | Own small **windowed evaluator** (alerts are streaming and time-windowed; YFex.Rules is situation→decision, synchronous). Reuse YFex.Rules only for **routing/escalation tables** (runtime-editable) |
| ⚑L7 | Level type | Own `YFex.Diagnostics.LogLevel` with values identical to MEL (`Trace=0 … Critical=5, None=6`). Audit is a **channel**, not a level |
| ⚑L8 | Default payload capture | **Shape only** (ids, counts, outcome codes, durations). Fields opt-in via `[LogField]` or fluent `.Logging(l => l.Capture(...))`; `[Sensitive]` always redacted |
| ⚑L9 | Default span granularity | `Boundaries + Operations` (RPC/HTTP/command/query/event/job/saga step/SQL). Property changes and validation per field = records only, no spans |
| ⚑L10 | First notifier channels | Email (SMTP via BCL), Telegram Bot API, Discord/Slack/Teams webhooks, ntfy (self-hosted push), generic webhook. WhatsApp/SMS are paid APIs: design the seam, implement later |
| ⚑L11 | Audit tamper evidence | SHA-256 hash chain per (tenant, day) + periodic anchor signed with ECDsa P-256 (BCL). Crypto-shredding (AES-GCM, BCL) for LGPD erasure |
| ⚑L12 | Name of the manual logging interface | `ILog<T>` (short, no clash with `ILogger<T>`) |
| ⚑L13 | API surfaced by the usage examples (11, header list): `[LogValue]` generator for value objects, alert/relay/control registration names, `LogCapture` | Accept as written, then fold into 02/03/06/08 |
