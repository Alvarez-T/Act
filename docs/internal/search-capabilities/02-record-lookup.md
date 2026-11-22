# 02 — Record lookup: "pick one record of type X"

> Owns the contract that Forms (⚑FM20) consumes. Decisions ⚑SK8–⚑SK13, ⚑SK17, ⚑SK29. All code is a *proposed* API.

## 1. The need

```
 Supplier  [ Acme Distrib|                    ▾ ]      ← user types; the field holds a SupplierId, shows the name
           ┌──────────────────────────────────────┐
           │ Acme Distribuidora Ltda   12.345.678/0001-90 │
           │ Acme Farma                São Paulo         │
           │ ＋ Create supplier "Acme Distrib"            │  ← optional, ⚑SK17
           └──────────────────────────────────────┘
```

One question asked in five places, by different callers:

| Caller | Question | Today |
|---|---|---|
| Forms `YLookup<SupplierId>` editor | search by text; show the name of the stored id | nothing |
| CRUD list grid column `Supplier` | names for 50 ids on one page | nothing |
| Universal search (Tier 1 "entity providers") | records matching the text, any type | designed as a separate provider family, not built |
| Verb + subject ("histórico maria") | records of the type a verb acts on | designed, not built |
| AI agent / paste fill | turn "Distribuidora Acme" into a `SupplierId` before calling a capability | nothing |

**Proposal: one contract per record type, `IRecordLookup<TId>`, in `YFex.Search.Public`; every caller above uses it.**
Universal search gets records by wrapping lookups as providers (⚑SK10), so "entity provider" stops being a second contract.

## 2. Knowing that a type is a record id (⚑SK8, request to core)

The Forms generator picks the editor at compile time, Capabilities must see that a parameter is a record reference, and
Validation's JSON schema should say so to the AI. A naming convention (`*Id`) is a guess. Proposal: a static-abstract
interface in core, declared once on the id type, the same way `[Sensitive]` and `[InputHints]` are declared once.

```csharp
// YFex core (proposed). TSelf is the id value type; the record kind is a stable text name.
public interface IRecordId<TSelf> : IEquatable<TSelf>, ISpanFormattable, ISpanParsable<TSelf>
    where TSelf : struct, IRecordId<TSelf>
{
    static abstract RecordKind Kind { get; }          // e.g. RecordKind.Of("supplier")
}

public readonly record struct RecordKind { /* stable lower-case name; catalog label key "Record.supplier" */ }
public readonly record struct RecordKey(RecordKind Kind, string Id);   // canonical text form of any record id, for frecency/audit/AI
```

```csharp
// Domain (YFex.Farma) — before
[ValueObject<int>]
public readonly partial record struct SupplierId;

// after: one line; Vogen already generates parse/format
[ValueObject<int>]
public readonly partial record struct SupplierId : IRecordId<SupplierId>
{
    public static RecordKind Kind => RecordKinds.Supplier;
}
```

Cons: every id type needs the line (once); Vogen's generated members must satisfy the interface (spike in SK-P2);
`RecordKind` is a new core type. Rejected: `[RecordId]` attribute (generators can read it, but runtime callers — the AI
tool, Capabilities `BySubject` — would need a registry anyway); per-app runtime registration only (the Forms generator
cannot see it).

## 3. The contract (⚑SK9)

```csharp
namespace YFex.Search.Public;

public interface IRecordLookup<TId> where TId : struct, IRecordId<TId>
{
    RecordLookupMetadata Metadata { get; }

    // search-as-you-type: already permission-filtered, normalized scores, capped by query.Take
    IAsyncEnumerable<RecordChoice<TId>> SearchAsync(LookupQuery query, SearchContext context, CancellationToken ct);

    // "show the name, not the number" — for one stored id and for a page of ids (grid columns)
    ValueTask<RecordChoice<TId>?> ResolveAsync(TId id, SearchContext context, CancellationToken ct);
    ValueTask<IReadOnlyDictionary<TId, RecordChoice<TId>>> ResolveManyAsync(
        IReadOnlyCollection<TId> ids, SearchContext context, CancellationToken ct);
}

public sealed record RecordLookupMetadata(
    ResourceKey Label,                 // "Supplier" (catalog key)
    CostTier Cost,                     // Instant for small cached lists, Remote for DB
    int MinQueryLength = 1,
    int PageSize = 20,
    CapabilityKey? Create = null,      // ⚑SK17: "Create supplier 'Acme'" row
    bool InUniversalSearch = true);    // ⚑SK10

public readonly record struct RecordChoice<TId>(
    TId Id,
    string Display,                    // "Acme Distribuidora Ltda" — masked by [Sensitive] rules when needed
    string? Detail = null,             // second line: document, city…
    MatchScore Score = default,
    MatchRanges Ranges = default,
    RecordChoiceState State = RecordChoiceState.Active);   // Active | Inactive | Restricted | Missing

public sealed record LookupQuery(QuerySignature Signature, int Take, object? Filter = null);   // Filter: ⚑SK11
```

