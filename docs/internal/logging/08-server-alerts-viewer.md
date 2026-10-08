# 08 — Server, alerts & viewer

## 1. Shape

```
nodes / relays ──/ingest (.ylog batches)──┐
OTLP sources ────/v1/logs,/v1/traces──────┤      YFex.Logging.Server (stateless, N instances)
LoggY / admin ───Fusion RPC (CQRS)────────┤  ┌──────────────────────────────────────────────┐
                                          └─►│ ingest → verify → normalise → write          │
                                             │ issues (fingerprint) · metrics roll-up        │
                                             │ alert evaluator ──► incidents ──► notifiers ──┼─► Telegram/Slack/Discord/Teams/
                                             │ query (CQRS queries) · live tail (Fusion)     │    e-mail/ntfy/webhook
                                             │ control channel (overrides → nodes)           │
                                             └──────────────┬───────────────────────────────┘
                                                            ▼
                                   ILogStore: Postgres (partitioned) | SQLite | forward-only OTLP
```

The server is itself a YFex app. It uses CQRS messages for its API, Fusion for live views, YFex.Authorization for access,
YFex.Tenancy for isolation, YFex.Jobs for retention and alert ticks, and YFex.Web clients for notifier channels. It logs
**through a separate in-process engine instance writing locally**, never to its own ingest (no feedback loop).

## 2. Ingest (`YFex.Logging.Server.AspNetCore`)

- `POST /ingest`: body = `.ylog` frames (Brotli), headers carry the node API key and format version. Steps: authenticate
  node → parse frames with `YFex.Logging.Format` (span-based reader, pooled buffers) → dedupe by `(InstanceId, seq range)` →
  verify audit chain → map string tables → batch insert. Response: `lastDurableSeq`.
