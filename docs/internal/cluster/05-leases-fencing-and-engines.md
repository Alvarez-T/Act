# 05 — Leases, fencing, clocks, node list, notices, counters, duplicate filter, engines

SQL below is Postgres for readability; SQL Server and SQLite render the same statements through YFex.Data's dialects. Schema is
declared with YFex.Data's schema model and installed by `SchemaInstaller` (so migrations already run once, 06 §3.3).

## 1. Lease table (⚑CL5)

```sql
create sequence cluster.fencing;                                   -- one sequence for every key: tokens never repeat across keys
create table cluster.leases (
    key          varchar(200) primary key,
    holder       uuid,                                             -- ClusterNodeId; null = free
    token        bigint       not null,
    acquired_at  timestamptz  not null,
    expires_at   timestamptz  not null,                            -- database clock
    fenced_at    timestamptz                                       -- last FenceAsync, for the admin view
);
```

| Operation | Statement (sketch) | Result |
|---|---|---|
| **Acquire** | `insert … values (@key, @me, nextval('cluster.fencing'), now, now + @d) on conflict (key) do update set holder = @me, token = nextval('cluster.fencing'), acquired_at = now, expires_at = now + @d where leases.holder is null or leases.expires_at < now returning token, expires_at - now` | a row = granted with a **new** token; none = held by someone else |
| **Renew** | `update … set expires_at = now + @d where key = @key and holder = @me and token = @t and expires_at > now returning expires_at - now` | 0 rows = lost (fires `Lease.Lost`) |
| **Release** | `update … set holder = null, expires_at = now where key = @key and holder = @me and token = @t` + notice `cluster.released` | the next node acquires without waiting for expiry |
| **Fence** | `update … set fenced_at = now where key = @key and holder = @me and token = @t and expires_at > now` **inside the caller's transaction** | 0 rows → `LeaseLostException` |
| **Find** | `select holder, token, expires_at - now …` | admin view |

`now` = `clock_timestamp()` (Postgres), `SYSUTCDATETIME()` (SQL Server), `julianday('now')`-based UTC (SQLite). Rows are never
deleted while a key is in use; free rows older than 7 days are swept.

## 2. Fencing (⚑CL6)

### 2.1 Why

A holder can lose its lease without knowing it in time: a long GC pause, a frozen VM, a network cut between it and the database. When
it wakes, it may still be inside a loop that writes. Expiry alone cannot stop that write; a **check at the store** can.

### 2.2 How the fence works

`FenceAsync` runs the fence `UPDATE` on the caller's connection, inside the caller's transaction, **before** the business writes:

- if the lease is still ours, the `UPDATE` takes a **row lock on the lease row** until the transaction ends. A competing node's
  acquire (which updates the same row) **waits** for that commit or rollback. So "fence passed" and "business writes committed" are
  atomic with respect to any takeover;
- if another node took the key, the token differs → 0 rows → `LeaseLostException` → the caller's transaction rolls back.

Cost: one indexed single-row `UPDATE` per transaction. The worker fences once per batch, not per row.

### 2.3 Limits (written down, not hidden)

| Case | Protected? | What to do |
|---|---|---|
| writes to tables **in the same database** as `cluster.leases` | ✅ | `FenceAsync` |
| writes to **another database** (Audit in its own database, a tenant database) | not by `FenceAsync` | the owner stores the **token** in its own row and refuses `token < stored` (`update … where fence_token <= @t`); Cluster offers the helper SQL fragment |
| external calls (SMS provider, marketplace, vendor HTTP) | ❌ | idempotency keys at the receiver; the duplicate filter (§7) for our own endpoints |
| database failover that loses the last seconds | consistent | the token may be issued again after a promotion, but the writes fenced with the lost token were lost with it (same database) |
| in-memory engine | ✅ within the process | token comparison in memory |
| site engine | ❌ (`BestEffort`) | low-stakes work only (09 §3) |

## 3. Timing and clocks (⚑CL7, ⚑CL22)

- **Expiry is decided by the database clock only.** Nodes never compare their wall clock with `expires_at`.
- The holder measures with its **monotonic** clock (`TimeProvider.GetTimestamp`): it records `t0` *before* sending acquire/renew, reads
  `remaining` from the reply, and considers the lease lost at `t0 + remaining − SafetyMargin`. Network delay only makes it stop
  earlier, never later.
- Defaults: Duration 30 s, RenewEvery 10 s, SafetyMargin 5 s. Two failed renewals in a row still leave ~5 s before the local
  deadline. Crash takeover ≤ Duration + the second-ranked grace (§4.1 of 04) ≈ 35 s; drain handover ≈ 1 s.
