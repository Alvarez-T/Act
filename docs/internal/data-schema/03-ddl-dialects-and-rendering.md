# 03 — DDL dialects and rendering

## 1. Shape: a capability interface plus one generator base class

Like `IRowSecurityDialect` and `IBulkWriteDialect`, DDL is an **optional capability of the dialect**, so the object the
host already passes to `YFexConnectionFactory` is the only engine selector.

```csharp
// (sketch)
/// <summary>A dialect that can render the portable schema model (02) as DDL for its engine.</summary>
public interface ISchemaDialect
{
    SchemaSqlGenerator Schema { get; }
}

/// <summary>ANSI DDL for every SchemaStep. Engines override only what differs.</summary>
public abstract class SchemaSqlGenerator
{
    public abstract ProviderId Provider { get; }          // reuse ProviderId (ProviderId.Postgres, …)
    public abstract SchemaFeatures Features { get; }
    public abstract IReadOnlySet<string> ReservedWords { get; }

    public abstract string TypeName(ColumnType type);       // the one method every engine must write

    public virtual IEnumerable<SqlQuery> Build(SchemaStep step, SchemaState before);   // dispatch on the closed step set
    protected virtual IEnumerable<SqlQuery> CreateTable(TableSchema table);
    protected virtual IEnumerable<SqlQuery> CreateIndex(TableRef table, IndexSchema index, TableSchema owner);
    protected virtual IEnumerable<SqlQuery> AddColumn(TableRef table, ColumnSchema column);
    protected virtual IEnumerable<SqlQuery> DropColumn(TableRef table, string column);
    protected virtual IEnumerable<SqlQuery> RenameColumn(TableRef table, string from, string to);
    protected virtual string ColumnDefinition(ColumnSchema column);   // name type [identity] [default] null|not null
    protected virtual string Literal(ColumnDefault value);

    // migrator support (04)
    public virtual SqlQuery CreateTableIfNotExists(TableSchema table);   // history table bootstrap
    public virtual IEnumerable<SqlQuery> AcquireMigrationLock(string module);
    public virtual IEnumerable<SqlQuery> ReleaseMigrationLock(string module);
}

public sealed record SchemaFeatures(
    bool TransactionalDdl,          // PostgreSQL, SQL Server, SQLite: yes. Oracle, MySQL: no (implicit commit)
    int MaxIdentifierLength,
    int MaxIndexKeyBytes,           // 0 = no practical limit
    bool DropColumn, bool RenameColumn,
    bool AlterForeignKeys);         // SQLite: foreign keys only inside create table
// As built (DS-P1): a sealed record with init properties; MaxIndexKeyBytes split into MaxPrimaryKeyBytes (SQL Server clustered
// 900) and MaxIndexKeyBytes (1,700). IndexNamesPerTable was dropped: names are unique schema-wide on every engine (YFSCH005).
```

Why an abstract class behind a one-property interface rather than many interface methods: new steps or hooks become
new **virtual** members, which does not break existing engines. `IRowSecurityDialect` has five methods and can absorb
churn; DDL has far more surface.

Each engine package ships `SqliteSchemaSqlGenerator`, `PostgresSchemaSqlGenerator`, `SqlServerSchemaSqlGenerator` and
`OracleSchemaSqlGenerator`, and its dialect implements `ISchemaDialect`. `SqlDialect` (the generic base) does **not**
implement it: a host on the generic dialect gets a clear `NotSupportedException` from the migrator, not wrong DDL.

## 2. Rendering is pure

`Build` takes model objects and returns `SqlQuery` values. It never touches a connection. The same output feeds:

- `ScriptSchema(dialect, module, from, to)` for a DBA or CI artefact ([04](04-migrations-and-versioning.md));
- `ApplySchemaAsync(connection, modules)`;
- golden-file tests: `tests/YFex.Data.Tests/Schema/Golden/{engine}/{module}.sql`, compared byte for byte.

Output style: lower-case keywords, unquoted identifiers, one statement per `SqlQuery`, no trailing semicolon (SQL Server
batches are split by the migrator, not by `GO`). This matches what Offline and Authentication emit today. For SQLite
and SQL Server, the golden files for those two modules should equal today's `Statements(...)` output, apart from
whitespace and constraint names.

## 3. Type matrix