- **Auth:** per-node API keys (issued at enrolment, revocable) or mTLS. Tenant-scoped keys can only write records for their
  tenant prefix (enforced: a record whose scope is outside the key's tenant is rejected and audited).
- **Protection:** per-key and per-tenant rate limits (YFex.Resilience rate limiter), max batch size, 429 + `Retry-After`. The
  node spool absorbs the backlog.
- **OTLP receiver** (`/v1/logs`, `/v1/traces`, `/v1/metrics`, protobuf over HTTP, decoded by `YFex.Logging.Otlp`): third-party
  services and non-.NET apps join the same traces.
- **Legacy import:** LoggY's text format and `.jsonl` files (one-off upload) for old apps.

## 3. Storage (`ILogStore`, ⚑L4; full design, schema and YFex.Data integration in [14-storage](14-storage.md))

| Table | Key / partition | Notes |
|---|---|---|
| `nodes` | instance id | identity, last heartbeat, version, clock offset, pipeline health |
| `records` | **daily partitions**, index `(tenant_prefix, ts)`, `(trace_id)`, `(event_id, ts)`, `(level, ts)` | header columns typed; fields in `jsonb` (Postgres) / JSON text (SQLite); message rendered lazily by the reader |
| `spans` | derived view over operation-end records | waterfall = records of a trace ordered by parent |
| `activities` | `(tenant, user, start)`, daily partitions | one row per **root operation** (13 §5): the default list in every viewer |
| `search_keys` | `(key_type, value)` → trace ids | ids marked `[LogValue(SearchKey = true)]`; sensitive keys stored as hash (13 §5) |
| `issues` / `issue_occurrences` | fingerprint | first/last seen, count, affected tenants/nodes/releases, status (open/resolved/ignored/regressed), sample occurrences with breadcrumbs |
| `metric_rollups` | (descriptor, outcome, tenant_top, bucket) | 1 min → 1 h roll-ups by Jobs |
| `audit_entries` + `audit_anchors` | (tenant, day, chain index) | separate tablespace/schema; insert-only role; never partition-dropped by diagnostic retention |
| `subject_keys` | data subject id | crypto-shredding keys (07 §4) |
| `alert_rules`, `incidents`, `notifications` | | §5 |

- Retention = drop whole partitions (cheap), by tier (07 §5) and tenant plan. Data residency: a tenant can be pinned to a store
  instance per region.
- **Forward targets:** any store can be paired with an OTLP forwarder to SigNoz/OpenObserve/Grafana for heavy analytics, while
  audit, issues and alerts stay in our store.

## 4. Query, live tail, issues

- **Queries are CQRS messages** (`Logging.Queries.Search`, `GetTrace`, `GetIssue`, `GetNodeHealth`, `GetMetrics`), generated
  like everything else. They are therefore validated, authorized (`.RequirePermission("logs.read")`), tenant-filtered by
  `IScopeContext` (a tenant admin only ever sees `IsWithin(theirTenant)`) and cached via Fusion compute methods.
- **Search language** (small, own parser over spans, no LINQ): `level>=warn kind:cmd operation:Vendas.* tenant:acme/loja7
  outcome:faulted dur>500ms "estoque"`.
- **Live tail:** a Fusion live query per filter, with server-side filtering and a bounded per-subscriber buffer. Slow viewers
  see "N skipped".
- **Issues (Sentry-like):** grouping by fingerprint (03 §6). Regressions are detected when a resolved issue reappears in a newer
  release. Each occurrence keeps breadcrumbs, the retro records of its trace, node, tenant and release. Optional deep link to code:
  the frame's method FQN → RoselYn MCP / IDE "go to symbol".

## 5. Alerts and notifications (`YFex.Logging.Alerts`, ⚑L6, ⚑L10)

### 5.1 Rule types (own windowed evaluator; ticks via YFex.Jobs; streaming checks on ingest)

| Type | Example |
|---|---|
| `Immediate` | any `Critical`; any audit gap; any `Denied` on `admin.*` |
| `Threshold` | ≥ 20 `Faulted` of `kind:cmd` in 5 min for any tenant |
| `ErrorRate` | `Faulted / total` of `Vendas.*` > 5% over 10 min with ≥ 50 calls |
| `Latency` | p95 of `Sql` > 800 ms for 15 min (from metric roll-ups) |
| `NewIssue` / `Regression` | first occurrence in `prod`; resolved issue reappears in a newer release |
| `Absence` | no heartbeat from node X for 5 min; job `FechamentoDiario` did not run by 02:00 (reads YFex.Jobs schedules) |
| `Spike` (later) | volume deviates from the weekly baseline (reuse YFex.Security baseline/anomaly code) |
| `LateCritical` | a Critical record arrives from a node that was offline (shipped after reconnect) |

Each rule has: filter (same search language), group-by (tenant / node / operation), window, threshold, severity, **dedupe key**,
cooldown, auto-resolve condition.

### 5.2 Incidents → routing → notifiers

- **Incident** lifecycle: `Open → Acknowledged → Resolved` (+ `Flapping` guard). One incident per dedupe key, with an
  occurrence counter, so a crash loop on 300 POS terminals gives one incident with a count of 300, not 300 messages.
- **Routing policy:** severity × environment × tenant × time window (quiet hours, on-call schedule) → channel list. Policies are
  runtime-editable decision tables (the only reuse of YFex.Rules here).
- **Escalation:** not acked in N min → next level (dev → lead → phone channel).
- **Digests:** low severity grouped into hourly/daily summaries.
- **Notifier channels** (`INotifier`, implemented as YFex.Web `AddApi<T>` clients so credentials can be per tenant, secrets are
  protected and calls are resilient): Telegram Bot API, Slack/Discord/Teams webhooks, e-mail (SMTP, BCL), ntfy (self-hosted
  push), generic webhook (HMAC-signed). WhatsApp Business/SMS are paid: the seam exists, implementation later. Messages include
  severity, rule, count, tenants, a top frame, and a link into LoggY/web viewer. Ack is possible by link (signed, expiring token)
  or Telegram button.
- Every notification and ack is audited.

### 5.3 Channels, recipients and schedules are data and plug-ins (user decision 2026-10-01)

Nobody is hard-coded. Who receives what, through which channel, and when, is **configuration/data**. New channel types
(SMS providers, WhatsApp, PagerDuty-like services, a company chat) are **plug-ins** implementing one interface.

```csharp
namespace YFex.Logging.Alerts;

/// One channel TYPE (e-mail, SMS, Telegram…). Implementations are stateless; addresses/credentials come from ChannelConfig.
public interface INotificationChannel
{
    string Type { get; }                                         // "email", "sms", "telegram", "webhook", "ntfy", …
    NotificationCapabilities Capabilities { get; }               // MaxLength, SupportsAck, SupportsRichText, CostPerMessage
    ValueTask<DeliveryResult> SendAsync(Notification notification, ChannelAddress to, ChannelConfig config, CancellationToken ct);
}

/// SMS is its own seam because providers differ (Twilio, Zenvia, AWS SNS, a local GSM modem via YFex.Devices…).
public interface ISmsProvider
{
    string Name { get; }
    ValueTask<DeliveryResult> SendAsync(PhoneNumber to, ReadOnlyMemory<char> text, CancellationToken ct);
}
```

| Data (runtime-editable in LoggY/admin, stored in `logs.alert_*` tables) | Example |
|---|---|
| **Contacts** | João: e-mail `joao@acme.com`, SMS `+55 11 9…`, Telegram chat id |
| **Teams / on-call rotations** | `devs` (weekly rotation), `suporte-loja` |
| **Schedules** | business hours, quiet hours 23:00–07:00, holidays (per time zone) |
| **Routing policy** | `Critical` → on-call via SMS + Telegram, escalate after 10 min to lead via phone/SMS; `High` → team chat; `Medium` → hourly e-mail digest; per tenant/environment overrides |
| **Channel configs** | SMTP server, SMS provider + credentials (via YFex.Web secret storage), webhook URLs |

```csharp
services.AddYFexLoggingAlerts(a => a
    .AddChannel<EmailChannel>()                       // built-in
    .AddChannel<TelegramChannel>()                    // built-in
    .AddChannel<WebhookChannel>()                     // built-in (Slack/Discord/Teams/generic)
    .AddChannel<SmsChannel>(sms => sms.UseProvider<MyZenviaSmsProvider>())   // app-supplied provider
    .AddChannel<MyCompanyChatChannel>()               // any custom INotificationChannel
    .RoutingFrom<SqlAlertRoutingStore>());            // contacts/teams/schedules/policies as data
```

Built-in channels in the first wave: e-mail (SMTP), Telegram, webhooks (Slack/Discord/Teams/generic HMAC), ntfy. SMS and
WhatsApp ship as **interfaces + one reference adapter skeleton**. Concrete paid providers are added per deployment.

## 6. Remote control (server → node)

Over the shipper's Fusion control channel (05 §6): scoped verbose overrides with TTL ("trace session 3fa2 for 15 min"), flight
recorder upload, shipping pause/drain, a config version push (levels/sampling as data, like `RuleSetSync`). Nodes ack with
their applied config version, so the server shows the effective config per node. All grants are audited (06 §4).

## 7. External backends and dev tooling

| Need | Use |
|---|---|
| Local dev, zero setup | Aspire standalone dashboard via OTLP (`.Otlp("http://localhost:18889")`) |
| Heavy analytics / long retention cheap | forward OTLP to OpenObserve (Parquet on object storage) or SigNoz (ClickHouse) |
| Existing Grafana shop | forward OTLP to `grafana/otel-lgtm` / Loki + Tempo |
| `dotnet-trace`, PerfView, ETW | EventSource bridge sink |

## 8. LoggY v2 (desktop console, phase 8, UI last)

`YFex.LoggY` evolves from a file tailer into the **Avalonia client of the server**, built on YFex.State/NavigatR/Cqrs (it
dogfoods the ecosystem and is itself instrumented):

- **Live** (Fusion live tail with filters), **Search**, **Trace waterfall** (client + server spans, links for replays),
  **Issues** (grouping, regressions, breadcrumbs, assign/resolve), **Incidents/alerts inbox** (ack), **Nodes** (health, version,
  clock offset, spool backlog, last seen, effective config), **Tenants** (volume, error rate, quotas), **Audit explorer** (chain
  verification status, per-entity history), **Control** (grant scoped debug with TTL).
- **Local mode:** open `.ylog`/`.jsonl`/legacy `.log` files or attach to a local process via the LiveTap named pipe. No server
  needed.
- A Blazor viewer (YFex.UI Blazor) can reuse the same view models later.
