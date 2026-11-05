# 08 — Evaluation: EF Core, an existing migrator, our own model, or a source generator

> **Question (user, 2026-10-22):** EF Core was only the example. The goal is to model tables with **C# objects** instead
> of engine-specific SQL text. Is EF Core better for this, or our own way? Could generators simplify things and help
> performance?
> **Answer:** **our own model, as C# objects, is the core.** A **source generator over attributed row classes** is an
> optional authoring front-end on top of it, not a replacement. **EF Core stays a bridge** (05), never the schema engine
> for YFex libraries.

## 1. Options evaluated

| | Option | In one line |
|---|---|---|
| A | **EF Core owns the schema** | each library ships entity classes + a `DbContext` + EF migrations |
| B | **EF Core as renderer only** | our neutral steps → EF `MigrationOperation`s → the EF provider's SQL (05 §3) |
| F | **FluentMigrator** | off-the-shelf C# fluent migrations (`Create.Table("x").WithColumn("id").AsString(36).PrimaryKey()`) with its own processors per engine |
| C | **Own model, fluent C#** | `TableSchema.Create(...)`, `SchemaModule.Version(...)` (02–04 as written) |
| D | **Own model, generated** | attributed `partial` row classes; YFex.Data.Generator emits the `TableSchema` (and the readers/mappers it already emits) |
| C+D | **Hybrid (recommended)** | C is the runtime model and the migration-step language; D produces the *current shape* of a table from its row class; a test keeps both in sync |

## 2. Facts that decide it (checked 2026-10-22)

| Fact | Source |
|---|---|
| EF Core Native AOT is "highly experimental… not yet suited for production use", and Microsoft "recommend[s] against deploying EF NativeAOT applications in production". The page covers queries; migrations are not part of the AOT story | learn.microsoft.com, *NativeAOT Support and Precompiled Queries (Experimental)* |
| EF migrations are scaffolded for **the active provider only**. Several engines means "maintaining multiple sets of migrations, one for each provider, and adding a migration to each for every model change" | learn.microsoft.com, *Migrations with Multiple Providers* |
| `Oracle.EntityFrameworkCore` latest is 10.23.26400 (2026-10-20), depends on EF Core Relational **10.x** (< 11). YFex is on .NET 11 / EF 11 preview.4 | nuget.org |
| EF Core 9 `Microsoft.EntityFrameworkCore` + `.Relational` ≈ 4.7 MB of IL (local package cache), before any provider. All of `YFex.Data.dll` is 240 KB | local `~/.nuget/packages`, `bin/Release` |
| FluentMigrator latest release 8.0.1 (2026-01-21, netstandard2.0). Native AOT PR #2252 merged 2026-05-28 for the 9.0 milestone: discovery is still reflection by default (AOT path = `WithTypes(...)` by hand, **no source generator**); runner, version-table accessor and per-provider generators "not yet audited"; DataAnnotations validation is not trim-safe | nuget.org, github.com/fluentmigrator PR #2252 |
| YFex.Data.Generator already reads `[Table]`, `[Column]`, `[Key]`, `[NotMapped]`, `[DatabaseGenerated]` **by metadata name**, without referencing their packages, and already emits `RowReader<T>`, `RowShape<T>` and parameter binders | `src/YFex.Data.Generator/DataRowParser.cs` |
| Offline/Authentication stores read rows **by ordinal into domain records** and build parameters by hand. They have no row classes today | `OfflineSqlStore`, Authentication `SqlStores` |

## 3. Scorecard

✔ good · ~ workable with cost · ✘ fails the requirement

