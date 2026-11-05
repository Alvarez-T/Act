# 09 — Library conventions applied to this design

> **Requirement (user, 2026-10-22):** the schema work follows the YFex library conventions:
> 1. prefer **own types** over native ones;
> 2. avoid LINQ, **especially in hot paths**;
> 3. in hot paths, reduce memory with **spans / unsafe code**;
> 4. **AOT-friendly**: the rest of the ecosystem may not be yet, but **every new line must be**.
>
> This document is normative for every phase in [07](07-phases-and-acceptance.md). Where an earlier sketch (02–05, 08)
> disagrees, this one wins. The sketches were updated to match.

## 1. Own types

### 1.1 Types reused from YFex core and YFex.Data

| Concept | Type | Where it lives |
|---|---|---|
| Statement text | `SqlQuery` | YFex.Data |
| Database engine | `ProviderId` | YFex.Data. **No new engine enum**: `SqlEngine` and `RuleStoreDatabase` are removed (06) |
| Portable column kind | `BulkColumnType` (wrapped by `ColumnType`) | YFex.Data |
| Module checksum | **`Hash256`** (SHA-256 of the canonical step bytes, §1.3) | YFex core `YFex.Cryptography` |
| Fast non-cryptographic keys (statement caches, golden-file ids) | `StableHash64` | YFex core `YFex.Helpers` |
| Deterministic encoding contract | `ICanonicalValue<TSelf>` (implemented by the new value types) | YFex core `YFex.Canonical` |
| Pooled builders | `RefArrayPoolBufferWriter<T>`, `ArrayPoolBufferWriter<T>` | YFex core `YFex.Collections` |
| Interop with DataMining introspection | `TableRef`, `ColumnRef`, `TableDescriptor` (conversion only; see §1.2 for why the schema model does not use `TableRef` directly) | YFex.Data |

### 1.2 New value types (same shape as `AccountRealm`, `InstallationId`)

`readonly record struct`, private constructor, validating factory, `ISpanParsable<T>`, `ICanonicalValue<T>`,
`ISpanFormattable`/`IUtf8SpanFormattable`, a `MaxLength` constant, and a `default` that `IsEmpty`. No implicit
conversion from `string`: a conversion that can throw is not implicit.

| Type | Wraps | Rule | Replaces in the earlier sketches |
|---|---|---|---|
| `SqlIdentifier` | `string` | `[A-Za-z_][A-Za-z0-9_]*`, 1–128 chars, case-insensitive equality (ordinal ignore-case), checked with `SearchValues<char>`, no regex | every `string` table/column/index/constraint name |
| `SqlTableName` | `SqlIdentifier? Schema`, `SqlIdentifier Name` | qualified table name; `ToTableRef()` / `TryFrom(TableRef)` for DataMining. `TableRef` stays as it is because introspection must hold *any* source name (`"Order Details"`), which is not a portable identifier | `TableRef` in `TableSchema`, steps, `BulkTable` constructor overload |
| `SchemaModuleName` | `string` | lower-case dotted `[a-z0-9]+(\.[a-z0-9-]+)*`, ≤ 100 | `string` module name |
| `SchemaVersionNumber` | `int` | ≥ 1; `Next()`; ordering | `int version` |
| `ColumnType` | kind + facets | already an own type (02 §1); now also `ICanonicalValue` | — |
| `ColumnDefault` | tagged literal | already own (02 §2) | — |
| `SchemaDiagnostic` | `SchemaDiagnosticCode` (enum `YFSCH001…`), severity, `SqlTableName`, `SqlIdentifier?`, message | no string codes | `string` codes |

Builder ergonomics. Fluent builders accept `string` for literals and convert immediately:
`.Column("processed_at", …)` calls `SqlIdentifier.Parse` and fails **when the module is built**, at type initialization
with the table named in the message, not at apply time. The **model's properties** are always the own types. The
generator (DS-P4) emits `SqlIdentifier` constants (`ReceiptRow.Columns.ProcessedAt`), so generated code never parses at
runtime. **As built (DS-P4):** each is `SqlIdentifier.Parse("…")` once at type initialization (already validated by the
generator, so it cannot throw); a public unchecked factory would let callers put unvalidated text into DDL.

### 1.3 Canonical encoding → checksum

