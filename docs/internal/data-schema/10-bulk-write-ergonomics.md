# 10 — Bulk-write ergonomics (one type argument)

> **Question (user, 2026-11-13):** the bulk-write call is too verbose. Can it be simplified, maybe with the source
> generator? **Answer:** yes, and the generator already emitted everything needed.
> **Status: ⚑DS14–⚑DS21 approved and DS-P8 BUILT 2026-11-13** (record in
> [07 §DS-P8 record](07-phases-and-acceptance.md#ds-p8-implementation-record-2026-11-13)). Two things turned out
> differently from the first sketch, both marked **[changed]** below: there is no `List<T>` overload (it ties with the
> span and array overloads for every collection expression), and nested or generic row types never reach the emitter.

## 1. The problem

Every bulk write named the same type three times, and `cancellationToken:` by name because `options` sits before it:

```csharp
await _connection.BulkWriter.WriteAsync(
    AccessRow.BulkTable,
    BulkRows.From<AccessRow, AccessRow.BulkMapper>(rows),
    cancellationToken: ct).ConfigureAwait(false);
```

(`YFex.AgentTelemetry/src/YFex.AgentTelemetry/Store/ReadsStore.cs`.)

`AccessRow.BulkTable` and `AccessRow.BulkMapper` are **both generated** by DS-P4
(`src/YFex.Data.Generator/DataTableEmitter.cs`). So table, mapper and row type are all derivable from the row type
alone: the three arguments carried no information the compiler lacked.

## 2. The design: the row type is its own mapper

`IBulkRowMapper<TRow>` is already `static abstract void Write(ref BulkRowWriter, in TRow)` — nothing requires it to live
on a *separate* struct. One new self-referential interface in `YFex.Data` (`src/YFex.Data/Bulk/BulkContracts.cs`):

```csharp
public interface IBulkRow<TSelf> : IBulkRowMapper<TSelf>
    where TSelf : IBulkRow<TSelf>
{
    static abstract BulkTable BulkTable { get; }
}
```

The emitter puts it in the partial's base list and forwards `Write` to the mapper it already emits:

```csharp
// generated
partial record AccessRow : global::YFex.Data.IBulkRow<global::YFex.AgentTelemetry.Store.AccessRow>
{
    // Columns / Schema / BulkTable / BulkMapper: unchanged.
    // BulkTable (public static property) already satisfies the static abstract property.

    [GeneratedCode…]
    static void global::YFex.Data.IBulkRowMapper<AccessRow>.Write(ref BulkRowWriter writer, in AccessRow row)
        => AccessRow.BulkMapper.Write(ref writer, in row);
}
```

The **explicit** static implementation (⚑DS15) keeps `Write` off the row's public surface and cannot collide with a
hand-written `Write` member. Duplicate interfaces across partial declarations are legal, so a hand-written
`: IBulkRow<T>` on the same type does not break the build.

`BulkMapper` stays public and usable on its own, so every existing call keeps compiling (⚑DS14).

## 3. The write: `BulkWriterExtensions`

An extension block on `IBulkWriter` (`src/YFex.Data/Bulk/BulkWriterExtensions.cs`), one type argument inferred from the
rows:

```csharp
await connection.BulkWriter.WriteAsync(rows, ct);                       // List<T>, T[], ReadOnlyMemory<T>, IReadOnlyList<T>
await connection.BulkWriter.WriteAsync(rows, options, ct);
await connection.BulkWriter.WriteAsync([row, row], ct);                 // collection expression → the span overload
await connection.BulkWriter.WriteAsync(cursor, cancellationToken: ct);  // IAsyncEnumerable<T>
await connection.BulkWriter.WriteAsync(staging, rows, cancellationToken: ct);   // another table
```

The overloads, each with a `(rows, ct)` and a `(rows, options, ct)` form:

| Parameter | Source used | Note |
|---|---|---|
| `ReadOnlySpan<TRow>` | `MemoryBulkRowSource` | what a collection expression binds to; the rows are copied, because a span cannot survive an `await` |
| `TRow[]` | `MemoryBulkRowSource` | exact match, so an array never falls through to the interface overload (§4) |
| `ReadOnlyMemory<TRow>` | `MemoryBulkRowSource` | |
| `IReadOnlyList<TRow>` | `ListBulkRowSource` for a `List<TRow>`, else `ReadOnlyListBulkRowSource` | **[changed]** one type test per write replaces the planned `List<TRow>` overload |
| `IAsyncEnumerable<TRow>` | `AsyncBulkRowSource` (a class) | the extension disposes the enumerator, however the write ends |
| `BulkTable` + `IReadOnlyList<TRow>` | same as the interface overload | the escape hatch: staging table, one partition, a subset of columns |

`ct` is positional because each shape has a `(rows, ct)` overload with no `options` hole — that `cancellationToken:`
label was a third of the old noise.

**Overload resolution.** The instance `IBulkWriter.WriteAsync<TSource>(BulkTable, TSource, …)` still wins wherever it
applies (a real `IBulkRowSource`); the extensions are only reached for collections, which never satisfy that
constraint. `MemoryBulkRowSource<TRow, TRow>` and the list sources needed no change: `TMapper` has no `new()`
constraint and is used only for static calls, so a record *class* as its own mapper is fine.

## 4. Cost and traps

| Item | Note |
|---|---|
| Performance | **Zero change.** `<TRow, TRow>` resolves the constrained static call exactly as `<TRow, TRow.BulkMapper>` did: same devirtualization, no boxing, no new allocation |
| AOT | No reflection added; only generic instantiations over types already instantiated. The AOT smoke publishes with 0 YFex warnings and exits 0 |
| **Trap, arrays** | With `ReadOnlyMemory<TRow>` and `IReadOnlyList<TRow>` overloads but **no `TRow[]`** one, an array argument silently picks the *list* path (array→interface is a standard conversion and beats the user-defined `ReadOnlyMemory` operator), i.e. an interface dispatch per row. The `TRow[]` overload is mandatory, not a convenience; `BulkRowWriteTests.Each_overload_picks_the_row_source_it_promises` pins it |
| **Trap, collection expressions** | **[changed]** A `List<TRow>` overload beside the array one makes **every** `[row, row]` argument ambiguous (CS0121) — it broke six existing `BulkRows.From<…>([…])` call sites the moment a `From(List<TRow>)` overload existed. A `ReadOnlySpan<TRow>` overload beats the array overload for a collection expression but **ties with `List<TRow>`**, so the two cannot coexist: `List` is handled at runtime inside the `IReadOnlyList` overload instead, and `BulkRows` names its list factory `FromList` |
| Generator edge cases | **[changed]** None: YFSCH102 already rejects a nested or generic `[DataTable]` type, so every emitted partial is top-level and non-generic |
| One row type, several tables | Covered by the explicit-table escape hatch; `TRow.BulkTable` is the default only |

## 5. Secondary wins (built with the phase)

- **A real `List<T>` source** (⚑DS18): `ReadOnlyListBulkRowSource` (the former `ListBulkRowSource`) indexes through
  `IReadOnlyList<T>.this[int]`, one interface dispatch per row. `ListBulkRowSource` now holds the concrete `List<TRow>`
  and uses its inlineable indexer. `List` is what call sites actually build (`ReadsStore.cs`). A span cannot be held
  across `await`, so the list itself is the field. Reachable by hand as `BulkRows.FromList`.
- **Streaming** (⚑DS19): `AsyncBulkRowSource<TRow, TMapper>` + `BulkRows.From(IAsyncEnumerable<TRow>)`, so callers stop
  hand-rolling the "must be a `class`" source that `IBulkRowSource`'s own documentation warns about.
- **Connection-level sugar** (⚑DS17): **not built**, deferred as recommended.

## 6. Decisions (approved by the user, 2026-11-13)

| Flag | Question | Decision |
|---|---|---|
| ⚑DS14 | Add `IBulkRow<TSelf>` (row type = its own mapper + its own `BulkTable`) and emit it from the generator? Keep the nested `BulkMapper` struct public? | **Yes**, and **`BulkMapper` kept**: source-compatible, and it stays the way to write a row to another table or through a custom source |
| ⚑DS15 | Implement `Write` implicitly (public `AccessRow.Write(...)`) or as a static **explicit** interface implementation? | **Explicit**: no new public surface on row types, no collision with a hand-written `Write` |
| ⚑DS16 | Overload set: `List<TRow>`, `TRow[]`, `ReadOnlyMemory<TRow>`, `IReadOnlyList<TRow>`, each with a `(rows, ct)` and a `(rows, options, ct)` form, plus the explicit-table escape hatch? | **Yes, except the `List<TRow>` overload** (§4, collection expressions); a `ReadOnlySpan<TRow>` pair was added instead, and `List` is recognized at runtime |
| ⚑DS17 | Also add `YFexConnection.BulkWriteAsync(rows, …)`? | **Deferred.** One more indirection to document for three fewer characters; revisit if call sites ask for it |
| ⚑DS18 | Dedicated `List<TRow>` row source (concrete indexer instead of `IReadOnlyList<T>`)? | **Yes**: free, and `List` is the common shape |
| ⚑DS19 | `IAsyncEnumerable<TRow>` overload + class source in YFex.Data? | **Yes**, in the same phase: it removes the one place where callers must write their own source |
| ⚑DS20 | C# 14 `extension(IBulkWriter writer)` block or a plain static extension class? | **`extension` block** (`LangVersion preview` on `net11.0` takes it) |
| ⚑DS21 | Migrate the existing call sites in DS-P8, or leave them until each file is touched? | **Migrate every one that can use the new form.** Of the 25 `BulkWriter.WriteAsync` call sites, 5 wrote a `[DataTable]` row to its own table (migrated); the other 20 pass a hand-built `BulkTable` or a custom `IBulkRowSource` (row-security tests, failure-injection sources, the logging-style mapper) and stay explicit by design |

## 7. Documents updated when it landed

- `YFex.Data/docs/DATA-ACCESS-AND-BULK-WRITE.md` §4.0: the one-argument form leads; the explicit `BulkTable` +
  `BulkRows.From` form is the escape hatch. The `BulkRows` sketch in §4.2 matches the real API again.
- `YFex.Data/docs/SCHEMA.md` §(generated members): `IBulkRow<TSelf>` row added.
- `src/YFex.Data/Schema/DataTableAttributes.cs`: the `[DataTable]` summary lists what the generator emits.