| Criterion (from the user and AGENTS.md) | A EF owns | B EF renders | F FluentMigrator | C own fluent | D own generated | C+D |
|---|---|---|---|---|---|---|
| Tables as C# objects, no engine SQL in libraries | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| **One** definition for every engine (no N×M) | ✘ one migration set per provider per library | ✔ | ✔ syntax-level; semantic differences still need `IfDatabase(...)` | ✔ semantic | ✔ semantic | ✔ |
| Native AOT / trimming (preserve AOT paths) | ✘ experimental, not for production | ✘ | ~ unreleased, partly unaudited | ✔ | ✔ | ✔ |
| Same engine selector as DML (`YFexConnectionFactory.Dialect`) | ✘ provider + dialect | ~ | ✘ processor id + dialect | ✔ | ✔ | ✔ |
| New engine without touching libraries | ~ needs an EF provider **and** new migration sets | ✔ if an EF provider exists | ✔ if a processor exists | ✔ one generator class | ✔ | ✔ |
| Release cadence (YFex on .NET 11 preview) | ✘ Oracle EF not on 11; providers lag each major | ✘ same | ✔ netstandard2.0 | ✔ ours | ✔ | ✔ |
| Dependency weight for a library | ✘ ≈4.7 MB + provider per engine | ✘ | ~ runner + processors | ✔ none | ✔ none (analyzer only at build) | ✔ |
| Library-owned modules sharing one database | ~ one context + history table per library | n/a | ~ global `VersionInfo`; per-library tables configurable | ✔ module = version line | ✔ | ✔ |
| Integrates bulk (`BulkTable`), row security, value objects | ✘ separate world | ~ | ✘ | ✔ | ✔ | ✔ |
| Compile-time diagnostics (unbounded key, nullable PK, identifier length…) | ~ some at model build (runtime) | — | ✘ runtime | ~ in tests (`SchemaAssert`) | ✔ as you type | ✔ |
| Shared source for DDL and DML (column names, readers, bulk mapper) | ✔ (if you also use EF for DML) | ✘ | ✘ | ~ string column names | ✔ typed constants + generated readers/mappers | ✔ |
| Schema diff / scaffold the next migration | ✔ best in class | ✘ | ✘ hand-written | ✘ hand-written | ~ shape only | ~ sync test prints the missing steps |
| Effort for us | low per engine, high overall (N sets) | low | low to adopt, plus wrappers | medium (02–04) | medium, on top of C | C, then D |

## 4. Why not EF Core as the engine (A)

1. **It recreates the N×M problem.** A library such as YFex.Authentication.Data would ship one migration set per provider,
   and add a migration to each set for every change. That is the `switch (SqlEngine)` again, in generated files.
2. **AOT.** YFex.Offline/Authentication are `IsAotCompatible` with AOT smoke apps at 0 warnings. EF migrations would be the
   only non-AOT component, and Microsoft itself advises against EF AOT in production.
3. **Cadence and coverage.** Oracle has no EF 11 provider today. Each EF major waits for every provider; our dialects do
   not.
4. **Two data stacks.** The stores' DML is hand-written over YFex.Data. EF would be loaded only to create tables.
5. **Where EF *is* better:** model diffing and `migrations add` scaffolding, for an *application* with one engine and EF
   for DML. Those apps keep EF and run YFex modules inside their migrations (05 §2). For an engine with an EF provider
   but no YFex generator, EF renders our steps (option B, design-time, 05 §3).

## 5. Why not FluentMigrator (F), even though it is the closest fit

- AOT is not shipped (latest release 8.0.1) and, per its own PR, discovery needs manual `WithTypes(...)`, with the
  runner and per-provider generators not yet audited. We would be early adopters of an unreleased path on a preview
  runtime.
- It renders **syntax**, not **semantics**. The unique-index-with-NULLs case still needs `IfDatabase("SqlServer")…` and
  an Oracle branch in each library: the same per-engine branching we are removing.
- It brings its own engine selector (processor ids) and connections, so it would not share the dialect, the row-security
  installer, `BulkTable` or YFex value objects.
- What we would build instead is a **closed, small** subset (02 §4: create/drop table, add/drop/rename column,
  create/drop index, FKs, `SqlStep`, row security). That is a medium effort, and we own the AOT and semantics guarantees.

If the user prefers to buy rather than build, F is the only candidate. Revisit it when FluentMigrator 9 ships with a
discovery source generator and audited processors.

## 6. Generators: what they add, and what they cannot do

### 6.1 What D looks like

```csharp
// (sketch) — a row class owned by the library; standard attributes where they exist
[DataTable("offline_receipts")]
[DataIndex("ix_offline_receipts_processed", nameof(ProcessedAt))]
public sealed partial record ReceiptRow(
    [property: Key, StringLength(36), Ascii] string IdempotencyKey,   // as built (DS-P4): [MaxLength] raises IL2026 under AOT
    short Outcome,
    short ErrorKind,
    string? Detail,                 // NRT → nullable column; unbounded string
    long Findings,
    long ProcessedAt);

// generated (sketch)
partial record ReceiptRow
{
    public static TableSchema Schema { get; }                  // same model as option C
    public static BulkTable BulkTable { get; }                 // Schema.ToBulkTable()
    public static class Columns { public const string ProcessedAt = "processed_at"; /* … */ }
    // + RowReader<ReceiptRow>, RowShape<ReceiptRow>, IBulkRowMapper<ReceiptRow> (the generator already emits the first two)
}
```

- **Type mapping from the CLR type**: `bool/short/int/long/double/Guid/DateTime/DateTimeOffset/byte[]` map directly.
  `decimal` requires `[Precision]` (else error). `string` requires `[MaxLength]` when it is a key or indexed (else
  YFSCH002 at **compile time**). Enums map to their underlying integer. NRT `?` or `Nullable<T>` means a nullable column.
  YFex value objects are not mapped in v1 (diagnostic: use a primitive row type).
