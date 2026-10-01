# 09 — Pitfalls, what is missing, further integrations

## 1. Pitfalls (and the design's answer)

| # | Pitfall | Answer |
|---|---|---|
| 1.1 | **Logging becomes the bottleneck**: sync I/O or formatting on the caller, lock contention | Caller only memcpys into a pooled slot; lock-free rings; formatting/I/O on consumer and sink workers (03 §2–§3) |
| 1.2 | **Unbounded memory** when the server or disk is down | Fixed slot pool, bounded per-sink queues, spool caps with priority drop, drop counters reported (03 §3.2, 05 §6) |
| 1.3 | **`AsyncLocal` allocates** on every write (and boxes struct values), and leaks ambient context into fire-and-forget work | Ambient written only when a span opens (⚑L9); immutable class node (no struct boxing); consumer/sink threads start under `SuppressFlow`; document `ExecutionContext.SuppressFlow` for long-lived background loops started inside a request |
| 1.4 | **Double instrumentation**: dispatcher + RPC + handler all open spans for the same work | Span ownership rules (04 §4): client/server spans, stages annotate, RPC server middleware opens nothing |
| 1.5 | **Recursive logging**: a sink failure logs, which fails the sink, which logs… | `[ThreadStatic]` re-entrancy guard, self-log outside the pipeline, the server uses a separate local engine |
| 1.6 | **PII in exception messages and free text** (SQL key values, validation messages with input, URLs with tokens) | Shape-only default, consumer-side scrubber, `Secret`/`Financial` hard-erased, YFL0030 (07 §3) |
| 1.7 | **High cardinality**: user/session ids as metric dimensions explode storage | Metrics dimensions = operation, kind, outcome, top tenant only; ids live on records (03 §8) |
| 1.8 | **Sampling breaks audit and metrics** | Audit never sampled; metrics computed before sampling; failures/slow always kept (03 §4.3) |
| 1.9 | **Offline replays as children** distort traces | New trace + `Replay` link (05 §4) |
| 1.10 | **Process-local ids leak out** (`ScopeValueInterner` ids, descriptor indexes, interned codes mean nothing on another machine) | Consumer remaps everything into per-file/per-batch string tables (02 §9) |
| 1.11 | **Clock skew** between POS devices and servers | Per-node offset at handshake, `corrected_ts`, causality over clocks in the viewer (05 §5) |
| 1.12 | **Native AOT stack traces** have method names but **no file/line**, and `StackTraceSupport=false` removes even names | Keep `StackTraceSupport=true` (document in app templates); fingerprint by method names + **operation descriptor name** (the operation chain is a *logical stack trace*, usually more useful than the physical one); optional future symbol upload for line numbers |
| 1.13 | **`Enum.ToString()`, `string.Format`, LINQ, closures** sneak into hot paths | Enum name tables (generated), templates rendered on consumer, analyzers YFL0012 (ToString fallback) / YFL0013 (closure in log call) |
| 1.14 | **Generated code bloat / compile time** across ~20 generators | Generators emit descriptors + capture only; wrapping lives in runtime choke points; shared linked emit file (04 §1, §3) |
| 1.15 | **Global static engine vs DI containers** (tests with several hosts) | One engine per process (like `EventSource`); tests use `LogCapture` filtered by the test's trace id, or `.AllowReinstall()` in an isolated collection |
| 1.16 | **Background consumer makes tests flaky** | `Test` profile = synchronous mode + `Flush()`; `LogCapture` assertions wait for the trace's records |
| 1.17 | **Binary format version skew** (offline devices on old releases) | Versioned frames, server reads N-2, descriptors frame makes files self-describing (02 §9) |
| 1.18 | **Trace poisoning**: an external caller sends a crafted `traceparent` to join/flood your traces | Trust policy at HTTP ingress (05 §2) |
| 1.19 | **Log injection / forging** (CR/LF or ANSI in text sinks, fake lines in LoggY) | Text sinks escape control chars; viewers render from structured fields, never from raw text |
| 1.20 | **A noisy tenant or a crash loop floods everything** (and the dev's phone) | Per-tenant quotas, EventId rate limits, duplicate folding, incident dedupe/cooldown (06 §5, 08 §5.2) |
| 1.21 | **Losing the most important logs on crash** | Crash hooks, write-through for Critical/Audit, global recorder ring flush, spool fsync, "late critical" alerts (03 §10) |
| 1.22 | **Audit immutability vs LGPD erasure** | Crypto-shredding (07 §4) |
| 1.23 | **`ActiveOperation` copied and ended twice / never ended** | Generation CAS makes `Complete` idempotent; generated code uses the try/catch shape; consumer marks operations open > timeout as `Unknown` orphans (LongRunning only) |
| 1.24 | **NU1510** if someone adds `Microsoft.Extensions.Logging.Abstractions` as a package on net11 | Framework reference only (README constraint 7) |
| 1.25 | **`YFex.System` namespace shadowing** in generated code | `global::` everywhere |
| 1.26 | **Over-logging cost on battery/metered devices** | `Device` profile, bandwidth limit, unmetered-only Debug (05 §6) |

## 2. What the brief does not mention but a "know everything" system needs

1. **Separate Audit from Diagnostics** (07 §1). Treating "everything is audit" literally makes all logs lossless and
   immutable, which is too expensive and conflicts with LGPD. Three channels with different guarantees is what makes "log
   everything" affordable.
2. **Entity change history ("who changed the price from 10 to 12")**: `[AuditChanges]` on entities, with field-level diffs
   generated by the Persistence/Data generators. State.History already has client deltas. This is the most frequent audit
   question in business apps.
3. **Release and deploy tracking**: version in `NodeIdentity`, deploy markers, issue regressions per release, "which tenants are
   still on 1.3".
4. **Heartbeats, health and SLOs**: node liveness, pipeline health, error budgets per critical operation (e.g. `FinalizarVenda` 99.9%).
   Alerts on burn rate are better than raw thresholds.
5. **Breadcrumbs and "what did the user do before the crash"**: navigation + UI actions + commands per session, joined with
   Profiling behaviour when consent allows.
6. **Crash capture beyond managed exceptions**: Windows minidumps via WER/`MiniDumpWriteDump` for native crashes (YFex.Devices
   drivers, Skia/GL), uploaded with the next batch.
7. **Feature-flag and configuration change audit**: who changed which setting/flag/rule table, linked to the incidents that
   followed. Rules.Admin already audits rule data; extend it to app config.
8. **Volume governance**: a "top noisy operations / tenants" report and cost per tenant. Logging cost becomes a product metric.
9. **Developer experience**: analyzers (`ILogger` used inside YFex libs → use probes; unclassified `Cpf`-like members captured;
   string concatenation in log calls), `LogCapture` assertions, golden "log contract" tests so dashboards and alerts do not break
   silently.
10. **Time-boxed debug grants with audit** (06 §4): pro systems never leave verbose logging on "by accident".

## 3. Integrations across the YFex ecosystem

| With | Integration |
|---|---|
| **YFex.Profiling / System** | shared `session.id` and `DeviceId`; behaviour + operations timeline per session (consent-gated); move `PrivacyScrubber` into a shared place (engine or core) |
| **YFex.Jobs Hub** | its dashboard reads the logging server instead of its private `IJobObserver` ingest; `Absence` alerts know job schedules |
| **YFex.Security** | security events → Audit + alerts; reuse its baseline/anomaly detection for `Spike` alerts; SIEM export (syslog/CEF) later |
| **YFex.Brain / Automation (AI)** | an **MCP server over the logging store** ("why did acme/loja7 fail at 14:00?": search, traces, issues as tools); AI incident triage summaries in notifications; AI cost/tokens per operation as metrics |
| **RoselYn.MCP** | issue frames → symbol FQN → source navigation, and "blast radius" of the failing method in the issue view |
| **YFex.Capabilities** | expose log search/trace lookup as capabilities for in-app assistants (permission-gated) |
| **YFex.Rules / Rules.Admin** | decisions and approvals in Audit; routing/escalation tables for alerts; "what-if" on alert routing |
| **YFex.Tenancy / Authorization** | tenant-scoped views, permission `logs.read`/`logs.audit.read`/`logs.control`, impersonation audit |
| **YFex.Web** | notifier channels as API clients; outgoing HTTP tracing |
| **YFex.EventSourcing** | trace/causation in event metadata; optional ES-backed audit store |
| **YFex.Orchestration** | saga timelines (instance view = all step traces linked) |
| **YFex.Devices** | device health dashboard (printers, scales, fiscal devices: error rates, reconnects per store) |
| **YFex.Fiscal / NFe** | SEFAZ round-trips as operations (`fiscal.sefaz`, rejection codes as outcome codes → alert on rejection spikes per UF), emission/cancellation in Audit with 5-year retention |
| **YFex.Payment** | transitions/webhooks in Audit; ambiguous-outcome alerts (reconciliation needed) |
| **YFex.UI (Avalonia/Blazor)** | in-app diagnostics panel (Memory/LiveTap sink), unhandled UI exception hook, "send diagnostics to support" button (uploads the flight recorder for the session) |
| **YFex.Localization** | render coded outcomes (validation/rule codes) in the viewer in the reader's language |
