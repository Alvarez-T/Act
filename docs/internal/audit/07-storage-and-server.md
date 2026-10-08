# 07 — Storage and server

## 1. Where audit data lives

| Tier | What | Storage |
|---|---|---|
| Producer memory | the open `AuditScope` buffer, the failure-path spill ring | pooled buffers |
| Node journal | unsealed + recently sealed entries, node chain head, cursors | `IKeyValueStore` (SQLite `synchronous=FULL`, IndexedDB, file system) (05 §3) |
| **Server (system of record)** | inbox, entries, targets, field changes, seals, anchors, verifications, gaps, tombstones, exports, signing keys, node heads | **PostgreSQL schema `audit`** (✅ L4: audit always Postgres; never ClickHouse); SQLite for single box/dev |
| Archive | sealed days | `.yaudit` packages on WORM targets (06 §8) |

## 2. PostgreSQL schema (⚑A19)

```sql
-- (sketch) schema "audit" — DDL generated/migrated by YFex.Audit.Data through YFex.Data TableCreator + TableVersionRepository
create table audit.entries (                 -- one row per entry: the canonical bytes + projected columns for queries
    entry_id        uuid        not null,     -- UUID v7 from the producer (idempotency)
    received_at     timestamptz not null,     -- partition key
    tenant          text        not null,     -- top-level tenant (chain tenant); RLS key
    tenant_path     text        not null,     -- full scope path "acme/loja7/caixa3"
    retention_class text        not null,
    node_id         uuid        not null,
    node_seq        bigint      not null,
    occurred_at     timestamptz not null,     -- producer clock (hashed value)
    occurred_corrected timestamptz not null,  -- clock-offset corrected (05 §8), used for ordering
    action_id       int         not null,     -- AuditAction.Id (names in audit.names)
    category        text        not null,
    actor_kind      smallint    not null,
    actor_id        text        not null,     -- subject id (pseudonymous key, never a display name)
    real_actor_id   text,
    relation        smallint    not null,
    outcome         smallint    not null,
    outcome_code    text,
    flags           int         not null,
    trace_id        uuid        not null,
    parent_id       uuid,
    intent_id       uuid,
    idempotency_key uuid,
    fields          jsonb,                    -- projection of captured fields (protected values stay protected)
    entry           bytea       not null,     -- canonical bytes v1 (02 §9) — THE truth
    primary key (entry_id, received_at)
) partition by range (received_at);           -- monthly partitions, created ahead by a YFex.Jobs job

create table audit.targets (                 -- per-entity history index ("who changed this?")
    entity_type text not null, entity_key text not null, tenant text not null,
    occurred_corrected timestamptz not null, entry_id uuid not null, change_kind smallint not null,
    received_at timestamptz not null
) partition by range (received_at);
create index on audit.targets (tenant, entity_type, entity_key, occurred_corrected desc);

create table audit.field_changes (           -- field-level index ("who changed Preco on any product last week?")
    entry_id uuid not null, entity_type text not null, entity_key text not null, tenant text not null,
    path text not null, treatment smallint not null,
    before_text text, after_text text,         -- display projection ONLY for Clear values; null for protected ones
    occurred_corrected timestamptz not null, received_at timestamptz not null
) partition by range (received_at);
create index on audit.field_changes (tenant, entity_type, path, occurred_corrected desc);

create table audit.seals (                   -- the chain (written only by the sequencer)
    tenant text not null, retention_class text not null, day date not null, chain_index bigint not null,
    entry_id uuid not null unique, prev_hash bytea not null, hash bytea not null, sealed_at timestamptz not null,
    primary key (tenant, retention_class, day, chain_index));

create table audit.anchors        (tenant text, retention_class text, day date, chain_index bigint, hash bytea, kind smallint,
                                   merkle_root bytea, signed_at timestamptz, key_id text, signature bytea,
                                   primary key (tenant, retention_class, day, chain_index, kind));
create table audit.signing_keys   (key_id text primary key, public_key bytea not null, valid_from timestamptz, valid_to timestamptz, revoked_at timestamptz);
create table audit.node_heads     (node_id uuid, last_seq bigint, last_hash bytea, key_id text, received_at timestamptz,
                                   primary key (node_id, last_seq));                       -- insert-only history of heads
create table audit.gaps           (node_id uuid, from_seq bigint, to_seq bigint, detected_at timestamptz, closed_at timestamptz,
                                   close_entry_id uuid, primary key (node_id, from_seq));  -- closing = new row version via insert (see note)
create table audit.verifications  (tenant text, retention_class text, day date, status smallint, entries bigint,
                                   first_bad_index bigint, detail text, verified_at timestamptz);
create table audit.tombstones     (tenant text, retention_class text, day date, final_index bigint, final_hash bytea,
                                   merkle_root bytea, purged_at timestamptz, policy text, key_id text, signature bytea,
                                   primary key (tenant, retention_class, day));
create table audit.exports        (tenant text, retention_class text, day date, target text, package_hash bytea, exported_at timestamptz);
create table audit.intents_open   (intent_id uuid primary key, tenant text, action_id int, occurred_at timestamptz);  -- maintained by sequencer
create table audit.out_of_band    (…);                                                       -- trigger mode only (04 §4)
create table audit.names          (id int primary key, kind smallint, text text unique);     -- action names
```

