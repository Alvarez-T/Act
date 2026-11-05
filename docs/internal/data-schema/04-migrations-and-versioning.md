# 04 — Migrations and versioning

Replaces `TableCreator`, `VersioningTableCreator`, `TablesProcessor`, `TableVersionRepository` and `VersionTable`
(⚑DS1, ⚑DS4).

## 1. Model: modules with ordered versions (⚑DS2)

A `SchemaModule` ([02 §4](02-schema-model.md#4-schemamodule-the-unit-a-library-owns)) is a list of versions 1…N, each
a list of portable steps. Version 1 of an existing library is "create every table it has today". A later change adds a
version and **never edits an applied one**: the checksum check (§3) refuses that.

```csharp
// (sketch)
public static SchemaModule Module { get; } = new SchemaModule("yfex.offline")
    .Version(1, "Central stores (offline 11 Phase 3e)", v => v
        .CreateTable(Receipts).CreateTable(Sessions).CreateTable(Sequences) /* … 12 tables, indexes inside each TableSchema */)
    .Version(2, "Receipts keep the terminal clock skew", v => v
        .AddColumn(Receipts.Table, new ColumnSchema("clock_skew_ms", ColumnType.Int64, nullable: true)));
```

Why ordered steps rather than a desired-state diff: each change is explicit and reviewable. No diff engine has to guess
renames versus drop+add. And the rendering of each step is a small, testable function. `SchemaModule.Current` gives
the desired state for free, by replay.

## 2. History table

Defined with the model itself and created through `CreateTableIfNotExists` (the one bootstrap statement):

```csharp
// (sketch)
TableSchema.Create("yfex_schema_history", t => t
    .Column("module", ColumnType.AsciiString(100))
    .Column("version", ColumnType.Int32)
    .Column("description", ColumnType.String(400))
    .Column("checksum", ColumnType.Binary(32))               // Hash256 of the canonical steps (09 §1.3)
    .Column("applied_at", ColumnType.Int64)                  // Unix ms, same convention as the stores
    .Column("applied_by", ColumnType.String(200), nullable: true)
    .Column("duration_ms", ColumnType.Int64)
    .Column("statements", ColumnType.Int32)
    .Column("state", ColumnType.Int16)                       // 0 = completed, 1 = partially applied (non-transactional DDL, §4)
    .PrimaryKey("module", "version"));
```

Table name configurable (`SchemaApplyOptions.HistoryTable`) for databases with naming rules. It does not store the
rendered script, so there is no `VARCHAR(4000)` ceiling. The script can be regenerated from the module at any
version, on any engine.

## 3. Applying a module

```csharp
// (sketch) — extension methods, same pattern as RowSecurityInstaller
public static class SchemaInstaller
{
    public static SqlQuery[] ScriptSchema(this IQueryDialect dialect, SchemaModule module,
        SchemaVersionNumber? from = null, SchemaVersionNumber? to = null);                       // DBA / CI artefact; no connection

    public static Task<SchemaApplyResult> ApplySchemaAsync(this YFexConnection connection,
        ReadOnlyMemory<SchemaModule> modules, SchemaApplyOptions? options = null, CancellationToken cancellationToken = default);

    public static Task<SchemaModuleStatus[]> GetSchemaStatusAsync(this YFexConnection connection,
        ReadOnlyMemory<SchemaModule> modules, CancellationToken cancellationToken = default);   // applied vs pending, checksum drift
}
```

`ApplySchemaAsync`, per module:

1. Require `connection.Dialect is ISchemaDialect` (else `NotSupportedException` naming the dialect).
2. `SchemaValidator.Validate(module, dialect)`. Any error stops the run before any statement.
3. Bootstrap `yfex_schema_history` (`CreateTableIfNotExists`).
4. Acquire the migration lock for the module ([03 §4](03-ddl-dialects-and-rendering.md#4-semantic-features-the-dialect-must-honour)).
5. Read applied versions. For each applied version whose checksum differs from the code, **throw**
   `SchemaDriftException` (module, version, expected, actual). A row in state `1` (partially applied) also stops the run
   with instructions (§4).
6. For each pending version, in order: render its steps against `SchemaState` at the previous version, execute, and
   insert the history row.
7. Release the lock. Return per-module results (versions applied, statements, duration).

Row security: `ApplySchemaAsync` runs under `RowSecurityContext.Bypass()` for application mode. In database mode, DDL
needs an owner/DBA connection anyway, and the doc says so.

## 4. Transactions

| Engine | `Features.TransactionalDdl` | Unit of work |
|---|---|---|
| PostgreSQL, SQLite | yes | one transaction per version: steps + history row commit together; a failure leaves nothing behind |
| SQL Server | yes (for the step set in v1) | same |
| Oracle, MySQL | no (each DDL commits implicitly) | history row inserted with `state = 1` **before** the first statement, updated to `0` after the last. A failure leaves `state = 1` and the failing step index in `description`. The next run refuses with a message naming the statement, until an operator fixes the database and calls `MarkVersionResolvedAsync(module, version)` |

`YFexConnection.StartScopedTransaction` is used for the transactional engines, so an app that applies schema inside its
own scope joins it.

## 5. The escape hatch: `SqlStep`

```csharp
// (sketch)
v.Sql(SqlStep.For(ProviderId.Postgres, "create index ix_x on t using brin (created_at)")
             .For(ProviderId.SqlServer, "create index ix_x on t (created_at) with (data_compression = page)")
             .Otherwise(SqlStep.Skip("BRIN-like index is an optimisation only")));
```

- A provider with no entry and no `Otherwise` fails validation (YFSCH006). Nothing is skipped silently. `Skip` must
  say why.
- `SqlStep` cannot change `SchemaState`. If it changes a table's shape, the module must also state the change with a
  portable step, or `Current` would lie. Validation cannot detect this, so code review must.
- Its checksum covers every provider's text, so editing the Oracle text of an applied version is drift on every engine.
  That is intentional.

## 6. Hosting (⚑DS7)

```csharp
// (sketch)
services.AddYFexOfflineData(connections);            // registers OfflineSqlSchema.Module into SchemaRegistry
services.AddYFexAuthenticationData(connections);     // registers AuthenticationSqlSchema.Module

// Option A (default for servers): a CLI / deployment step
//   dotnet run --project MyHost -- schema script --dialect postgres --out ./schema.sql
//   dotnet run --project MyHost -- schema apply
// Option B (desktop / SQLite / development): opt-in at startup
services.AddYFexSchemaMigration(o => o.ApplyOnStartup = true);   // IHostedService: ApplySchemaAsync(registry.Modules)
```

`SchemaRegistry` (singleton, in YFex.Data) is just the list of modules the libraries registered. Order between modules
does not matter unless a module declares `DependsOn("yfex.authentication")`, for cross-module foreign keys, which are
not used today.

## 7. What happens to the existing types

| Today | Becomes |
|---|---|
| `ITable`, `ITableCreator<T>`, `TableCreator<T>` | removed; a table is a `TableSchema`, creation is a `CreateTableStep` |
| `VersioningTableCreator<T>`, `TableVersionRepository`, `VersionTable` / `TABLE_VERSION` | removed; `yfex_schema_history` + `ApplySchemaAsync` |
| `IMultiTableCreator<T>`, `TablesProcessor<T>` | removed; a module is the multi-table unit |
| `TableAlreadyExistsException` | removed; replaced by `SchemaDriftException` and `SchemaValidationException` |
| `DataAccessTests` cases for `TableVersionRepository` | rewritten against `ApplySchemaAsync` on SQLite |