- **Attributes read by metadata name**, no package reference: DataAnnotations `[Key]`, `[Column]`, `[Table]`,
  `[MaxLength]`/`[StringLength]`, `[Required]`, `[NotMapped]`, `[DatabaseGenerated]`. EF's `[Index]`, `[PrimaryKey]`,
  `[Unicode]`, `[Precision]` are honoured if present, so one class can also serve an EF model. YFex.Data defines
  `[DataTable]` (opt-in, fast `ForAttributeWithMetadataName`), `[DataIndex]`, `[Ascii]` and `[Precision]` for code that
  does not reference EF (⚑DS10). `[Column(TypeName = …)]` is rejected: it is engine-specific.
- **Names**: `[Column]` wins; otherwise snake_case of the member (assembly option), consistent with
  `RowMapping.MatchNamesWithUnderscores` on the read side.
- **Compile-time diagnostics**: the engine-independent YFSCH rules (unbounded key, nullable key, invalid identifier,
  identifier longer than the strictest supported engine (PostgreSQL 63), duplicate names, missing precision). Engine
  limits that depend on configuration (SQL Server key bytes, Oracle 30) stay in `SchemaValidator`/`SchemaAssert`.

### 6.2 What a generator cannot do: migration history

A class describes the table **as it is now**. A migration must describe **how it got there**, and an applied version must
never change. If version 1 were `CreateTable(ReceiptRow.Schema)`, adding a property to `ReceiptRow` would silently
rewrite version 1. Every deployed database would then report drift, or worse, never get the column. EF solves this with
generated migration files and a model snapshot, written by a CLI tool. A source generator cannot write files into the
project.

So in C+D:

- **Versions are always frozen fluent steps (C).** Version 1 of an existing table is written out once.
- **The class is the current shape (D).**
- **A sync test** keeps them honest and does the scaffolding work:

```csharp
// (sketch) — one test per module
[Fact] public void Schema_matches_row_classes()
    => SchemaAssert.InSync(OfflineSqlSchema.Module, ReceiptRow.Schema, SessionRow.Schema /* … */);
// On failure it prints the missing steps as C#, ready to paste into the next version:
//   .Version(2, "…", v => v
//       .AddColumn("offline_receipts", new ColumnSchema("clock_skew_ms", ColumnType.Int64, nullable: true)))
```

That gives most of EF's `migrations add` experience with no tool and no runtime diff, and it works under AOT.

### 6.3 Performance: honest accounting

- **DDL is not a hot path.** It runs once per deployment. Building a `TableSchema` takes microseconds whether fluent or
  generated, and round trips dominate the apply. A generator does **not** make schema creation measurably faster. The
  one DDL optimisation worth doing is sending a version's statements as one batch on engines that accept multi-statement
  batches (PostgreSQL, SQL Server), which is a migrator detail.
- **Startup.** EF builds its model at first use and needs a compiled model for AOT. A static `TableSchema` (fluent or
  generated) has no comparable cost. Not measured here, because it does not change the decision: A and B are already out
  on AOT.
- **Where generators do pay off is DML**, as stage 2 already measured: generated `RowReader<T>` with typed getters
  instead of reflection or hand-written ordinal code, generated parameter binding, and `IBulkRowMapper<T>`. Tying the
  schema to the same row class means `ReceiptRow.BulkTable` + its mapper feed `IBulkWriter` with no hand-written column
  list. That is the real simplification D brings, and it matters only for libraries that adopt row classes for DML.

## 7. Recommendation

1. **Build C first** (DS-P1/P2 unchanged). It is what every other piece consumes: migrator, validator, EF bridge, and the
   generator's own output type. Consumers migrate with fluent tables (DS-P3, mechanical).
2. **Add D as a new phase, DS-P4** ([07](07-phases-and-acceptance.md)): `[DataTable]` + attributes, emitted
   `Schema`/`Columns`/`BulkTable`/`IBulkRowMapper<T>`, compile-time YFSCH diagnostics, `SchemaAssert.InSync` with step
   printing. Pilot it where row classes are natural (new tables, Rules.Data's single table). Offline/Authentication adopt
   it only if their stores move from ordinal readers to row classes, which is a separate decision (⚑DS11).
3. **EF stays a bridge** (DS-P5): EF apps run YFex modules inside their migrations; EF renders our steps for engines
   with no YFex generator.
4. **No FluentMigrator now**; revisit when 9.x ships a discovery generator and audited processors.