"Inbox" in the rest of the design = `audit.entries` rows **without** a matching `audit.seals` row. There is no separate inbox table: entries
are written once and never moved or updated, and sealing only **adds** rows to `audit.seals`.

Notes:

- `gaps` closing and `intents_open` maintenance are the only non-append operations; they are done by the server roles below, on tables that
  hold **no evidence** (evidence is entries/seals/anchors/tombstones). Gaps are closed by inserting a closing row (`closed_at`) — implementation
  may model it as a second table `gap_closures` to stay strictly append-only.
- Monthly partitions on `received_at` keep "drop a whole retention day" simple: a purge deletes the rows of whole chains (08 §5); when a
  partition holds only expired classes it is detached and dropped.

### 2.1 Roles and protections

| Role | Grants | Used by |
|---|---|---|
| `yfex_audit_writer` | `INSERT` on entries/targets/field_changes/node_heads/out_of_band; `SELECT` on node_heads | app servers (transactional inbox), ingest |
| `yfex_audit_sequencer` | `SELECT` entries; `INSERT` seals/anchors/verifications/gaps/exports | sequencer, verifier, exporter |
| `yfex_audit_reader` | `SELECT` everything, **through RLS** on `tenant` (prefix match on `tenant_path` for store-level readers) | query side |
| `yfex_audit_retention` | `DELETE` on evidence tables **only** for chains with a valid tombstone; `DROP`/`DETACH` partitions | retention job, separate credentials |

- `UPDATE`/`DELETE`/`TRUNCATE` revoked from every role except `yfex_audit_retention`, plus `BEFORE UPDATE OR DELETE` triggers on evidence tables
  that raise unless `current_setting('yfex.audit_retention', true) = 'on'` (set only by the retention job after it wrote the tombstone).
- Row-level security through YFex.Data `RowSecurityPolicy` on `tenant` **for the reader role only**. The writer role is not subject to RLS, so the
  ingest path can use `IBulkWriter` (Postgres refuses `COPY` on RLS-enabled tables for subject roles, YFex.Data 4.4).

### 2.2 SQLite (single box / dev / on-premise small server)

Same tables without partitions; `CREATE TRIGGER … BEFORE UPDATE/DELETE … SELECT RAISE(ABORT, 'audit is insert-only')` unless a retention
pragma-like flag table row is present. Single writer: the sequencer runs in-process.

## 3. YFex.Data integration

