# 04 — Search engine: what changes in YFex.Search

> Decisions ⚑SK2, ⚑SK4, ⚑SK7, ⚑SK16, ⚑SK18, ⚑SK24, ⚑SK25, ⚑SK28, ⚑SK31, ⚑SK32, ⚑SK42, ⚑SK43. The pipeline, ranking formula
> and frecency of [universal-search-design](../universal-search-design.md) §3–§7 stay as built; this file lists only the
> changes. All code is a *proposed* API.

## 1. One catalog search (⚑SK2)

```csharp
// before: two engines over the same registry
ICapabilitySearch capSearch;                       // YFex.Capabilities — scope-aware, own fold + score, own SearchHit
CapabilityCatalogProvider provider;                // YFex.Search.Capabilities — shared scorer, base registry only

// after: the provider is the only one; it asks Capabilities for the per-identity registry
public CapabilityCatalogProvider(CompiledCapabilityRegistry registry, IScopeContext? scope, …)
ISearchRegistry current = scope is { } s ? registry.SearchFor(s.Current) : registry.Search;   // scoped caps appear
```

`ICapabilitySearch`, `CapabilitySearchService` and `YFex.Capabilities.SearchHit` are removed (breaking; no caller outside
the repo's tests and AOT smoke). Callers that want "just search capabilities" register `AddYFexSearch()` +
`AddCapabilityCatalogProvider()`. The visibility rule (policy, else permission hint) moves into one internal helper.

## 2. Text folding has one owner (⚑SK4)

`TextNormalization.Fold/Tokenize/DigitsOnly` stay in `YFex.Search` and become span-based (fold into a caller buffer, no
`string.Normalize` per keystroke for ASCII input). UI controls that filter text (searchable `YComboBox`, NavBar search,
`YIconPicker`) take matching through a small seam in `YFex.UI.Public` that an app fills with Search's folding; their
built-in fallback is ordinal-ignore-case. Rejected: folding in core (core holds types, not behaviour); a third copy in UI.

## 3. Formats are declared by the app (⚑SK18)

```csharp
// before (built): the infrastructure enum names Brazilian documents
public enum FormatKind : byte { Text, Cpf, Cnpj, CpfCnpj, Rg, Email, Telefone, Ean, Custom = 255 }

// after: a value object; the domain declares its formats once
public readonly record struct QueryFormat(string Name);                 // in YFex.Search.Public
public static class BrFormats { public static readonly QueryFormat Cpf = new("br.cpf"); … }   // YFex.Domain
public readonly record struct FormatFlag(QueryFormat Format, Confidence Confidence);
// SearchHit.Satisfies : QueryFormat?
```

Breaking for `.Public`; no external implementer exists yet. Detectors stay one-liners over the domain value types. Email
and phone come from core `Contacts` value types and may ship as built-in detectors in `YFex.Search`.

## 4. Typed actions (⚑SK7)

```csharp
// before: RunCapability(string CapabilityId) | OpenEntity(Type EntityType, object Id) | NavigateTo(Type RouteType, object? Argument)
// after (needs YFex.Capabilities.Public, ⚑SK3; and core RecordKey, ⚑SK8):
public readonly union SearchAction(RunCapability, OpenRecord, NavigateTo);
public sealed record RunCapability(CapabilityKey Key, CapabilityArguments Arguments);   // record actions carry the id
public sealed record OpenRecord(RecordKey Record);                                       // no boxed id, no Type
public sealed record NavigateTo(Type RouteType, object? Argument = null);                // unchanged until NavigatR.Public names a route value
```

Selecting a hit goes to the invoker (03 §3) for `RunCapability`, to the record's "open" capability (or its CRUD route) for
`OpenRecord`, and to NavigatR for `NavigateTo`.

## 5. Orchestrator: stream per provider, with deadlines (⚑SK42, ⚑SK31)

Built: each tier waits for all of its providers (`Task.WhenAll`), then the next tier starts. One slow Remote provider holds
back every Remote hit. Proposal:

- Instant tier ranked and emitted at once (unchanged).
- Fast and Remote providers start together; each provider's batch is merged and emitted as it arrives, as a **ranked
  replacement list** (`SearchUpdate { Sequence, Hits }`), so the UI re-renders one stable list instead of appending.
- A per-provider deadline (default 300 ms) uses YFex.Resilience's per-attempt timeout; no retries (a search result is stale
  in a second). A late provider is dropped for that query and counted (telemetry).
- Stale sequence drop moves into the orchestrator (the caller passes the sequence; superseded queries are cancelled and their
  late batches discarded).

## 6. View models (⚑SK16)

New package `YFex.Search.Mvvm` (refs State, Mvvm, `YFex.Search.Public`, `YFex.Capabilities.Public`):

| View model | Used by | Holds |
|---|---|---|
| `SearchBoxViewModel` | `YSearchBox`, `YCommandPalette` | query, debounce, sequence, grouped results, prefix operators (`>` actions, `@` records, `#` record kind), empty-query MRU from frecency, select → invoker / navigation, records the selection in frecency |
| `LookupViewModel<TId>` | `YLookup<TId>` (Forms ⚑FM20), CRUD filter bar | value, resolved display + state, query, results, create row, filter |

Controls bind to small interfaces in `YFex.UI.Public` (YFex.UI never references Mvvm or Search engines).

## 7. Typo tolerance (⚑SK43)

Tier 2, only when Tier 0/1 return nothing: bounded Damerau-Levenshtein (distance ≤ 1 for tokens ≤ 5 chars, ≤ 2 above)
over the prefiltered catalog candidates, written in `YFex.Search` (about 100 lines). Record lookups get typo tolerance from
the database (trigram similarity), not from this tier. Rejected: SymSpell package (free, MIT, but a dictionary index per
culture for a catalog of a few thousand entries is more than needed); Lucene.NET (heavy, index files).

## 8. Privacy, frecency, telemetry, offline

- **Privacy (⚑SK24).** Queries may contain a CPF or a name: never logged; probes record length, digit ratio, recognized
  formats, provider timings and counts. Results are masked by `[Sensitive]` in view rendering. Frecency entries keyed by a
  `RecordKey` are personal data: YFex.Privacy erasure for a record deletes them (`IFrecencyStore.ForgetAsync(RecordKey)`).
  The query cache stays in-process with a short TTL.
- **Frecency vs Profiling (⚑SK25).** Frecency stays in Search: functional, per user, stored locally through Persistence,
  no consent gate. Profiling may consume selection events (consent-gated) for product insight; it does not own ranking.
- **Telemetry (⚑SK32).** `YFex.Diagnostics` probes + `Meter` instruments in core style: keystroke-to-first-paint, provider
  latency and deadline misses, zero-result rate, selected rank. YFex.Logging ships them; no raw text.
- **Offline (⚑SK28).** Capability descriptors gain an availability fact (`RequiresOnline`, or derived from Cqrs `IQueueable`
  = works offline, deferred); the search box greys out online-only actions when YFex.Offline reports offline. Lookups
  resolve names from the CQRS offline cache; searches over Remote lookups say "offline — showing cached results".