Every step and value type writes its canonical bytes (`ICanonicalValue.TryWriteCanonical`) into an
`ArrayPoolBufferWriter<byte>`. `SHA256.HashData(span)` → `Hash256`. No JSON, no `ToString()`, no culture. The history
table stores it as `Binary(32)` (it was `AsciiString(64)` hex in 04 §2), read back with `GetBytes` into a `stackalloc`
span and `Hash256.Read`, so there is no `byte[]` per row.

### 1.4 Collections on public surfaces

As `BulkTable.Columns` already does: `ReadOnlySpan<T>` over a private array plus a `Count`, not `IReadOnlyList<T>`.
`foreach` over a span allocates nothing and needs no interface dispatch. `TableSchema.Columns`, `.Indexes`,
`.ForeignKeys`, `SchemaVersion.Steps` and `SchemaModule.Versions` follow this. Lookups (`IndexOf(SqlIdentifier)`) are
linear scans: tables have tens of columns, and a dictionary costs more than it saves.

## 2. LINQ

| Code | Rule |
|---|---|
| All new runtime code (YFex.Data `Schema/`, engine generators, migrator, `YFex.Data.<Engine>` packages, EF bridge) | **No `System.Linq`**, hot path or not. Loops over spans/arrays |
| Generated code (DS-P4) | No LINQ, no lambdas, no closures |
| Source generator / analyzer (netstandard2.0) | No LINQ in syntax predicates and transforms (they run on keystrokes); `ImmutableArray<T>.Builder` and loops. The existing `DataRowParser` uses some LINQ; new code does not add more |
| Tests | Free (FluentAssertions etc.) |

**Enforcement** (⚑DS13): `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `N:System.Linq` in `BannedSymbols.txt` for
YFex.Data and every new project. **As built (DS-P1):** `BannedSymbols.txt` holds `N:System.Linq` (banning the `Enumerable` type
alone does not flag extension calls such as `.Select(...)`; the namespace entry does, and it ignores the implicit global
`using`). Projects opt in with `<YFexBanLinq>true</YFexBanLinq>` (YFex.Data, YFex.Sqlite, YFex.Postgres, YFex.SqlServer,
YFex.Oracle). The analyzer found **42** existing call sites (not ~46): YFex.Data 32 in 9 files, the four dialect classes 10.
Each got a `#pragma warning disable RS0030 // <reason>` … `restore` pair (adjacent lines merged into one block).
That makes each existing use explicit and reviewable, and any new one fails the build. Rewriting the hot ones
(`Access/`, `Bulk/`) is a separate, measured task.

## 3. Hot paths: where they are and what each one does

Most of this design is **cold** (it runs at deployment or startup). The rules for cold code are "no LINQ, no boxing,
no avoidable allocations", but no `unsafe`: it would add risk with no measurable gain. The hot paths are few and named:

| Path | Frequency | Technique |
|---|---|---|
| **Generated row reader** (DS-P4) | per row | typed getters (`GetInt64`, `GetInt16`); binary columns that map to fixed-size own types (`Hash256`, `KeyId`) read with `GetBytes(ordinal, 0, span)` into a `stackalloc` buffer, so there is **no `byte[]` per row**; ordinals resolved once in `Bind` (existing `RowReader<T>` contract) |
| **Generated parameter writer / bulk mapper** (DS-P4) | per row | `ParameterWriter` / `BulkRowWriter` typed calls (no boxing up to the driver boundary). At the driver boundary, a dialect passes `ReadOnlyMemory<byte>` where the driver accepts it (Npgsql) and a pooled exact-size array otherwise. That is the one documented allocation the ADO.NET contract forces |
| **Store SQL text** (`BuildInsertIfAbsent`, `BuildUpsert`, generated `SELECT` column lists) | per call | rendered **once per (dialect, table)** and cached: a generated `static` per-row-type cache holding a small array of `(IQueryDialect, SqlQuery)` pairs, found by reference equality (linear scan of ≤ 4 entries, no dictionary, no lock after warm-up: publish with `Interlocked.CompareExchange`). Zero allocation per call after the first |
| **`ClassifyError(DbException)`** (DS-P6) | per failed command (frequent under unique-key races) | `switch` on the driver's integer code, or `SqlState.AsSpan().SequenceEqual("23505")`; no `string` building, no regex, no exception-message parsing |
| **Connection-per-call store helpers** (03 §7) | per call | generic over `TConsumer : struct, IRowConsumer` (existing pattern), no closures; results into an exact-size array when the count is known, else `RefArrayPoolBufferWriter<T>` |
| `TableSchema.ToBulkTable()` | once | cached in a `static` (generated `ReceiptRow.BulkTable`) |