| Concern | How |
|---|---|
| Connections, dialects | YFex.Data `YFexConnectionFactory` + `YFex.Postgres` / `YFex.Sqlite` |
| DDL & migrations | `TableCreator` + `TableVersionRepository` (schema version checked at startup) + provider-specific partition DDL |
| Transactional inbox | the business transaction's `YFexConnection` via `ITransactionParticipant` (05 §2.2); plain multi-row `INSERT` |
| Ingest of shipped batches | `IBulkWriter` (`BulkTable` for entries/targets/field_changes; `IBulkRowMapper<T>` generated by hand first, by the Data generator later); `PerBatch` transactions; duplicates handled by `INSERT … ON CONFLICT DO NOTHING` staging for retries |
| Reads | `RowReader<T>` registered readers and `ReadRowsAsync` with struct `IRowConsumer` for streaming verification/export (no materialized lists) |
| Never | Dapper directly (L18), Z.Dapper.Plus, string-concatenated SQL (QueryBuilder for searches) |

## 4. Server composition

```csharp
// ✍ server Program.cs
services.AddYFexAuditServer(audit => audit
    .UsePostgres(builder.Configuration.GetConnectionString("Audit"))      // or .UseSqlite("audit.db")
    .Retention(r => r
        .Class("standard",  hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(2 * 365))
        .Class("security",  hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(5 * 365))
        .Class("fiscal-5y", hot: TimeSpan.FromDays(365), keep: TimeSpan.FromDays(5 * 365 + 30), requireExport: true, requireComplete: true)
        .Map("fiscal", "fiscal-5y").Map("cash", "fiscal-5y").Map("security", "security"))   // unmapped → "standard"
    .Anchors(a => a.Every(1_000).Or(TimeSpan.FromMinutes(10)))
    .Archive(x => x.FileSystem(@"\\worm\audit"))
    .Witness(w => w.TenantEmail()));                                        // optional (06 §9)
fusion.AddAuditRpcServer();                                                  // generated RPC contracts (AuditIngest, AuditQueries, AuditCommands)
```

## 5. Optional Marten / event-sourced store (⚑A19)

`YFex.Audit.EventSourcing` can store **sealed** entries as events in a Marten stream per `(tenant, retention class)`. It is offered because
the logging design suggested it (logging 07 §2.3), but it is **not recommended**: the Postgres tables already are append-only, chains and Merkle
proofs are ours either way, Marten's stream versioning adds nothing to the hash chain, and per-entity history would need a projection we get
for free from `audit.targets`. Keep it as a later option if an app already runs everything on Marten.

## 6. Server API (CQRS messages, generated RPC)

```csharp
public static partial class AuditQueries
{
    public sealed partial record GetEntityHistory(string EntityType, string EntityKey, AuditPage Page)
        : IQuery<AuditHistoryPage>;                                                  // "who changed this?" (09 §2)
    public sealed partial record GetEntry(AuditEntryId Id, bool Reveal = false) : IQuery<AuditEntryView>;   // Reveal needs audit.reveal (08 §6)
    public sealed partial record Search(AuditFilter Filter, AuditPage Page) : IQuery<AuditSearchPage>;
    public sealed partial record GetVerificationStatus(DateOnly From, DateOnly To) : IQuery<VerificationSummary>;
    public sealed partial record GetReviewQueue(AuditPage Page) : IQuery<AuditSearchPage>;                  // NeedsReview, dangling intents, gaps (09 §5)
    public sealed partial record GetChainHeads(DateOnly Day) : IQuery<ChainHeadList>;
}

public static partial class AuditCommands
{
    public sealed partial record Verify(DateOnly From, DateOnly To) : ICommand<VerificationSummary>;
    public sealed partial record Export(AuditExportRequest Request) : ICommand<AuditExportTicket>;          // runs as a job; result downloadable
    public sealed partial record MarkReviewed(AuditEntryId Id, string? Note) : ICommand;                    // itself audited
    public sealed partial record CloseGap(Guid NodeId, ulong FromSequence, string Reason) : ICommand;       // itself audited
}

public readonly record struct AuditPage(int Size, string? ContinuationToken);   // keyset pagination on (occurred_corrected, entry_id)

public sealed record AuditFilter(                                                // all optional; tenant comes from the caller's scope (RLS)
    string? TenantPath = null, string? ActorId = null, bool IncludeAsRealActor = true, string? Action = null, string? Category = null,
    string? EntityType = null, string? EntityKey = null, string? FieldPath = null, OperationOutcome? Outcome = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, TraceId? TraceId = null, AuditEntryFlags? Flags = null);
```

