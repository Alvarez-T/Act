# 01 — Current state and inventory (2026-10-22)

Sources: `YFex.Data` at `a535c72`, `YFex.Data/docs/DATA-ACCESS-AND-BULK-WRITE.md` (§10–§11), `ARCHITECTURE.md`,
`ROW-LEVEL-SECURITY.md`, and the consumer repos named below.

## 1. YFex.Data: where it stands

| Area | State | Evidence |
|---|---|---|
| Own command pipeline, `IBulkWriter` (stage 1) | **Done** 2026-10-01 | `DATA-ACCESS-AND-BULK-WRITE.md` §10 |
| Generated row mapping, parameter interceptors, `MergeAsync`, Dapper removed, AOT (stage 2) | **Done** 2026-10-02 | same, §11; `tests/YFex.Data.AotSmoke` |
| Row-level security (application + database modes, generator, Postgres/SQL Server/Oracle policies) | **Done** | `ROW-LEVEL-SECURITY.md`, commit `e9e5878` |
| Row security on EF Core (`YFex.Data.EntityFramework`) | **Done**, row security only | §3 below |
| Tenancy → row security (`YFex.Data.Tenancy`) | **Done** | `TenancyRowSecurityTests` (13) |
| **Schema / DDL / versioning** | **Never designed.** Code from the original import, untouched by stages 1–2 except the `TableVersionRepository` fix | §2 below |
| Provider split (dialect vs DataMining provider), Q7 | **Deferred** ("later, separate task") | §8 and §9 Q7 of the data-access doc |
| YFex.Security on YFex.Data | **Deferred** (still on its own Dapper + `MigrationRunner`) | Q8 |
| Native writers, dialect upserts (`MERGE`, `ON CONFLICT`), database-mode session statements on real servers | **Never run** against PostgreSQL / SQL Server / Oracle (env-gated tests exist: `YFEX_TEST_*`) | §10.5, §11.4 |
| `IN @list` expansion | Not implemented (no caller) | §11.3 |
| `RelationalProviderBase` introspection (list tables/columns, sample, code objects, extraction cursor, scalar) | **TODO** (5 markers in `src/YFex.Data/Providers/RelationalProviderBase.cs`) | source |
| YFex.DataMining engine | **Skeleton**: concrete providers, SQL Server interceptor, AI worm, automation bridge marked `// TODO` (≈14 markers across DataMining, Csv, Excel, MongoDb, Oracle, Postgres, SqlServer) | `README.md` "Status", `ARCHITECTURE.md` |
| SQLite | Dialect only (`SqliteDialect`: prefix, `LIMIT/OFFSET`, `ON CONFLICT` upsert); no mining provider; bulk uses the shared prepared-statement path | `src/YFex.Sqlite` |

**Answer to "was it fully implemented or is there another phase?"** The data-access plan (stages 1–2) and row-level
security are complete and tested on SQLite. Four things are still open: the provider split (Q7), real-server
verification, YFex.Security's migration, and the DataMining engine, which is a skeleton by design. **Schema creation was
never part of any phase.** This folder is that phase.

## 2. The schema code in YFex.Data today (`src/YFex.Data/Schema/`)

| Type | What it does |
|---|---|
| `ITable { string Name; SqlQuery CreateScript; }` | A table is a name plus **one** DDL string. The engine is baked into the string |
| `ITableCreator<T>` / `TableCreator<T>` | Runs `CreateScript`; warns if it does not look like DDL |
| `VersioningTableCreator<T>` | Refuses a table already in `TABLE_VERSION`, creates it, records version 1.0 |
| `IMultiTableCreator<T>` / `TablesProcessor<T>` | Runs every `ITableCreator<T>` in one scoped transaction |
| `VersionTable` | `TABLE_VERSION (VERSION_ID, TABLE_NAME, VERSION DECIMAL(5,2), COMMIT_SCRIPT VARCHAR(4000), ROLLBACK_SCRIPT, DESCRIPTION, PRIORITY)` in "ANSI" types |
| `TableVersionRepository` | `INSERT` version 1 / `EXISTS` by table name |

Defects (each one is a reason to replace the area rather than patch it):

1. **`TableCreator.CreateTable` swallows every exception** (logs `Critical`, returns normally). `VersioningTableCreator`
   then records version 1.0 for a table that does not exist. After that, the table can never be created again: the
   next run throws `TableAlreadyExistsException`.
2. **`TablesProcessor<T>` passes `new T()` to every creator.** All creators receive the same table type, so "several
   related tables" means "one table type, N creators". The generic shape cannot express a module of different tables.