The engines' columns are normative for the four shipped dialects. They match what the bulk writers already bind
(Oracle `RAW(16)` for `Guid`, `CLOB` for `Json`; PostgreSQL `jsonb`, `timestamptz` written as UTC). MySQL is an
example of a new engine, not part of this plan.

| `ColumnType` | SQLite | PostgreSQL | SQL Server | Oracle | MySQL (example) |
|---|---|---|---|---|---|
| `Boolean` | `integer` | `boolean` | `bit` | `number(1)` | `tinyint(1)` |
| `Int16` | `integer` | `smallint` | `smallint` | `number(5)` | `smallint` |
| `Int32` | `integer` | `integer` | `int` | `number(10)` | `int` |
| `Int64` | `integer` | `bigint` | `bigint` | `number(19)` | `bigint` |
| `Double` | `real` | `double precision` | `float` | `binary_double` | `double` |
| `Decimal(p,s)` | `text` (YFSCH007: SQLite numeric affinity is lossy) | `numeric(p,s)` | `decimal(p,s)` | `number(p,s)` | `decimal(p,s)` |
| `Guid` | `text` | `uuid` | `uniqueidentifier` | `raw(16)` | `char(36)` |
| `DateTime` | `text` | `timestamp` | `datetime2` | `timestamp` | `datetime(6)` |
| `DateTimeOffset` | `text` (YFSCH007) | `timestamptz` | `datetimeoffset` | `timestamp with time zone` | `datetime(6)` UTC (YFSCH007) |
| `String(n)` | `text` | `varchar(n)` (⚑DS3) | `nvarchar(n)` | `varchar2(n char)`; `clob` above 4000 | `varchar(n)` |
| `String()` | `text` | `text` | `nvarchar(max)` | `clob` | `longtext` |
| `AsciiString(n)` | `text` | `varchar(n)` | `varchar(n)` | `varchar2(n char)` | `varchar(n)` |
| `Binary(n)` | `blob` | `bytea` | `varbinary(n)` | `raw(n)`; `blob` above 2000 | `varbinary(n)` |
| `Binary()` | `blob` | `bytea` | `varbinary(max)` | `blob` | `longblob` |
| `Json` | `text` | `jsonb` | `nvarchar(max)` | `clob` | `json` |

SQLite's `integer` for every integer width is what both schemas do today. SQLite also stores `Guid`, `DateTime` and
`decimal` as text, which is how `Microsoft.Data.Sqlite` binds them.

## 4. Semantic features the dialect must honour

