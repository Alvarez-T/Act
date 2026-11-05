# YFex.Data — portable schema model, DDL dialects and migrations — Design

> **Status:** every ⚑DS recommendation approved by the user (2026-10-22). **DS-P1 implemented 2026-10-23** in
> `C:\Dev\YFex\YFex.Data` (model, four generators, validator, LINQ ban; record in [07 §DS-P1 record](07-phases-and-acceptance.md#ds-p1-implementation-record-2026-10-23)).
> **DS-P0 (provider split) implemented 2026-10-23**: `YFex.Data.<Engine>` (no DataMining) + `YFex.DataMining.<Engine>`
> (record in [07 §DS-P0 record](07-phases-and-acceptance.md#ds-p0-implementation-record-2026-10-23)).
> **DS-P2 (versioned modules, `yfex_schema_history`, locks, migrator, `SchemaRegistry`) implemented 2026-10-23**; legacy
> `ITable`/`TableCreator`/`VersionTable` deleted (record in [07 §DS-P2 record](07-phases-and-acceptance.md#ds-p2-implementation-record-2026-10-23);
> user guide `YFex.Data/docs/SCHEMA.md`). **DS-P3 implemented 2026-10-23**: YFex.Offline (`yfex.offline`), YFex.Authentication
> (`yfex.authentication`) and Rules.Data (`yfex.rules`) declare their tables as modules; `SqlEngine`/`RuleStoreDatabase` deleted;
> v1 corrected in place (Oracle reserved `session`, SQL Server key widths); record in
> [07 §DS-P3 record](07-phases-and-acceptance.md#ds-p3-implementation-record-2026-10-23).
> **DS-P4 implemented 2026-10-27**: `[DataTable]` row classes → generated `Schema`/`Columns`/`BulkTable`/`BulkMapper`,
> compile-time YFSCH diagnostics, `SchemaAssert.InSync` printing the next version; pilot `RuleDataRow` in Rules.Data; record in
> [07 §DS-P4 record](07-phases-and-acceptance.md#ds-p4-implementation-record-2026-10-27).
> **DS-P5 implemented 2026-10-27**: EF bridge (`ApplyYFexModule`/`CreateYFexSchemaHistory`/`RevertYFexModule`, `EfSchemaRenderer`,
> EF model → `TableSchema`, `InstallYFexRowSecurity`, catalog-vs-model check, `ProviderDialects` registered by the engine packages);
> record in [07 §DS-P5 record](07-phases-and-acceptance.md#ds-p5-implementation-record-2026-10-27); user guide `YFex.Data/docs/SCHEMA.md` §10.
> **DS-P6 implemented 2026-10-27**: `BuildInsertIfAbsent` and `ClassifyError` (`DataErrorKind`) on the four dialects, `DialectStatement`,
> connection-per-call helpers on `YFexConnectionFactory`, `RowSecurityKey.SqlType` derived from modules; Offline.Data and Authentication.Data
> drop every catch-all `DbException` (upserts in one statement, Oracle `''` review); record in
> [07 §DS-P6 record](07-phases-and-acceptance.md#ds-p6-implementation-record-2026-10-27). Next: DS-P7 (optional: live verification, a fifth engine).
> **EF vs own model vs generator:** evaluated in [08](08-evaluation-ef-vs-own-model-vs-generator.md). Verdict: own C# model as
> the core, an optional source-generated front-end over row classes, EF only as a bridge.
> **Why:** the user asked (2026-10-22) to review the Entity Framework integration and the schema (table/column) creation
> path. Today every library that owns tables writes its own DDL strings and picks types with a `switch` on an engine
> enum of its own (`SqlEngine` in YFex.Offline.Data **and** YFex.Authentication.Data, `RuleStoreDatabase` in
> YFex.Rules.Data). Each new engine (MySQL, MariaDB, Firebird, DB2, …) would mean editing every library's schema.
> **Repo:** `C:\Dev\YFex\YFex.Data` (`data.slnx`). Consumers changed in later phases: YFex.Offline, YFex.Authentication,
> YFex (Rules.Data), YFex.Security.
> **Audience:** the user first, then the sessions that implement the phases ([07](07-phases-and-acceptance.md)).
> **Normative vs sketch:** every code block is a proposal until the ⚑DS flags are answered; names are marked `// (sketch)`.

## 1. The one sentence

**A library declares *what* its tables are (columns with portable types, keys, indexes, versions); the dialect of the
connection decides *how* that is written for its engine.** Libraries never name an engine again. Adding an engine means
one schema generator in that engine's package. No consumer changes.

```
   YFex.Offline.Data          YFex.Authentication.Data          YFex.Rules.Data          (any library)
   OfflineSqlSchema.Module    AuthenticationSqlSchema.Module    RuleStoreSchema.Module
          │  SchemaModule: versions of portable operations (CreateTable, AddColumn, CreateIndex, …)
          ▼
   ┌─────────────────────────── YFex.Data (core, AOT) ───────────────────────────┐
   │ ColumnType · ColumnSchema · IndexSchema · TableSchema · SchemaModule           │
   │ SchemaValidator (per-dialect diagnostics) · SchemaMigrator (history + lock)  │
   │ ISchemaDialect ──► SchemaSqlGenerator (ANSI base: CREATE TABLE/INDEX …)      │
   └───────┬───────────────┬───────────────┬───────────────┬───────────────┬─────┘
           ▼               ▼               ▼               ▼               ▼
     YFex.Sqlite     YFex.Postgres   YFex.SqlServer   YFex.Oracle    YFex.Data.EntityFramework
     type map +      type map +      type map +       type map +     (a) EF migrations run YFex modules
     few overrides   few overrides   filtered index   function idx   (b) any EF provider renders DDL
                                                                     (c) EF model → TableSchema/BulkTable
```

## 2. Documents

| # | Document | What it answers |
|---|---|---|
| 01 | [Current state and inventory](01-current-state-and-inventory.md) | Where YFex.Data stands (phases done/open), what EF integration does today, every hand-written DDL site, the bugs in `TableCreator`/`VersionTable` |
| 02 | [Schema model](02-schema-model.md) | `ColumnType`, `ColumnSchema`, `IndexSchema`, `TableSchema`, `SchemaModule`, builder, validation, link to `BulkTable` |
| 03 | [DDL dialects and rendering](03-ddl-dialects-and-rendering.md) | `ISchemaDialect`, the generator base, per-engine type matrix, semantic features (unique-ignoring-nulls), adding an engine, the DML gaps (insert-if-absent, error classification) |
| 04 | [Migrations and versioning](04-migrations-and-versioning.md) | Module versions, `yfex_schema_history`, locking, transactional vs non-transactional DDL, script vs apply, replacing `TableCreator`/`VersionTable` |
| 05 | [Entity Framework integration](05-entity-framework-integration.md) | What the EF bridge lacks and the proposed additions (migration operations, EF-rendered DDL, model export, row-security DDL) |
| 06 | [Consumer migration](06-consumer-migration.md) | Before/after for Offline.Data, Authentication.Data, Rules.Data, YFex.Security; removing `SqlEngine` |
| 07 | [Phases and acceptance](07-phases-and-acceptance.md) | DS-P0…DS-P7, acceptance criteria, test and AOT gates |
| 08 | [Evaluation: EF Core vs FluentMigrator vs own model vs generator](08-evaluation-ef-vs-own-model-vs-generator.md) | Why the core is our own C# model, what a generator adds (and cannot do: migration history), where EF still fits |
| 09 | [Library conventions](09-library-conventions.md) | Own types used and added, LINQ ban and enforcement, the hot paths and their memory techniques, `unsafe` policy, AOT rules and gates |

## 3. Principles

1. **Semantics in the model, syntax in the dialect.** The model says "unique, but rows whose `operator_code` is NULL do not
   collide". SQL Server renders a filtered index, Oracle a function-based index, PostgreSQL and SQLite a plain unique
   index. Today that knowledge sits in `AuthenticationSqlSchema` as `engine == SqlEngine.SqlServer ? … : …`, and the
   Oracle case is wrong because Oracle has no branch at all.
2. **One selector.** The dialect on `YFexConnectionFactory` already picks the parameter prefix, upserts, paging, bulk
   path and row-security DDL. DDL must use the same object. Today a host passes `PostgresDialect` to the stores and
   `SqlEngine.PostgreSql` to the schema separately, and nothing checks that the two agree.
3. **Optional capability interfaces**, like `IRowSecurityDialect` and `IBulkWriteDialect`: `ISchemaDialect` is
   implemented by dialects that can render DDL. `IQueryDialect` stays small.
4. **Domain-neutral core.** YFex.Data owns the model, generator base, validator and migrator. Table definitions stay in
   the owning library. (AGENTS.md: "domain-specific mapping patterns live in configuration, samples, or adapters".)
5. **AOT first.** The model is plain objects built in code (no reflection, no attributes required). The EF bridge is
   the only non-AOT piece, because EF migrations are not supported under Native AOT. It is isolated and annotated (⚑DS12).
6. **Script and apply are the same code.** As `RowSecurityInstaller` does today (`Script…` / `Install…Async`), every
   schema change can be emitted as SQL for a DBA or applied through a connection.
7. **Escape hatch, never silent.** Engine-specific SQL is allowed in a migration step only as an explicit
   `SqlStep.For(provider, sql)` map. A missing engine fails validation; it is never skipped.
8. **YFex library conventions are normative** ([09](09-library-conventions.md)): own value types (`SqlIdentifier`,
   `SqlTableName`, `SchemaModuleName`, `SchemaVersionNumber`, `Hash256`, `ProviderId`) instead of `string`/`int`;
   no LINQ in new code (banned-API analyzer); spans, pooling and measured `unsafe` only on the named hot paths; every
   new file AOT-safe, with the AOT smoke as a gate.

## 4. What this does **not** do

- Generate C# from a database (reverse engineering). DataMining's introspection (`IDataSource.IntrospectAsync`) covers
  discovery, and it is still a TODO ([01 §1](01-current-state-and-inventory.md#1-yfexdata-where-it-stands)).
- Replace EF Core migrations for apps that already use them. [05](05-entity-framework-integration.md) plugs YFex
  modules *into* EF migrations.
- Cover local app-state storage (`YFex.Persistence`, e.g. `SqliteKeyValueStore`). AGENTS.md keeps that out of the
  SQL layer.
- Auto-diff a desired model against a live database in the first phases (⚑DS2). Verification against the live
  database comes later (DS-P7).

## 5. Decisions for the user

| Flag | Question | Recommendation |
|---|---|---|
| ⚑DS1 | Replace `ITable` / `TableCreator` / `VersioningTableCreator` / `TablesProcessor` / `TableVersionRepository` / `VersionTable` with the new model and migrator (breaking; the only caller is `DataAccessTests`)? | **Yes**: delete them in DS-P2; no shim |
| ⚑DS2 | Migration style: ordered versions of portable operations per module (Flyway-like, explicit), or a desired-state model with automatic diff (EF-like)? | **Ordered operations.** The current shape of every table is derived by replaying them in memory. Diff only for *verification* (DS-P7) and for the row-class sync test (08 §6.2) |
| ⚑DS3 | Bounded strings on PostgreSQL: `varchar(n)` (same limit as SQL Server/Oracle) or `text` (today)? | **`varchar(n)`.** Today a 500-character `display_name` fails on SQL Server (`nvarchar(400)`) and succeeds on PostgreSQL. Offline/Authentication data shipped on 2026-10-18/19; confirm no PostgreSQL database is deployed yet |
| ⚑DS4 | History: a new `yfex_schema_history` keyed by (module, version), or extend `TABLE_VERSION`? | **New table**, and drop `TABLE_VERSION` |
| ⚑DS5 | Apps on EF migrations: apply YFex modules from inside EF migrations (`migrationBuilder.ApplyYFexModule`) **and** record them in `yfex_schema_history`, so the YFex migrator never re-applies them? | **Yes, both** |
| ⚑DS6 | Provider split (deferred as Q7 in `DATA-ACCESS-AND-BULK-WRITE.md` §8): `YFex.Data.<Engine>` (dialect, bulk writer, DDL, row-security DDL, driver) and `YFex.DataMining.<Engine>` (mining provider, interceptor)? | **Yes**, as DS-P0, independent of the rest. A host that only stores Offline data in PostgreSQL should not load MemoryPack, YamlDotNet and Microsoft.Extensions.AI |
| ⚑DS7 | Apply migrations at startup? | **Off by default.** `ApplyOnStartup` opt-in for desktop/SQLite and development; servers run `ScriptSchema` or a CLI step |
| ⚑DS8 | Identifiers stored as text (`AsciiString(36)`, as today) or the native GUID type for new tables? | **Keep text** for the existing modules (portable across engines and offline envelopes); native `Guid` is available for new tables |
| ⚑DS9 | Authentication's `authentication_providers.descriptor`: `Json` (PostgreSQL `jsonb`, which normalizes the text) or opaque `String()`? | **`String()`**: nothing queries inside it, and `jsonb` would change the stored bytes |
| ⚑DS10 | Generator attributes: define YFex's own (`[DataTable]`, `[DataIndex]`, `[Ascii]`, `[Precision]`) **and** honour EF's `[Index]`/`[PrimaryKey]`/`[Unicode]`/`[Precision]` by metadata name, or only one set? | **Both**, read by name, so no package reference. YFex's set is the documented one; EF's set lets one class serve an EF model too |
| ⚑DS11 | Should Offline/Authentication stores move from ordinal readers + hand-built parameters to generated row classes (DS-P4), or keep fluent tables only? | **Fluent only in DS-P3.** Decide row classes per library later, when its DML is touched anyway; pilot the generator on Rules.Data and new tables |
| ⚑DS12 | The EF bridge cannot be AOT-safe (EF itself is not). Keep it isolated and annotated (`[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`), or drop it? | **Keep, isolated + annotated** (09 §4.1) |
| ⚑DS13 | Enforce "no LINQ" with `BannedApiAnalyzers` in YFex.Data, marking the existing uses (42 found) with a `#pragma` + reason, or ban only in new projects? | **Ban in YFex.Data too**, so every existing use is visible and new ones fail the build; rewriting the hot ones is a separate measured task |