- Per key overrides (`LeaseOptions`) for work that tolerates longer gaps (a daily export: 2 min) or needs faster takeover (5 s, with
  a warning metric if renew latency exceeds a third of it).
- Clock check (06 §5): at each heartbeat the node compares `GetTrustedNow()` (YFex.Time) with the database clock read in the same
  round trip; it only *reports* skew, except that skew above `SafetyMargin` keeps the node out of `Ready` (it would misjudge its
  own deadlines if its monotonic clock drifted too; a bad wall clock is a symptom worth stopping on).

## 4. Node table (⚑CL14)

```sql
create table cluster.nodes (
    id           uuid primary key,                -- ClusterNodeId
    app          varchar(100) not null, version varchar(64) not null, host varchar(255) not null, process_id int not null,
    installation uuid,                            -- NodeIdentity.Installation (on-premises central)
    state        smallint not null,               -- ClusterNodeState
    features     varchar(2000) not null,          -- sorted tags, '\n'-separated
    weight       int not null,
    started_at   timestamptz not null,
    last_seen    timestamptz not null             -- database clock
);
```

Heartbeat every 5 s (`update … set last_seen = now, state = @s`); gone after 15 s; rows with `state = Left` or gone for 1 day are
swept. The heartbeat also returns the current view (one `select` of ready/draining rows): a cluster of 50 nodes reads 50 small rows
every 5 s per node — fine; above ~200 nodes the view is read every 15 s instead (configurable).

## 5. Notices (⚑CL12, ⚑CL13)

### 5.1 Engines

| Engine | Publish | Receive | Delay | Loss |
|---|---|---|---|---|
| In-memory | direct call | direct | 0 | none |
| Postgres | `select pg_notify('yfex_cluster', @payload)` (base64 of `topic · from · bytes`, ≤ ~5.5 KB) | one dedicated `LISTEN` connection per node | ms | on listener reconnect |
| Polling table (SQL Server, SQLite, fallback) | `insert into cluster.notices` | `select … where id > @last` every 1 s | ≤ 1 s | rows older than 5 min are swept: a node paused longer misses them |
| Messaging bus (later, optional) | through `YFex.Messaging` when an app already runs a server bus | | | per bus |

- **Transactional publish:** on the Data engine, `PublishAsync(connection, …)` inside a transaction sends the notice **only if the
  transaction commits** (`NOTIFY` and the table insert are both transactional). Owners use it for "License of tenant X changed" so a
  notice never announces a rolled-back change.
- **Gaps:** after a listener reconnect, or when the polling cursor fell behind the sweep, the node raises `IClusterNotices.Gap`;
  consumers re-read their versions (that is why each consumer keeps one).
- **Echo:** a node does not receive its own notices unless `includeSelf`.

### 5.2 The FusionCache backplane (⚑CL13)

FusionCache supports a pluggable backplane (`IFusionCacheBackplane`). Persistence implements one over `IClusterNotices`
(`YFex.Persistence.Cluster`, owned by Persistence): "entry *k* changed/expired" → other nodes evict their L1. Messaging.Fusion's
computed-value invalidation and Authorization's signals use their own topics the same way. Redis stays an optional alternative an app
may configure in FusionCache directly; YFex never requires it.

## 6. Shared counters (later, ⚑CL17)

```sql
create table cluster.counters (
    key varchar(200) not null, scope varchar(400) not null, period_start timestamptz not null,
    value bigint not null, "limit" bigint,                       -- null = unlimited (meters)
    primary key (key, scope, period_start)
);
```

| Mode | Take | Use for | Cost |
|---|---|---|---|
| **Exact** | `update … set value = value + @n where … and ("limit" is null or value + @n <= "limit") returning value` (upsert for a new period) | money-like budgets, licensing meters, anything audited | one round trip per take |
| **Block** | a node takes a block (`@n = 100`) the same way, spends locally, and gives back the unused rest at drain or period end (`value − rest`) | high-rate limits (SMS per tenant per day, AI tokens) | one round trip per block; a crash **loses** the rest (under-spends, never over-spends) |

Periods are computed by the **owner** (Resilience's quota uses the scope's time zone and `ITrustedClock`, notifications ⚑N16); the
counter only stores `period_start`. The in-memory engine keeps the same semantics in a dictionary (one node). Resilience's durable quota
adapter is a bridge package owned by Resilience (10 §2); Cluster does not depend on Resilience's quota types.

## 7. Duplicate filter (later, ⚑CL18)