Cold-path techniques, used for consistency rather than speed: DDL is rendered with one `DefaultInterpolatedStringHandler`
(or `RefArrayPoolBufferWriter<char>`) per statement on a `stackalloc` initial buffer; identifiers are validated with `SearchValues<char>`;
checksums use pooled buffers (§1.3).

**`unsafe` policy:** span-based code first. `Unsafe.*` / `MemoryMarshal` (no `AllowUnsafeBlocks` needed) or pointer code
is allowed only in the hot paths above, and only when a BenchmarkDotNet result in the same PR shows it beats the span
version (AGENTS.md: "measured hot-path performance"). Benchmarks live in `tests/YFex.Data.Benchmarks` (new) and in
consumers' existing benchmark projects (`YFex.Offline.Benchmarks`).

## 4. AOT

Every **new** project and every new file in an existing project is AOT-safe from its first commit:

- `<IsAotCompatible>true</IsAotCompatible>` on new projects (`YFex.Data.<Engine>` after DS-P0), and on `YFex.Data`
  (already set). Trim, AOT and single-file analyzers are therefore on, and **0 new IL warnings** is a phase gate.
- **Forbidden in new code:** reflection (`GetProperties`, `GetMethod`, `Activator`, `MakeGenericType`), `dynamic`,
  `Expression.Compile`, reflection-based `System.Text.Json` (use source-generated contexts if JSON is ever needed, e.g.
  a CLI status output), assembly scanning, `[MemoryPackable]` on new types (MemoryPack is the source of today's AOT
  publish warnings, `DATA-ACCESS-AND-BULK-WRITE.md` §11.4), and `Enum.ToString()` / `Enum.Parse` on hot or logged paths
  (explicit `switch` to constant strings).
- **Registration without scanning:** modules are registered explicitly (`services.AddYFexSchemaModule(OfflineSqlSchema.Module)`),
  DI uses factory lambdas (`AddSingleton(sp => …)`), no open-generic resolution. Engine→EF-provider pairs are registered
  through module initializers (05 §5), not by scanning.
- **Generator output is AOT-safe by construction**: plain static code, no reflection fallbacks. When the generator cannot
  map a member, it reports a compile-time error instead of emitting a reflection path (unlike the DML fallback reader,
  which predates this rule).
- **Gates:** `tests/YFex.Data.AotSmoke` builds a module, validates it, scripts it for the four dialects, applies it on
  SQLite and runs `GetSchemaStatusAsync`, published with `PublishAot=true` and exit code 0. Offline's and
  Authentication's AOT smoke apps do the same with their modules (06).

### 4.1 The one exception: the EF bridge (⚑DS12)

`YFex.Data.EntityFramework` (DS-P5) is new code that cannot be AOT-safe, because EF Core's migrations and model
building are not (08 §2). Options:

| Option | Effect |
|---|---|
| **A. Isolate + annotate (recommended)** | stays in its own package, never referenced by `YFex.Data` or any library; every public bridge API carries `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` with the reason, so an AOT app that calls it gets a warning at the call site, not a runtime failure. Its own code still follows §1–§3 (own types, no LINQ) |
| B. Drop the bridge | EF apps run YFex modules with `ApplySchemaAsync` next to their EF migrations (two history tables); no EF-rendered DDL for exotic engines |

## 5. Phase gates added by this document

Each phase in 07 also requires:

1. Banned-API build clean: no new `System.Linq` (§2).
2. Release build: 0 new trim/AOT warnings; the AOT smoke publishes and exits 0 (§4).
3. Public API review: no `string`/`int` where §1.2 defines an own type; collections exposed as spans (§1.4).
4. For DS-P4 and DS-P6: a BenchmarkDotNet run (allocations and time) for each hot path touched, compared with the code it
   replaces (hand-written ordinal readers, catch-all inserts), attached to the phase record. No regression in allocated
   bytes per operation.