3. **Versioning is per table, not per change set.** `VERSION_ID` is always 1, `VERSION` is a `double`, and there is no
   way to express "version 2 adds a column". `ROLLBACK_SCRIPT` and `PRIORITY` are never written.
4. **`COMMIT_SCRIPT VARCHAR(4000)`.** Offline's 18 statements are already close to 4,000 characters. One more table
   and recording the module as one script would overflow.
5. **No lock.** Two instances starting together both see "not versioned" and both run the DDL.
6. **No checksum, no applied-at, no applied-by.** Drift between code and database is invisible.
7. **Only caller:** `tests/YFex.Data.Tests/DataAccessTests.cs`. Nothing in any repo creates tables through it, so it can
   be replaced freely (⚑DS1).

Related engine-specific strings elsewhere in YFex.Data:

- `RowSecurityKey.SqlType` / `[RowSecurityKey(SqlType = …)]`: the SQL type of a key column used by native policy DDL.
  It is a per-engine string on a portable attribute. With a schema model it can be derived from the column's
  `ColumnType` when the table belongs to a module ([03 §6](03-ddl-dialects-and-rendering.md#6-row-security-ddl)).
- `tests/YFex.Data.Tests/Bulk/BulkTestEngines.cs`: hand-written DDL per engine for the bulk contract tables.

## 3. Entity Framework integration today (`src/YFex.Data.EntityFramework`)

One file, `RowSecurityInterceptors.cs` (265 lines), plus `UseYFexRowSecurity(...)` overloads (dialect + `RowSecurity`,
or a `YFexConnectionFactory`):

| Interceptor | Does |
|---|---|
| Command | application mode: rewrites SELECT/UPDATE/DELETE (raw SQL too) with the same `RowSecurity.Secure` as `DbExtensions`, binds values through the dialect |
| Connection | database mode: pushes context values into the session once per physical open (`RowSecuritySession`) |
| SaveChanges | stamps key columns on insert; validates on insert/update/delete (application mode) |

Tests: `YFex.Data.EntityFramework.Tests` (8, EF Core + SQLite): LINQ, raw SQL, SaveChanges,
`ExecuteUpdate/Delete`, database-mode session pushes.

**Missing:** everything except row security. [05](05-entity-framework-integration.md) has the full list. In short:

| # | Gap |
|---|---|
| E1 | No way to apply a YFex schema module from an EF migration, so an app on EF migrations runs two unrelated migration systems |
| E2 | No way to render YFex DDL through an EF provider's `IMigrationsSqlGenerator` (would cover every EF provider, MySQL included) |
| E3 | No export of the EF model to `TableSchema` / `BulkTable`, so bulk-inserting EF entities through `IBulkWriter` means describing the table twice |
| E4 | Row-security policies cannot be installed from an EF migration, and nothing checks the `RowSecurityCatalog` against the EF model (secured table/column exists, type matches) |
| E5 | The caller must pass the `IQueryDialect` by hand; it is not inferred from the EF provider |

## 4. Hand-written DDL across the ecosystem

| Site | Engine selector | Engines | Pattern |
|---|---|---|---|
| `YFex.Offline/src/YFex.Offline.Data/OfflineSqlSchema.cs` | own `enum SqlEngine { Sqlite, PostgreSql, SqlServer }` | 3 | 7 type variables (`binary`, `text`, `longText`, `id`, `key`, `number`, `small`) chosen by `switch`, then interpolated into 12 tables + 6 indexes |
| `YFex.Authentication/src/YFex.Authentication.Data/AuthenticationSqlSchema.cs` | **another** `enum SqlEngine` (same values, different namespace) | 3 | 9 tables + 10 indexes; 8 type variables plus an engine-specific index: SQL Server gets `… where operator_code is not null`; a second inline `engine == SqlServer ? "nvarchar(12)" : "text"` |
| `YFex/src/Validation/YFex.Rules.Data/SqlRuleStore.cs` `CreateTableScript` | `RuleStoreDatabase` | 4 (adds Oracle) | 5-tuple of types from a `switch`, one table + index |
| `YFex.Security/.../Storage/Migrations/MigrationRunner.cs` | none (SQLite only, Dapper) | 1 | own `_migrations` table + `CREATE TABLE IF NOT EXISTS` |
| `YFex.Data/src/YFex.Data/Schema/VersionTable.cs` | none ("ANSI") | — | one fixed script |
| `YFex/src/Persistence/YFex.Persistence.Sqlite/*` | — | SQLite | **out of scope** (local app-state storage, AGENTS.md) |

Usages of the two `Statements(SqlEngine)` methods: tests (`DataTests`, `SqlStoreTests`, `SqlExternalStoreTests`),
both AOT smoke apps, and `YFex.Offline.Benchmarks`. No host runs them yet, so no deployed database depends on the
exact text (to confirm, ⚑DS3).

### 4.1 What the `switch` encodes (and the model must express)

| Intent | Offline / Authentication today | Portable form ([02](02-schema-model.md)) |
|---|---|---|
| id (GUID as text) | `varchar(36)` / `text` | `ColumnType.AsciiString(36)` |
| short name, indexable | `nvarchar(400)` / `text`; `varchar(32)`; `nvarchar(12)` | `ColumnType.String(400)`, `AsciiString(32)`, `String(12)` |
| key, indexable (SQL Server 900-byte index limit) | `nvarchar(450)` / `text` | `ColumnType.String(450)` |
| unbounded text | `nvarchar(max)` / `text` | `ColumnType.String()` |
| instant (Unix ms), counters | `bigint` / SQLite `integer` | `ColumnType.Int64` |
| small enum | `smallint` / SQLite `integer` | `ColumnType.Int16` |
| bytes, unbounded | `bytea` / `varbinary(max)` / `blob` | `ColumnType.Binary()` |
| bytes, bounded (MAC, hash) | `varbinary(64)` / `bytea` / `blob` | `ColumnType.Binary(64)` |
| JSON document | `nvarchar(max)` / `text` | `ColumnType.String()` (⚑DS9) |
| unique, NULLs never collide | SQL Server filtered index; others plain | `.Unique(ignoreNulls: true)` |

### 4.2 Problems this causes

1. **Two selectors.** The host passes `SqliteDialect.Instance` to `YFexConnectionFactory` for the stores and
   `SqlEngine.Sqlite` to the schema. A mismatch (PostgreSQL connection, SQL Server schema) is caught only by the
   database.
2. **N × M growth.** Every new engine edits every library (Offline, Authentication, Rules, …). Every new library
   copies the `switch`.
3. **Duplicated enum.** `YFex.Offline.Data.SqlEngine` and `YFex.Authentication.Data.SqlEngine` are separate types.
   A host using both maps its engine twice. YFex.Data already has `ProviderId` (`ProviderId.Postgres`, …) and the
   dialect.
4. **Oracle is silently unsupported** by Offline and Authentication, even though YFex.Data supports it. A naive
   Oracle branch would also break: `text` is not an Oracle type, unbounded text (`CLOB`) cannot be a key, and a unique
   index on `(realm, operator_code)` *does* collide on `(realm, NULL)` in Oracle. Only all-NULL keys are exempt.
5. **Behaviour differs by engine without anyone deciding it**: string lengths are enforced on SQL Server, not on
   PostgreSQL/SQLite (⚑DS3).
6. **Latent SQL Server failure in Offline.** Four primary keys (`offline_claims`, `offline_spending`,
   `offline_publications`, `offline_deliveries`) combine `nvarchar(400/450)` columns up to ≈1,700–1,850 bytes, over SQL
   Server's 900-byte clustered-key limit. The `create table` succeeds with a warning, and an insert whose key really is
   that long fails. Nothing checks this today; YFSCH003 would ([06 §1](06-consumer-migration.md#1-yfexofflinedata)).
7. **No versioning.** "Run `Statements` from the app's migration" assumes a migration system that YFex does not provide.
   The first column added to `offline_receipts` has nowhere to go.

## 5. DML: what is already portable and what is not

The stores' DML is mostly portable. They use `Connections.Dialect.ParameterPrefix`, plain `INSERT/UPDATE/DELETE/SELECT`,
compare-and-set on `version` columns, and no paging or engine functions. Remaining engine-sensitive spots:

| Spot | Where | Issue |
|---|---|---|
| "Insert, false if the key exists" | `OfflineSqlStore.TryInsertAsync`, `SqlExternalStores` (2×), Authentication `DuplicateAsync` | **any `DbException` is read as a duplicate** (a timeout, a dropped connection or a missing table would be reported as "duplicate") |
| Upsert as update-then-insert-then-update | `OfflineSqlStore.UpsertAsync` | three round trips; `IQueryDialect.BuildUpsert` already exists |
| Retry on conflict | `SqlReconciliationStores` `catch (DbException) when (attempt < 2)` | same blanket catch |
| Store plumbing | `OfflineSqlStore` and Authentication's `SqlStores` base | the same helpers twice (connection per call, `P`, `Ms`, `At`, row consumers) |

[03 §7](03-ddl-dialects-and-rendering.md#7-dml-gaps-found-on-the-way) proposes `BuildInsertIfAbsent` and
`ClassifyError(DbException)` on the dialect, so stores stop using exceptions for control flow.