| Model meaning | SQLite / PostgreSQL | SQL Server | Oracle |
|---|---|---|---|
| Unique index; a row with a NULL in any indexed column never collides (02 §2) | plain `create unique index` | `create unique index … where c1 is not null [and c2 is not null]` over the **nullable** indexed columns (no `where` when none is nullable) | function-based: `create unique index ux on t (case when c2 is not null then c1 end, case when c2 is not null then c2 end)`. Oracle exempts only all-NULL keys, so `(realm, NULL)` would otherwise collide |
| Primary key | `constraint pk_t primary key (…)` | same | same |
| Identity `Int64` | SQLite: `integer primary key` (rowid alias, single-column key only, else YFSCH005); PostgreSQL: `bigint generated by default as identity` | `bigint identity(1,1)` | `number(19) generated by default as identity` |
| Boolean default literal | `1`/`0`; PostgreSQL `true`/`false` | `1`/`0` | `1`/`0` |
| History-table bootstrap (`CreateTableIfNotExists`) | `create table if not exists` | `if object_id(N'…', N'U') is null create table …` | PL/SQL block that ignores ORA-00955 |
| Migration lock | SQLite: `begin immediate` (the migrator's transaction); PostgreSQL: `select pg_advisory_lock(hashtext('yfex.schema:' \|\| :module))` | `sp_getapplock @Resource = 'yfex.schema:…', @LockMode = 'Exclusive', @LockOwner = 'Session'` | `lock table yfex_schema_history in exclusive mode` inside the version's unit (no `DBMS_LOCK` grant needed) |
| Drop/rename column | SQLite ≥ 3.35 / ≥ 3.25 (validated against `Features`) | `alter table … drop column`; `sp_rename` | `alter table … drop column`; `rename column` |

Column **type changes** are not a portable step in v1. SQLite cannot `alter column` (it needs a table rebuild), and the
others differ in what converts implicitly. A type change is "add the new column + copy with a `SqlStep` + drop the old",
written out in the module, or a per-provider `SqlStep`.

## 5. Adding an engine (acceptance test for "many providers")

1. In the engine package (`YFex.Data.MySql` after DS-P0): `MySqlSchemaSqlGenerator : SchemaSqlGenerator`. Write
   `TypeName`, `Features`, `ReservedWords`, the lock statements, and override only what differs from ANSI.
2. `MySqlDialect : SqlDialect, ISchemaDialect`.
3. Add the engine to the shared contract suite (`SchemaGeneratorContractTests`): golden files for the test module and
   the YFex modules, validation cases, and a live round trip when `YFEX_TEST_MYSQL` is set.
4. **Nothing changes in Offline, Authentication, Rules or any other module.** Their `SchemaAssert.Valid` tests can add
   the new dialect to the list. A module that fails validation on the new engine (e.g. a 70-character identifier on
   MySQL) is caught in its own repo's tests.

### 5.1 Engines without a YFex dialect: render through EF Core

For an engine that has an EF Core provider but no YFex generator, `YFex.Data.EntityFramework` can render the same steps
through that provider's `IMigrationsSqlGenerator` ([05 §3](05-entity-framework-integration.md#3-e2-render-yfex-ddl-through-any-ef-provider)).
That is the "many providers" fallback: correct DDL without writing a generator. It is not AOT-safe and needs a
provider-configured `DbContext`, so it is a design-time / script-generation path. The YFex generators remain the
runtime path.

## 6. Row-security DDL

- `RowSecurityKey.SqlType` (a per-engine type string on a portable attribute) becomes **optional**. When the secured
  table belongs to a module, the policy's key type is `Schema.TypeName(column.Type)` of that column in
  `SchemaState Current`. An explicit `SqlType` still wins, for tables outside any module.
- `RowSecurityStep(RowSecurityPolicy)` lets a module version its policies with its tables: version 3 can add a column
  and the policy that uses it, in one unit. The step renders through the existing `IRowSecurityDialect`
  (`BuildRowSecuritySetup` once per apply, then `BuildCreatePolicy`). `RowSecurityInstaller` stays for catalog-wide
  installs.

## 7. DML gaps found on the way

The stores' DML is mostly portable ([01 §5](01-current-state-and-inventory.md#5-dml-what-is-already-portable-and-what-is-not)).
Two additions to the dialect remove the remaining engine sensitivity:

| Addition | SQLite | PostgreSQL | SQL Server | Oracle |
|---|---|---|---|---|
| `SqlQuery BuildInsertIfAbsent(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)`: returns 1 row affected when inserted, 0 when the key exists. No exception | `insert … on conflict (keys) do nothing` | same | `insert … select … where not exists (select 1 from t with (updlock, holdlock) where keys)` | `merge into t using dual on (keys) when not matched then insert …` |
| `DataErrorKind ClassifyError(DbException)` → `UniqueViolation`, `ForeignKeyViolation`, `Deadlock`, `SerializationFailure`, `Timeout`, `ConnectionFailure`, `Other` | extended codes 2067/1555 (constraint unique/primary key) | SQLSTATE `23505`, `23503`, `40P01`, `40001` | numbers 2627/2601, 547, 1205 | ORA-00001, ORA-02291, ORA-00060, ORA-08177 |

`ClassifyError` needs the driver's exception details (`DbException.SqlState` where the driver sets it, otherwise the
driver's exception type). The dialect packages therefore reference their driver. `YFex.Sqlite` does not today, and
the DS-P0 split ([07](07-phases-and-acceptance.md)) settles that.

Consumers then replace "catch any `DbException` = duplicate" (`OfflineSqlStore.TryInsertAsync`, `SqlExternalStores`,
`SqlReconciliationStores`) with `BuildInsertIfAbsent`, or with `when (dialect.ClassifyError(e) == UniqueViolation)` when
the duplicate comes from a secondary unique index. The connection-per-call helpers duplicated in `OfflineSqlStore` and
Authentication's `SqlStores` (execute, query with a row consumer, try-insert, upsert) are domain-neutral and can
move into `DbExtensions` on `YFexConnectionFactory`. The Unix-millisecond helpers are a consumer convention and stay
in the consumers.
