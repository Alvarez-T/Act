# 05 — Entity Framework integration

Today `YFex.Data.EntityFramework` is **row-level security only** ([01 §3](01-current-state-and-inventory.md#3-entity-framework-integration-today-srcyfexdataentityframework)).
This document lists what is missing, in order of value, and how each piece plugs into the schema model. Everything here
stays in `YFex.Data.EntityFramework` (refs `Microsoft.EntityFrameworkCore.Relational` only, no provider packages).

EF Core's Native AOT support is experimental and does not cover migrations. **This project is the non-AOT edge of the
design**: nothing in `YFex.Data` may depend on it, and AOT hosts use the YFex generators ([03](03-ddl-dialects-and-rendering.md)).
Every public API here carries `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]`, and its own code still follows
the conventions (own types, no LINQ): [09 §4.1](09-library-conventions.md#41-the-one-exception-the-ef-bridge-ds12), ⚑DS12.

## 1. Who needs what

| App | Schema owner | Need |
|---|---|---|
| A. YFex.Data only (Offline central, Authentication server) | YFex migrator | nothing from EF |
| B. EF Core app that also hosts YFex libraries (e.g. an ERP back office on EF that adds YFex.Authentication.Data) | EF migrations | E1: YFex modules applied **inside** EF migrations, one history the team already watches |
| C. Any app on an engine with an EF provider but no YFex generator (MySQL, Firebird, DB2, …) | either | E2: DDL rendered by the EF provider |
| D. EF app that bulk-loads (imports, DataMining targets) | EF | E3: `BulkTable` / `TableSchema` from the EF model |
| E. EF app with row security in database mode | EF | E4: policies installed from migrations; catalog checked against the model |

## 2. E1: run a YFex module from an EF migration (⚑DS5)

```csharp
// (sketch) — an EF migration in the app
public partial class AddAuthentication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.ApplyYFexModule(AuthenticationSqlSchema.Module, toVersion: 3);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.RevertYFexModule(AuthenticationSqlSchema.Module, toVersion: 0);   // only when every step is reversible
}
```

`ApplyYFexModule` translates each portable step into EF `MigrationOperation`s:

| YFex step | EF operation |
|---|---|
| `CreateTableStep` | `CreateTableOperation` (+ `AddColumnOperation` per column with `ClrType`, `ColumnType` = null, `MaxLength`, `IsUnicode`, `Precision`, `Scale`, `IsNullable`; `AddPrimaryKeyOperation`; `CreateIndexOperation` per index) |
| `AddColumnStep` / `DropColumnStep` / `RenameColumnStep` | `AddColumnOperation` / `DropColumnOperation` / `RenameColumnOperation` |
| `CreateIndexStep` / `DropIndexStep` | `CreateIndexOperation` / `DropIndexOperation` |
| `SqlStep` | `SqlOperation` with the text for `migrationBuilder.ActiveProvider` (mapped to a `ProviderId`, §5) |
| `RowSecurityStep` | `SqlOperation`s from the `IRowSecurityDialect` scripts |
| (end of each version) | `SqlOperation` inserting the `yfex_schema_history` row, so the YFex migrator sees the version as applied (⚑DS5) |

Leaving `ColumnType` null and passing `ClrType` + facets lets **the EF provider pick the store type**. That keeps
EF-created tables identical to what the EF provider would create for an equivalent entity. The exception is the
unique-NULL semantics (02 §2). EF's own SQL Server convention filters only for entities, so for raw operations the
bridge sets `CreateIndexOperation.Filter` itself when `ActiveProvider` is SQL Server. Oracle function-based indexes
become a `SqlOperation`.

**History bootstrap:** the first EF migration that uses a YFex module calls `migrationBuilder.CreateYFexSchemaHistory()`
once. It is a plain `CreateTableOperation` of the history `TableSchema`, so it works on engines without a YFex dialect.
EF's own history guarantees it runs once.

**Checksum note:** the history row written from EF stores the same portable checksum, so switching an app between
"EF owns migrations" and "YFex migrator owns migrations" is safe in both directions.

## 3. E2: render YFex DDL through any EF provider

```csharp
// (sketch)
public sealed class EfSchemaRenderer
{
    public EfSchemaRenderer(DbContext context);   // any context configured with the target provider; no model needed
    public IReadOnlyList<SqlQuery> Script(SchemaModule module, int fromVersion = 0, int? toVersion = null);
}
```

It builds the operations of §2 and calls `context.GetService<IMigrationsSqlGenerator>().Generate(operations, model: null)`.
Use it in a design-time tool or a test to produce `schema.mysql.sql`, for engines where no YFex `SchemaSqlGenerator`
exists. It does **not** make `ApplySchemaAsync` work on those engines. Runtime apply still needs an `ISchemaDialect`.
Lock statements and the history bootstrap are YFex's, so this renderer covers the steps and the history insert only.

## 4. E3: EF model → `TableSchema` / `BulkTable`

```csharp
// (sketch)
TableSchema schema = context.Model.FindEntityType(typeof(Order))!.ToYFexTableSchema();
BulkTable table = schema.ToBulkTable();
await connection.BulkWriter.WriteAsync(table, BulkRows.From<Order, OrderMapper>(orders));
```

Reads the relational model (`GetTableName`, `GetSchema`, `GetColumnName`, `GetMaxLength`, `IsUnicode`, `GetPrecision`,
`IsNullable`, keys, indexes, `ValueGenerated.OnAdd` → identity, value converters → provider CLR type). Unmappable
properties (owned JSON collections, spatial) raise a clear error naming the property. It uses EF metadata at runtime,
so it is a startup or test-time call, not a hot path. Cache the `BulkTable`.

The same export lets a test assert that an EF-owned table and a YFex module agree
(`SchemaAssert.Equivalent(efSchema, module.Current["orders"])`), for apps that touch one table from both sides.

## 5. E4 + E5: row security and dialect inference

- `migrationBuilder.InstallYFexRowSecurity(RowSecurityCatalog catalog, RowSecurityOptions options)`: `SqlOperation`s
  from `IRowSecurityDialect` for the active provider. The dialect is resolved as below.
- **Startup check** (in `UseYFexRowSecurity`, once per model): every policy's table and key columns exist in the EF
  model. Each key column's CLR type converts to the policy's `DbType`. A typo in `[RowSecurityKey(Column = "TENANTID")]`
  otherwise becomes a database error at the first query (application mode) or a policy that filters on nothing
  (database mode).
- **Dialect inference (E5):** `EfProviderDialects.Register("Npgsql.EntityFrameworkCore.PostgreSQL", PostgresDialect.Instance)`.
  Each `YFex.Data.<Engine>` package registers its own pair through a module initializer, which is AOT-safe and needs no
  scanning. `UseYFexRowSecurity(rowSecurity)` then looks up `Database.ProviderName`. The explicit-dialect overload stays.

## 6. Not planned

- Generating EF entity classes from YFex modules, or YFex modules from EF migrations' C#. One source of truth per
  table: a library's tables are owned by its YFex module; an app's own tables by EF.
- A YFex `IMigrationsSqlGenerator` replacement inside EF. EF providers already do that job better for their engines.

## 7. Tests

`YFex.Data.EntityFramework.Tests` (SQLite today) gains:

- `ApplyYFexModule` on the SQLite EF provider: tables equal (via E3 export) to `module.Current`, history rows present,
  a later `ApplySchemaAsync` is a no-op;
- `EfSchemaRenderer` output for SQLite and (when the package is restorable) the SQL Server provider, compared with golden
  files;
- E3 export of the existing test entities;
- the startup catalog-vs-model check failing on a misspelt key column.
