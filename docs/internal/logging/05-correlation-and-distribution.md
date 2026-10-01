# 05 — Correlation, identity & distribution

## 1. Ambient context

`Operations.Current` is backed by one `AsyncLocal<AmbientNode?>`, where `AmbientNode` is an immutable class
`{ TraceContext, SessionId, Baggage? , Parent }`. It is written **only when a span opens** (⚑L9 granularity). Records
without spans read it without allocating.

- **Tenant** is not duplicated. It is read from `IScopeContext.Resolve` (already ambient in Tenancy, already zero-alloc).
- **Principal** is read from the host's accessor (`IPrincipalAccessor` / RPC `CallContext`) through an engine-registered
  `IPrincipalProbe` that returns an interned id (no string per record).
- **Interop with `Activity.Current`:** at `Start`, if our ambient is empty and an `Activity` exists (ASP.NET Core request,
  3rd-party code), we adopt its trace/span ids. With `Bridge.ActivityParenting` on, every `Boundary` span also creates an
  `Activity`, so non-YFex `HttpClient`s and SDKs parent correctly. That costs one allocation, at boundaries only.

## 2. Propagation per boundary

| Boundary | Carrier | Written by | Read by | Notes |
|---|---|---|---|---|
| Fusion RPC | RPC headers: `traceparent`, `tracestate`, `baggage` (`yfex.session`, `yfex.node`) | client side of `CallContextMiddleware` (already exists, D29) | server side of `CallContextMiddleware` | Tenant/principal already travel via CallContext; we only add trace + session |
| HTTP out (YFex.Web) | W3C headers | `TracingHandler` in `AddApi<T>` pipeline | remote service | Never send `baggage` with tenant/user ids to **external** APIs (config: `PropagateBaggageTo(hosts)`) |
| HTTP in (ASP.NET) | W3C headers | caller | `YFex.Logging.AspNetCore` middleware | **Trust policy:** accept the incoming trace only from authenticated nodes; otherwise start a new trace + `FollowsFrom` link (prevents trace poisoning) |
| Outbox (offline) | outbox entry stores `TraceContext`, session, enqueue UTC | `OutboxEnqueue` operation | `OutboxReplay` operation → new trace + `Replay` link | §4 |
| Event bus (in-process) | ambient flows with `async` | — | `[Subscribe]` adapters | Deferred/background delivery captures context at publish |
| Remote events (`IRemoteEventBus`, `IEventChannel`) | envelope metadata | publisher | subscriber → `FollowsFrom` link | fan-out: one publisher, N consumers |
| Event sourcing | event metadata `{traceId, spanId (causation), correlationId}` | store append | projections, sync, replays → link | survives forever with the event: the audit trail of *why* an event exists |
| Jobs | schedule stores the creating trace | scheduler | each execution = **new root** + `FollowsFrom` link | a nightly job is not a child of the click that scheduled it |
| Sagas / process managers | saga instance id + anchor trace id | saga start | each step = new root + links (triggering event, anchor) | no days-long traces; the viewer groups by `yfex.saga.instance` |
| YFex.Profiling (behaviour) | shared `session.id` | UI session | server joins behaviour ↔ operations | consent rules stay in Profiling |

## 3. Identity carried by records

| Field | Source | Stored as |
|---|---|---|
| Tenant / scope chain | `IScopeContext.Resolve` | `(rank, valueId)×n` in the record → canonical `ScopeIdentity` text on the server (`IsWithin` = prefix filter) |
| Principal | CallContext / app session | interned id → `enduser.id` (optionally hashed per 07 §3) |
| Effective vs real principal | Tenancy impersonation | both, when impersonating (audit requirement) |
| Session | UI session (client), connection/Fusion session (server) | interned id |
| Device | `NodeIdentity.DeviceId` (= Profiling hardware fingerprint hash) | per node |
| Node | `NodeIdentity` | per connection / file |

## 4. Offline and links (why "parent" is wrong for replays)

A POS terminal goes offline at 10:00 and sells 40 items. Each `FinalizarVenda` is `Queued` and its outbox entry stores the
trace context. At 12:15 the connection returns and the outbox replays.

- If the replay **reused** the original trace as parent, the 10:00 trace would grow a child 2h15 later, and the waterfall,
  duration and "trace complete" detection would all break.
- So each replay is a **new trace** whose root `OutboxReplay` operation carries `OperationLink(original, LinkKind.Replay)` and the field
  `queued_for=2h15m`. The viewer shows "replayed from 4bf92f35… (queued 2h15m)" with a jump link, and the original trace shows
  "replayed as 9c01…".
- The same applies to ES catch-up (`Replay`), batch fan-in (`Batch`, many links) and resilience retries in a later process
  (`Retry`).

## 5. Ordering and clocks across instances

- **Per-node `Sequence`** (monotonic `ulong`, persisted high-water mark at shutdown): the server detects **gaps** (lost records)
  and duplicates (idempotent retries).
- Timestamps: node UTC at record time, plus server receive time. At shipper handshake the server returns its clock. The
  shipper estimates the offset NTP-style (`(t1 - t0 - rtt) / 2`) and the server stores `corrected_ts = ts + offset` per node
  session. A skew > 2 s raises a node warning (POS devices with dead CMOS batteries are a real case).
- **Within a trace, causality beats clocks:** the viewer orders by parent/child and clamps a child into its parent's interval when
  clocks disagree.

## 6. Shipping (`YFex.Logging.Shipping`)

```
consumer → SpoolSink (segment files) ─► Shipper ─► server /ingest (HTTP POST, application/x-ylog, Brotli)
                                         ▲  ack(lastDurableSeq) → truncate segments
```

- **Spool:** 4 MiB segment files of `.ylog` frames (CRC per frame) under `spool/{app}/`. fsync policy: Audit lane on every batch,
  Diagnostic lane every 1 s. Caps: max bytes/age. When full, the oldest **Diagnostic** segments below Warning are dropped
  first. **The Audit lane is never dropped.** Reaching its cap raises a local Critical and switches to `WriteThrough` blocking
  (configurable).
- **Two lanes, priority order:** Audit + `≥ Error` first, then the rest. A device coming back online ships the important
  records within seconds even with a 200 MB diagnostic backlog.
- **Idempotent:** a batch is identified by `(InstanceId, firstSeq, lastSeq)`, so retries never duplicate.
- **Backoff:** exponential with jitter via YFex.Resilience. HTTP 429/`Retry-After` from the server is honoured (server-side
  quotas, 08 §2).
- **Bandwidth/metered awareness:** `MaxBytesPerSecond`, `OnlyOnUnmeteredFor(LogLevel.Debug)` (Windows `NetworkCostType`; Android
  via platform probe).
- **Relay mode (combine sources):** a store-level edge node (`AddYFexLoggingRelay()`) receives batches from LAN terminals,
  spools them and forwards to the central server, preserving each origin's `NodeIdentity`. The central server ingests from
  relays, direct nodes and OTLP sources the same way.
- **Control channel (optional):** the shipper keeps a Fusion RPC connection for server → node commands: level overrides with TTL,
  "start debug capture for session X", "upload flight recorder now", ping (08 §6).
