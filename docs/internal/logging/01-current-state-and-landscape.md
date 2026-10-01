# 01 — Current state & NuGet landscape

## 1. Ecosystem inventory (2026-09-30)

### 1.1 What logs today

| Where | How | Problem |
|---|---|---|
| YFex.Jobs (Engine, Hub.Worker) | `ILogger<T>` + `IJobObserver` seam + `JobActivitySource` (`"YFex.Jobs"`) | Own observer the Hub dashboard ingests: a second, private observability stack |
| YFex.Payment | `[LoggerMessage]` `Log` class, `ActivitySource`+`Meter` `"YFex.Payment"` (OTel-ready) | Best practice in the repo, but isolated; tags use `enum.ToString()` (allocates) |
| YFex.Orchestration (Scheduler, PM, Rpc broker) | `ILogger<T>` | No correlation to the saga id |
| YFex.Data (SqlServer, DataMining) | `ILogger<T>` | No SQL timing, no tenant |
| YFex.Messaging.Rpc | `ILogger<T>` (1 file) | RPC calls are invisible |
| YFex.Security.Service, RoselYn.MCP | Serilog | Separate config world |
| YFex.State | `[LogChanges]` attribute declared ("boxing-free structured log output via [LoggerMessage]") | Not wired to anything |
| YFex.Cqrs, NavigatR, Rules, Validation, EventSourcing, Persistence, Tenancy, Authorization | nothing | The core of the ecosystem is dark |

### 1.2 Existing seams to absorb (keep the interfaces, add an adapter)

| Seam | Lives in | Becomes |
|---|---|---|
| `IJobObserver` (`in JobObsContext`, no-operation default) | Jobs.Abstractions | Adapter → `Operations` records (kind `job`) + Hub dashboard reads the logging server |
| `ICommandExecutedObserver` | Cqrs | Replaced by the dispatcher logging stage. Kept for business hooks |
| `IDecisionAuditSink` | Cqrs `RulesStage` | Audit channel adapter (decision fingerprint + applied adjustments) |
| `IRuleAuditSink` | Rules.Admin | Audit channel adapter (rule data diffs, publish, approvals) |
| `ITenantImpersonationAuditor` | Tenancy.Resolution | Audit channel adapter (impersonation start/stop) |
| `ISyncFailureLog` | Messaging.Rpc | Fed from, and visible in, the logging server |
| `PaymentTelemetry` | Payment | Migrated to generated descriptors; `Meter` export is kept through the engine |

### 1.3 Building blocks we reuse (do not re-create)

| Existing | Used for |
|---|---|
| `YFex.Tenancy` `IScopeContext.Resolve(Span<ScopeKey>)` (zero-alloc interned chain, `MaxLevels = 8`) | Tenant/scope on every record as `int`s, resolved to strings on the consumer |
| `ScopeIdentity.ToString()`/`Parse` (canonical `rank=value/…`) | Tenant column / filter on the server (`IsWithin` = prefix rule) |
| Messaging.Rpc `CallContext` (principal + tenant, `IRpcMiddleware`, connection/header/session sources) | Carries `traceparent`/baggage on RPC; principal for records |
| YFex core `RawValue`/`IRawValue<T>`/`ValueKind`/`Payload16` | Non-boxing field values |
| YFex core `StableHash64` | Event ids, issue fingerprints, string-table keys |
| YFex core `ArrayPoolBufferWriter<T>`/`RefArrayPoolBufferWriter<T>` | Batch encoding, spool writes |
| YFex core `Consent` (`ConsentLevel`, `IConsentStore`) | Gate behaviour-type data (not operational logs) |
| YFex core `Localization` (`IMessageSource`) | Rendering coded messages (validation/rule codes) in the viewer |
| YFex.Profiling `PrivacyScrubber`, offline queue, `HttpTelemetryTransport` | Ideas + code for scrubbing and shipping; behaviour telemetry joins logs via `session.id` |
| YFex.Security baselines / anomaly detection | Later: anomaly alerts (08 §5) |
| YFex.Resilience (retry, circuit breaker, rate limiter) | Shipper retries, server ingest rate limiting |
| YFex.Web (`AddApi<T>`, per-tenant credentials, FusionCache) | Notifier channels (Telegram, Slack, webhook…) are just API clients |
| YFex.Jobs | Retention sweeps, alert evaluation ticks, digest notifications |
| YFex.Devices `[FastEnum]` / YFex `EnumExtensions` | Enum name tables without `Enum.ToString()` |
| MemoryPack (already used by Rpc/ES) | Server control-channel messages |

## 2. NuGet survey — what fits, what we borrow

Criteria: free, AOT-clean, zero-alloc capable, tenant-aware, generator-friendly, server-capable.

