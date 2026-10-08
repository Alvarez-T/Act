# 14 — Storage: where logs live, which database, and the YFex.Data / YFex.Persistence integration

## 1. Where each kind of data lives (tiers)

| Tier | What | Storage | Why |
|---|---|---|---|
| **Process memory** | slots, queues, flight recorder, edit rings, breadcrumbs | RAM, fixed pools (03 §3) | the hot path never touches I/O |
| **Node: local log & spool** (desktop, server, POS) | rolling `.ylog` files + shipping spool segments | **append-only segment files** (02 §9 framing, CRC per frame) | sequential append is the cheapest durable write there is; crash-safe (torn tail only); no DB engine on weak devices |
| **Node: browser** (Blazor WASM) | spool | **IndexedDB** via `YFex.Persistence.IndexedDb` (`IKeyValueStore`, one key per segment: `log:spool:{seq}`) | browsers cannot write files |
| **Node: small state** | instance id, sequence high-water mark, shipping cursor, applied control-config version | `YFex.Persistence` `ILocalStore` (sync, read at startup) | exactly what `ILocalStore` exists for |
| **Node: encryption at rest** (shared POS terminals) | spool/file segments | `YFex.Persistence.Encryption` (AES-GCM) wrapping the segment writer, opt-in | hashed fields are not plaintext, but messages and strings can still be sensitive |
| **Relay** (store LAN edge) | buffered batches from terminals | segment files (same spool) | store-and-forward, no queries needed |
| **Server: hot store** | records, operations, activities, search keys, issues, metric roll-ups | **relational DB via `ILogStore`**: PostgreSQL default (§3) | queries, joins with tenants/issues, retention by partition drop |
| **Server: audit** | audit entries + anchors + subject keys | **PostgreSQL, separate schema, insert-only role** (never ClickHouse) | ACID, legal proof, crypto-shredding keys |
| **Server: archive** | expired partitions (optional), audit WORM export | compressed `.ylog` segments on disk/object storage (Parquet later) | cheap, re-importable by the same reader |
| **Forward (optional)** | copy for analytics | OTLP → SigNoz / OpenObserve / Grafana (08 §7) | heavy analytics without running it ourselves |

**Not used for records:** `IKeyValueStore` on desktop/server (per-key overhead, no ordering/range scans) and
`DataDurableQueue<T>` (one row per item, too much per-record overhead for log volume). They remain right for *small state*.

## 2. Choosing the server database

Log data is **append-only, time-ordered, wide, read mostly when recent, aggregated a lot, almost never updated**, and needs
**retention by time**. That favours time-partitioned or columnar storage.

| Option | Fit | Strengths | Weaknesses | Verdict |
|---|---|---|---|---|
| **PostgreSQL** (native partitioning) | good up to ~tens of GB/day | already in YFex.Data (`YFex.Postgres`), ACID (audit in the same DB), `COPY BINARY` ingest, BRIN on time, `jsonb`, **row-level security** (YFex.Data `RowSecurity*`), partition drop = instant retention, Marten (ES) runs on it too | row storage → larger on disk than columnar; aggregations over billions of rows get slow | **Default** (⚑L4) |
| PostgreSQL + **TimescaleDB** | very good | hypertables (automatic partitions), native compression (5–10×), continuous aggregates for metrics | compression/continuous aggregates are under the **Timescale License** (free to self-host, not OSI); extension must be installed | **Optional** (⚑L19): enable if the license is acceptable; `ILogStore` unchanged |
| **ClickHouse** | excellent at scale | columnar, 10–20× compression, aggregations over billions of rows in ms; used by SigNoz and others for exactly this | no real transactions (not for audit); one more engine to operate; needs `ClickHouse.Client` (MIT) **approval** | **Scale-out adapter** for diagnostic records/operations/metrics when volume demands it; audit stays in Postgres |
| SQLite (WAL) | small | `YFex.Sqlite` exists, zero ops, single file | single writer; not for multi-instance servers | **Single-box / dev / small on-premise** server |
| SQL Server / Oracle | ok | YFex.Data supports them | license cost scales badly with log volume | allowed as an `ILogStore` implementation if a customer mandates it; not first |
| MongoDB | weak | YFex.Data has a provider; flexible documents | no columnar compression, weaker time-range aggregations than ClickHouse or Postgres partitions; sharding ops | not recommended |
| Elasticsearch / OpenSearch | ok | full-text search | JVM cluster ops, memory hungry; Elastic license (SSPL/ELv2/AGPL) or OpenSearch ops | not recommended (full text comes from Postgres `tsvector` on the message, or the forward target) |
| Event sourcing (YFex.EventSourcing / Marten) | wrong for diagnostics | perfect append-only streams per aggregate | logs are not aggregates; no stream identity; replay makes no sense | **only as an optional audit store** (per-tenant stream on Marten = Postgres, 07 §2.3) |
| Plain files on the server | archive only | cheapest | no queries | archive tier |

