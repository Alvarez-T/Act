# 10 — Phases & acceptance

Order principle: **contract → engine → implicit wave 1 → shipping → server → audit → alerts → wave 2 → UI last**. Each phase
ends with green tests, a native-AOT smoke publish with zero warnings, and a log section appended to this file (deviations,
gotchas), the same protocol as the validation design.

| Phase | Scope | Loads | Acceptance |
|---|---|---|---|
| **0 — Contract** (⚑L1, ⚑L7, ⚑L12 confirmed) | YFex core `YFex.Diagnostics`: `LogLevel`, `LogChannel`, `OperationOutcome`, `OperationFlags`, `OperationKind`, `OperationDescriptor(<T>)`, `OperationDescriptorFor<T>`, `OperationRegistry`, `Operations` (no-operation), `ActiveOperation`, `TraceId/OperationId/TraceContext/OperationLink`, `LogWriter`, `ILogValue<T>`, `DataSensitivity`, attributes (§04-3). `ILogValue` on core value types (Money, Percentual, Quantity, Mass, Length, FieldPath) | README, 02, 04 §2–§3 | `Operations.*` with no engine: 0 B and ≤ 1 ns (benchmark); W3C parse/format round-trip tests; core still has no dependencies |
| **1 — Engine** (⚑L2, ⚑L3) | repo `YFex.Logging`: slot pool + Vyukov rings, consumer, thresholds, scoped overrides, flight recorder, enrichment, Console/RollingFile(.ylog/.jsonl/.txt)/Memory/LiveTap sinks, `ILog<T>` + `LogHandler`, `[Log]` generator, MEL provider bridge, Activity bridge, Meter export, crash hooks, fluent DI + config binding, profiles, `YFex.Logging.Format` reader/writer, `YFex.Logging.Testing`, benchmarks, AotSmoke | 02, 03, 06 | 03 §11 budgets met; 1 M records → 0 B on producers; `.ylog` survives `kill -9` (torn-tail test); config reload swaps thresholds under load without loss |
| **2 — Implicit wave 1** | Cqrs `LoggingStage` + generator descriptors/captures/`.Logging` parsing; Messaging `[Subscribe]`/bus/`[Live]`; Rpc proxies + `CallContextMiddleware` propagation + `TracingHandlerInvoker` + outbox context/links; State `[StateCommand]` + `[LogChanges]`; NavigatR; Validation/Rules annotations; shared `DiagnosticsEmit.cs` | 04, 05 §1–§4 | doc-12 sale scenario produces the 04 §6 trace (client+server, one trace id across the in-memory RPC bridge of `Messaging.Rpc.Tests`); offline replay test shows a `Replay` link; generator golden + caching tests per generator; existing test counts unchanged |
| **3 — Shipping & correlation** | spool, two-lane shipper, idempotency, clock offset, relay mode, YFex.Web `TracingHandler`, ASP.NET Core middleware + trust policy, control channel client | 05 | network-cut chaos test: no loss of Audit/≥Error, bounded Diagnostic loss counted; relay forwards with original node identity |
| **4 — Server core** (⚑L4, ⚑L5) | ingest + auth + quotas, `ILogStore` (Postgres partitions, SQLite), query messages, search language, live tail, issues/fingerprints/regressions, retention jobs, OTLP receiver + in-house OTLP exporter | 08 §1–§4, §7 | 50 k rec/s ingest on a dev box (Postgres); tenant isolation tests (a tenant admin never sees another tenant); OTLP round-trip to the Aspire dashboard |
| **5 — Audit** (⚑L11) | Audit channel, write-through, hash chain + anchors, adapters (`IDecisionAuditSink`, `IRuleAuditSink`, `ITenantImpersonationAuditor`, Authorization, Payment), crypto-shredding, retention tiers | 07 | tamper test (edit one row → verification fails at the right index); erasure test (key deleted → field unreadable, chain still valid); audit gap → Critical incident |
| **6 — Alerts & control** (⚑L6, ⚑L10) | evaluator, rule types, incidents, routing tables, escalation, digests, notifiers (Telegram, Slack/Discord/Teams, SMTP, ntfy, webhook), ack links, remote scoped-debug grants with TTL | 08 §5–§6, 06 §4 | crash loop on 300 simulated nodes → 1 incident, 1 notification + escalation; TTL grants auto-expire; every grant audited |
| **7 — Implicit wave 2** | EventSourcing, Orchestration, Jobs observer adapter, Resilience, Persistence, Data (`TracingDbConnection`, QueryBuilder), Payment migration, Devices, Automation, CustomLogic, Search, Avalonia hooks | 04 §5 | each library: golden generated output, an end-to-end trace test, no new reference to `YFex.Logging` |
| **8 — LoggY v2 (UI last)** | Avalonia console: live, search, waterfall, issues, incidents, nodes, tenants, audit explorer, control; local `.ylog` mode | 08 §8 | dogfooding: LoggY is instrumented and visible in itself |

### Open questions to settle before Phase 0

1. ⚑L1–⚑L12 (README).
2. Who receives critical alerts in the first rollout (people/channels), and quiet hours.
3. Which apps go first (suggestion: Farma desktop + its server, since it exercises offline, POS devices and fiscal).
4. Expected volume per tenant/day, which sizes Postgres partitions and quotas.
5. Hosting for the server (same infra as other YFex servers? one per region?).

## Implementation log

*(empty: append per phase: date, what was done, deviations from this design, gotchas, test counts)*