Rules every implementation follows (tests in the shared contract test kit, SK-P2):

- **Permission before results.** Records the caller may not read are never returned by `SearchAsync`. `ResolveAsync` of an
  id the caller cannot read returns `State = Restricted` with a neutral display ("— restricted —"), never the name.
- **Inactive / deleted records.** Not offered by `SearchAsync`; `ResolveAsync` still names them with `State = Inactive`,
  so an old document still shows "Acme (inactive)". A missing id returns `Missing`, and the editor shows the raw key.
- **Stable order.** Same tie-breakers as the orchestrator (score, then display), so typing one more character does not
  reshuffle.
- **Batching.** `ResolveManyAsync` is one round trip per page; the editor caches resolved names per screen.

## 4. Implementations through CQRS (⚑SK12)

A lookup that reads the database directly would skip the CQRS authorizer, query cache and offline cache. Proposal: a bridge
package `YFex.Search.Cqrs` builds a lookup from two app queries.

```csharp
// Domain: two ordinary queries (the app already writes the list query for the CRUD screen)
public sealed record SearchSuppliers(string Text, string Digits, int Take)
    : IQuery<IReadOnlyList<SupplierChoice>>, ICacheable;
public sealed record SupplierNames(IReadOnlyList<SupplierId> Ids)
    : IQuery<IReadOnlyList<SupplierChoice>>, ICacheable;
public sealed record SupplierChoice(SupplierId Id, string Name, string Document, bool IsActive);

// Composition root
services.AddRecordLookup<SupplierId>(lookup => lookup
    .Label("Supplier")
    .Search(q => new SearchSuppliers(q.Signature.NormalizedFolded, q.Signature.DigitsOnly, q.Take))
    .Resolve(ids => new SupplierNames(ids))
    .Display(c => c.Name, detail: c => c.Document)
    .Active(c => c.IsActive)
    .Create<CreateSupplier>());                 // ⚑SK17, optional
```

The bridge scores the returned rows with the shared `FuzzyScorer` when the query returns them unscored. The database side
(folded-name column, digits-only document column with a unique index, trigram index) is the app's migration; a Data helper
for those columns is a request (⚑SK30).

Cons: two queries per record type (the resolve query is small); `ICacheable` caching of a search query keyed by text can
grow — the bridge sets a short TTL. Hand-written `IRecordLookup<TId>` stays possible (external registry over YFex.Web,
an in-memory enum-like table).

## 5. Forms consumer (Forms ⚑FM20 stays the consumer side)

```csharp
// Forms generator (YFex.UI.Forms) — chosen at compile time because SupplierId : IRecordId<SupplierId>
TControl Lookup<T>(IFieldShape<T> field) where T : struct, IRecordId<T>;

// Avalonia: YLookup<TId> = searchable YComboBox (C7, remote mode) bound to a LookupViewModel<TId>
//   - resolves the current value on load (ResolveAsync), shows Display + State badge
//   - debounced SearchAsync with sequence numbers; Enter picks the top hit
//   - "Create …" row when Metadata.Create is set and CanInvoke allows it
```

No `ProductSearch.Provider` hand-off as in forms 01 §5.5: the editor resolves `IRecordLookup<ProductId>` from DI with a
closed generic (`GetService<IRecordLookup<ProductId>>()`, AOT-safe). An app override per type stays available.

Dependent lookups ("City of the selected State"): the field passes a typed filter (⚑SK11).

```csharp
[Field(LookupFilter = nameof(CityFilter))] public partial CityId City { get; set; }
CityFilter CityFilter => new(State);           // re-queried when State changes (inferred dependency, forms ⚑FM11)
```

## 6. AI and paste fill

- The JSON schema of a command/form marks a record reference: `"supplierId": { "type": "string", "x-yfex-record": "supplier" }`
  (request to Validation's `JsonSchemaWriter`, ⚑SK29).
- The agent gets one tool, `find_record(kind, text, take)`, over the registered lookups (by `RecordKind`), and passes the
  chosen `RecordKey` text back as the argument. A non-unique answer goes back to the user as a choice, never auto-picked.
- Paste fill uses the same tool locally (no AI): "CNPJ 12.345.678/0001-90" → document detector → `SearchAsync` → exact hit.

## 7. Universal search and record actions

- `AddRecordLookup` also registers a `RecordLookupSearchProvider<TId>` (`ISearchProvider`, `Category = Entity`) unless
  `InUniversalSearch = false`. Its hits carry `SearchAction.OpenRecord(RecordKey)`.
- Each record hit is expanded into its top record actions ("Edit — Maria", "History — Maria"): capabilities whose single
  required parameter is that record type, from Capabilities' `BySubject(Type)` index (⚑SK13), permission-filtered.

## 8. Cross-platform, AOT, privacy

- No OS code. Closed-generic DI resolution only; the CQRS bridge takes delegates, no reflection.
- `Display` and `Detail` may hold personal data: they follow the record's `[Sensitive]` masks in view mode; queries and
  results are never logged (probes record lengths, counts and timings only, ⚑SK24).