**Recommendation:** start with **PostgreSQL** (one engine for logs, audit and the rest of the YFex servers). Design every table
behind `ILogStore`, then add the **ClickHouse adapter** for the diagnostic tables when you pass roughly 50–100 GB/day or ~1 B
retained rows (estimate). Audit never moves.

### 2.1 Rough sizing (estimates to validate in Phase 4 benchmarks)

| Volume | Postgres (TOAST-compressed `jsonb` fields) | Postgres + Timescale compression | ClickHouse |
|---|---|---|---|
| 50 M records/day (~1 000 POS + servers, Information + retro) | ~10–15 GB/day | ~2–3 GB/day | ~1–1.5 GB/day |
| 14 days hot retention | ~150–200 GB | ~30–40 GB | ~15–20 GB |

## 3. PostgreSQL schema (normative shape; DDL details in Phase 4)

```sql
-- (sketch) schema "logs": diagnostic, partitioned by day
create table logs.records (
    ts            timestamptz not null,        -- corrected node time (05 §5)
    received_at   timestamptz not null,
    node_id       uuid        not null,
    seq           bigint      not null,         -- per-node sequence: dedupe + gap detection
    channel       smallint    not null,
    level         smallint    not null,
    kind_id       int         not null,          -- operation kind (names in logs.names)
    name_id       int         not null,          -- operation / message name
    outcome       smallint,
    outcome_code  int,
    duration_us   bigint,
    trace_id      uuid,                          -- 16 bytes; fits uuid
    operation_id  bigint,
    parent_id     bigint,
    tenant        text,                          -- canonical ScopeIdentity text; RLS + prefix filter
    principal     text,                          -- hashed per policy
    session_id    text,
    fields        jsonb,                         -- captured fields (already redacted)
    message       text,                          -- rendered lazily for text-search; null when template-only
    exception     jsonb,
    primary key (node_id, seq, ts)
) partition by range (ts);
create index on logs.records using brin (ts);
create index on logs.records (trace_id);
create index on logs.records (tenant, ts);

create table logs.activities (                   -- one row per root operation (13 §5): the default list
    start_ts timestamptz not null, trace_id uuid not null, name_id int not null, kind_id int not null,
    tenant text, principal text, session_id text, node_id uuid, duration_us bigint, outcome smallint,
    child_count int, error_count int, release text
) partition by range (start_ts);
create index on logs.activities (tenant, principal, start_ts desc);

create table logs.search_keys (                  -- [LogValue(SearchKey = true)] values → traces
    key_type_id int not null, key_value text not null, trace_id uuid not null, ts timestamptz not null
) partition by range (ts);
create index on logs.search_keys (key_type_id, key_value);

create table logs.names (id int primary key, kind smallint, text text unique);   -- string table (02 §9)
-- logs.issues, logs.issue_occurrences, logs.metric_rollups, logs.nodes, logs.incidents …   (08 §3)

-- schema "audit": separate role with INSERT/SELECT only, never partition-dropped by diagnostic retention
create table audit.entries (tenant text, day date, chain_index bigint, prev_hash bytea, hash bytea, ts timestamptz,
    action text, actor text, real_actor text, outcome smallint, code text, trace_id uuid, fields jsonb,
    primary key (tenant, day, chain_index));
create table audit.anchors (tenant text, day date, chain_index bigint, hash bytea, signature bytea, signed_at timestamptz);
create table audit.subject_keys (subject_id text primary key, wrapped_key bytea, created_at timestamptz, erased_at timestamptz);
```

- **Partitions:** created ahead by a YFex.Jobs job (`logs.partition.maintain`: 3 days ahead) and dropped by retention per tier
  (07 §5). Dropping a partition is instant and produces no vacuum load.
- **Tenant isolation:** the query side runs with YFex.Data **row security** (`RowSecurityPolicy` on `tenant` with prefix match).
  A tenant admin cannot read other tenants' rows, even through a buggy query.
- **Names table:** strings shipped in batch string tables (02 §9) are stored once. Records hold ints, so they stay narrow.