- Every query and command is authorized by YFex.Authorization permissions (08 §6) and **audited** (08 §7).
- Tenant isolation is enforced twice: the caller's scope narrows the filter, and RLS on the reader role enforces it in the database.
- Search translates `AuditFilter` into a YFex.Data `QueryBuilder` tree (no string concatenation).

## 7. Read models

```csharp
// (sketch) cold path
public sealed record AuditHistoryPage(ImmutableArray<AuditHistoryItem> Items, string? ContinuationToken);
public sealed record AuditHistoryItem(AuditEntryId Id, DateTimeOffset OccurredAt, AuditActorView Actor, AuditActorView? RealActor,
    ActorRelation Relation, string Action, string Category, OperationOutcome Outcome, string? OutcomeCode,
    ImmutableArray<FieldChangeView> Changes, string? Reason, ImmutableArray<AuditApproval> Approvals,
    AuditEntryFlags Flags, ChainVerificationStatus Verification, bool HasIntentWithoutOutcome);
public sealed record FieldChangeView(string Path, ChangeKind Kind, AuditValueView Before, AuditValueView After);
public readonly record struct AuditValueView(ValueTreatment Treatment, RawValue Value, string? MaskedDisplay);   // protected → masked text only
public readonly record struct AuditActorView(AuditActorKind Kind, string Id, string? DisplayName);              // display resolved at read time
```

Values travel as `RawValue` + treatment; the **client** formats them per culture (Money as `R$ 12,00`, dates in the user's time zone). Protected
values travel only as a mask (`***.456.789-**`) unless `Reveal = true` was authorized (privacy ⚑P17 server-side masking).

## 8. Export package `.yaudit` (auditors, fiscal inspection, data subjects)

```
package.yaudit (zip, store-only)
├ manifest.json              tenant, range, classes, filter, produced at/by, counts, per-file SHA-256, format version
├ manifest.sig               ECDsa P-256 signature of manifest.json (anchor key) + key id
├ keys.json                  every public key needed (anchor keys valid in range, device keys of included nodes)
├ chains/{class}/{day}.bin   full days: canonical entries + seals                      (operator/auditor exports)
├ proofs/{entry}.json        partial exports: entry bytes + Merkle inclusion path + day seal (data subject, single document)
├ anchors.bin                anchors, day seals and tombstones in range
└ view/entries.jsonl, view/entries.csv   human-readable projection (decoded, masked unless the export was authorized to reveal)
```

- **Full export** (all entries of a tenant/range/class): verifiable as complete chains.
- **Partial export** (one entity, one data subject, one fiscal document): entries + inclusion proofs; verifiable without other entries.
- App-specific formats (a fiscal authority's layout) plug in through `IAuditExportFormat` in the app/fiscal module; the library stays agnostic.

## 9. Offline verifier CLI (⚑A30)

```
yfex-audit verify  package.yaudit            → chains, anchors, Merkle proofs, signatures, manifest; exit code 0/1; report
yfex-audit inspect package.yaudit --entity Produto/7891234567895
yfex-audit prove   package.yaudit --entry 4f1c9a2e…   → prints the inclusion path and the day seal it verifies against
```

Native AOT single file, no database, no app assemblies: it reads canonical bytes with the format readers only. This is what an external
auditor runs.