```sql
create table cluster.claims (
    key varchar(200) not null, scope varchar(400) not null,
    state smallint not null,                 -- 1 InProgress, 2 Completed
    holder uuid, token bigint,               -- InProgress: the node and its token (reclaimed after expires_at)
    result bytea,                            -- Completed: the owner's encoded result (owner's format, versioned by the owner)
    created_at timestamptz not null, expires_at timestamptz not null,
    primary key (key, scope)
);
```

- `ClaimAsync(key, scope)` → `Claimed` (go ahead), `Completed(result)` (return the stored result), `InProgress` (another node runs it:
  wait with back-off or answer "in progress"). An `InProgress` row whose holder is gone is **reclaimed** after `expires_at` (the
  same rule as a lease).
- `CompleteAsync` stores the result in the caller's transaction when possible (exactly-once effect for writes to the same database).
- Retention per kind (Messaging: ≥ 30 days, offline 08 §3; webhooks: 7 days).
- Users: Messaging `IIdempotencyStore` shared adapter (finding F4), webhook ingress (provider event ids), vendor check-in (request
  nonce, LC-P5), payment-style commands in apps.

## 8. Engines

### 8.1 In-memory (`YFex.Cluster`)

One node: `TryAcquireAsync` grants at once (a per-key token counter), `Lost` never fires except at drain, notices are direct,
counters and claims are dictionaries. Several **in-memory clusters in one process** are only for the harness (§8.4).

### 8.2 Data (`YFex.Cluster.Data`)

Postgres first (CL-P2), then SQL Server and SQLite (CL-P7). Uses YFex.Data's commands, generated mappings and dialects; no EF.
Connection use: renewals and heartbeats share one small pool slot per node (a dedicated `YFexConnection` kept open, reopened on
failure); the Postgres listener has its own connection. A node therefore needs 2 connections at rest.

### 8.3 SQLite single-machine guard

At join, the node reads `cluster.nodes`; if a non-gone node from **another host** exists, join fails with
`ClusterConfigurationException("SQLite supports one machine; use Postgres or SQL Server for two servers")`. Two processes on one
machine (an IIS overlapped recycle) are fine: SQLite's file locks serialize them.

### 8.4 Harness engine (`YFex.Cluster.Testing`)

A shared in-memory "database" with the Data engine's semantics (database clock = a `FakeTimeProvider`; tokens from one counter; row
locks simulated for `FenceAsync`), plus faults: `Partition(node)`, `Pause(node, d)`, `Skew(node, d)`, `DropNotices(node)`,
`SlowDatabase(latency)`. The contract test suite runs against the harness engine **and** a real Postgres (Docker via `linuxKit`),
later SQL Server (Docker) and SQLite (file).

### 8.5 Site engine (Offline/Installations)

Implements `IClusterEngine` with `Guarantee = BestEffort`; no counters, no claims. Design in [09](09-site-engine.md).

## 9. When you do not need a lease (agreed 2026-11-11)

A lease is for **long-held ownership of work that is not a stream of versioned writes** (a sequencer, a scheduler, an outbox loop, a
saga timeout checker). Before taking one, check whether a cheaper guarantee already exists:

| Situation | Use instead of a lease | Why |
|---|---|---|
| Writes to **event-sourced** data (one order, one stock item) | the stream's **expected version**: YFex.EventSourcing `IEventStore` appends with an expected `StreamVersion` and throws `StreamConcurrencyException` on a mismatch (`YFex.EventSourcing/src/YFex.EventSourcing/Store/IEventStore.cs:13`) | the stream version is already a fencing token; nothing to renew or expire |
| Writes to an ordinary row that two nodes may update | optimistic concurrency (a version column / EF concurrency token) | same idea for state-based tables |
| One run of a due job | the store's atomic claim (Jobs JO1: `SKIP LOCKED` / conditional `UPDATE … RETURNING`) | finer and cheaper than a lease per job (10 §2.2) |
| Work that arrives as messages | the broker's competing consumers / single active consumer, inbox de-duplication (Orchestration.Rpc `BrokerConsumer`) | the broker already gives each message to one consumer |
| **Processing** event streams (projections, reactions) | partition the streams; one `WorkerPerScope` lease **per partition**, not per event | the lease owns the partition; the checkpoint write is fenced |

**Why leases are not stored as events:** a lease is renewed every 10 s forever (≈ 8 640 events per key per day nobody needs), its
expiry needs a clock that a stream does not have, and it is short-lived "who holds it now" state, not history to keep.

**Guidance for developers and AI agents** (goes into the agent rules with the CL6 `FenceAsync` note): exclusive write to versioned
data → expected version / optimistic concurrency first; a long-running loop that must run once → Cluster worker; an exclusive
database write *inside* such a worker → `FenceAsync`; an external side effect → idempotency key.