| Library | License | Strengths | Weak for us | Verdict |
|---|---|---|---|---|
| **Microsoft.Extensions.Logging** + `[LoggerMessage]` generator | MIT, in the framework | Universal; every 3rd-party lib logs through it; generator avoids boxing for its own calls | `ILogger.Log<TState>` + scopes box/allocate; no tenancy; providers do formatting on the caller | **Inbound bridge only** (we implement `ILoggerProvider`) |
| **Microsoft.Extensions.Telemetry** (+ Compliance.Redaction, log buffering, sampling) | MIT | Global/per-request **log buffering with flush-on-error**, probabilistic/trace-based sampling, data classification + redactors | Built on top of MEL (alloc-heavy extended logger); buffer loses trace ids on flush; config-driven | **Borrow the ideas**: flight recorder (03 §6), `DataSensitivity` + redactors (07 §3) |
| **ZLogger v2** (Cysharp) | MIT | UTF-8 end to end, `IUtf8SpanFormattable`, interpolated-string handler that captures hole names, source generator, JSON/MessagePack | A MEL provider (inherits MEL model); AOT warning reported in JSON path; no operations/tenancy/audit | **Borrow**: interpolated handler with `CallerArgumentExpression` hole names, UTF-8 writers |
| **ZeroLog v2** (ABC Arbitrage) | MIT | Truly zero-alloc after init: pooled fixed buffers, **all formatting on a background thread**, configurable pool-exhausted strategy | Own API, no structured ecosystem, no distribution | **Borrow**: slot pool + background formatting + exhaustion strategies (03 §3) |
| **Serilog 4** | Apache-2.0 | Message templates, enrichers, huge sink ecosystem | Reflection destructuring, config package not AOT-safe, allocates per event | Inspire only (enricher/sink vocabulary) |
| **NLog 6** | BSD-3 | Targets/layouts, async wrapper overflow actions (`Discard`/`Block`/`Grow`) | Allocates; XML config world | Inspire only (overflow actions) |
| **OpenTelemetry .NET** (1.18) | Apache-2.0 | The interop standard: W3C trace context, OTLP, semantic conventions; OTLP exporter rewrote protobuf by hand for AOT | `Activity` per span (class, ~400 B); SDK pipeline is generic, not tenant-aware | **Interop target**: our ids are W3C, our OTLP exporter speaks OTLP/HTTP-protobuf (in-house, ⚑L3), attribute names follow semconv |
| **Sentry** | SDK MIT, server FSL | Issue grouping by fingerprint, breadcrumbs, releases/regressions, alert rules | Self-host is heavy; server license is not OSI | **Borrow**: issues, breadcrumbs, release tracking (08 §4) |
| **Seq** | Commercial (free single-user) | Signals, query language, dashboards | Paid for teams | Inspire only (signals / saved queries) |
| **SigNoz / OpenObserve / Grafana LGTM / Aspire dashboard** | OSS (MIT core / AGPL / AGPL / MIT) | OTLP backends: ClickHouse APM / Parquet on object storage / Loki+Tempo / local dev viewer | Separate infra, not tenant-aware for *our* model | **Optional forward targets** via OTLP (08 §7). The Aspire dashboard is the zero-effort dev viewer |
| Datadog, Honeycomb, New Relic | Commercial | Tail sampling, high-cardinality events, SLOs | Paid | Inspire only (tail sampling, SLO/error budget) |

### 2.1 Recommendation

**Build our own engine (`YFex.Logging`) with zero 3rd-party dependencies, and interoperate through standards:**

- **In:** `ILogger` (3rd-party libs), `ActivityListener` (ASP.NET Core, HttpClient, Npgsql, ActualLab spans).
- **Out:** OTLP (any backend), BCL `Meter` (dotnet-counters, Prometheus via OTel if someone wants it), `.ylog` binary to our server.
- **Why not adopt ZLogger/ZeroLog as the core:** what makes this valuable is generator-described operations, tenancy-aware
  records, audit/diagnostic channel split, flight recorder, and shipping/server. None of those libraries model them. Taking one
  would leave us with two abstractions (theirs plus ours) and their allocation model at the boundary. We take their
  *techniques* (background formatting, pooled slots, UTF-8 interpolated handler), which is a few hundred lines.

### 2.2 Packages that would need your approval (none required for phases 0–6)

| Package | License | Only if | Project |
|---|---|---|---|
| `OpenTelemetry` (+ `.Exporter.OpenTelemetryProtocol`) | Apache-2.0 | You want the official OTel SDK pipeline instead of / beside our in-house OTLP exporter | `YFex.Logging.OpenTelemetry` (optional adapter) |
| `ClickHouse.Client` | MIT | Server volume outgrows Postgres (billions of rows) | `YFex.Logging.Server.ClickHouse` |
| `K4os.Compression.LZ4` | MIT | Profiling shows Brotli/ZLib too CPU-heavy on POS-class devices | `YFex.Logging.Format` |
| `MailKit` | MIT | SMTP needs beyond BCL `SmtpClient` (OAuth2 mail, IMAP for replies-as-ack) | `YFex.Logging.Alerts` |
| `BenchmarkDotNet` | MIT | Already used by YFex.Devices.Benchmarks; same for `YFex.Logging.Benchmarks` (test-only) | benchmarks |

Sources consulted: [ZLogger](https://github.com/Cysharp/ZLogger), [ZLogger v2 architecture](https://neuecc.medium.com/zlogger-v2-architecture-leveraging-net-8-to-maximize-performance-2d9733b43789),
[ZeroLog](https://github.com/Abc-Arbitrage/ZeroLog), [OpenTelemetry .NET releases](https://github.com/open-telemetry/opentelemetry-dotnet/releases),
[OTLP exporter](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md),
[.NET log buffering](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/log-buffering),
[.NET data redaction](https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction),
[Fine-tune log volume (.NET blog)](https://devblogs.microsoft.com/dotnet/finetune-the-volume-of-logs-your-app-produces/),
[Aspire standalone dashboard](https://aspire.dev/dashboard/standalone/), [OpenObserve vs SigNoz](https://openobserve.ai/blog/openobserve-vs-signoz/),
[grafana/otel-lgtm](https://github.com/grafana/docker-otel-lgtm).