## 4. YFex.Data integration (specified here; it was only mentioned before)

`YFex.Logging.Server.Data` implements `ILogStore` on top of YFex.Data:

| Concern | How | Note |
|---|---|---|
| Connections, dialect | YFex.Data `IDataSource` / provider modules (`YFex.Postgres`, `YFex.Sqlite`, …) | the same configuration as every YFex server |
| DDL & migrations | YFex.Data `TableCreator` + `TableVersionRepository` (versioned schema), plus provider-specific partition DDL | the store checks its schema version at startup |
| **Ingest** | **new provider capability `IBulkWriter`** (⚑L18), advertised in `ProviderCapabilities`: Postgres → `NpgsqlBinaryImporter` (`COPY … FROM STDIN (FORMAT BINARY)`), SQL Server → `SqlBulkCopy`, SQLite → prepared insert in one transaction | **not Z.Dapper.Plus**: it is a commercial package and not AOT-friendly. `IBulkWriter` is useful beyond logging too (DataMining extraction, Rules.Data imports) |
| Reads | YFex.Data's own data-access adapter (L18) | never Dapper directly; the logging store prefers the adapter's typed/span-based reader path (AOT, allocations) |
| Tenant isolation | YFex.Data `RowSecurity*` (`IRowSecured`, `RowSecurityInstaller`) | |
| Query builder | YFex.Data QueryBuilder for the search language translation (08 §4) | the search parser produces a QueryBuilder tree, never string concatenation |

```csharp
// (sketch) new in YFex.Data: provider capability for high-volume ingest
public interface IBulkWriter
{
    ValueTask<long> WriteAsync<TRow>(TableRef table, ReadOnlySpan<ColumnRef> columns,
                                     IBulkRowSource<TRow> rows, CancellationToken ct);
}
public interface IBulkRowSource<TRow>
{
    bool MoveNext();                              // own iterator, no IEnumerable/LINQ
    void WriteCurrent(ref BulkRowWriter writer);  // typed column writes (int, long, Guid, string, ReadOnlySpan<byte> jsonb…)
}
```

```csharp
// server composition
services.AddYFexLoggingServer(s => s
    .UsePostgres(builder.Configuration.GetConnectionString("Logs"))       // YFex.Postgres provider underneath
    // .UseSqlite("logs.db")                                              // single box
    // .UseClickHouse(diagnostics: "...", audit: postgres)                // later, ⚑L4 scale-out
    .Retention(r => r.Hot(TimeSpan.FromDays(14)).Debug(TimeSpan.FromDays(3)).AuditYears(5)));
```

## 5. YFex.Persistence integration (nodes)

```csharp
services.AddYFexLogging(log => log
    .State(s => s.UseLocalStore())                         // ILocalStore: instance id, seq high-water, cursor, config version
    .WriteTo(w => w.Server(uri, srv => srv
        .Spool(sp => sp.Files("spool/")                    // desktop / server / POS
                       // .KeyValue()                      // Blazor WASM: IKeyValueStore (IndexedDb backend)
                       .EncryptWith<AesGcmSegmentProtector>()))));   // YFex.Persistence.Encryption, opt-in
```

| Persistence piece | Used for | Not used for |
|---|---|---|
| `ILocalStore` | node identity, sequence, shipping cursor, applied config | records |
| `IKeyValueStore` (IndexedDb) | browser spool segments | desktop/server spool (files are faster) |
| `YFex.Persistence.Encryption` | at-rest encryption of segments on shared devices | — |
| `DataDurableQueue<T>` | — | log records (too much per-row overhead) |

## 6. Decisions

| # | Decision | Recommendation |
|---|---|---|
| ✅ L4 (confirmed 2026-10-01) | Server database | **PostgreSQL** default for everything; ClickHouse adapter later for the diagnostic tables only; SQLite single-box; audit always Postgres |
| ✅ L18 (confirmed 2026-10-01) | Bulk ingest + data adapter | own `IBulkWriter` capability in YFex.Data (Postgres COPY BINARY, SqlBulkCopy, Oracle, SQLite batch); Z.Dapper.Plus removed; own adapter layer so callers never use Dapper directly (Dapper may remain underneath for now). Implemented by a separate session in the YFex.Data repo; the logging store consumes only the YFex.Data abstractions |
| ✅ L19 (confirmed 2026-10-01) | TimescaleDB | optional extension (compression + continuous aggregates); the store detects it and falls back to native partitioning |
