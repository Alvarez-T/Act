# Universal Search Bar — System Design

**Status:** V0 in progress — the sibling repo `C:\Dev\YFex\YFex.Search` is scaffolded and green (37 tests). Builds on [`capabilities-design.md`](capabilities-design.md) and [`authorization-design.md`](authorization-design.md); framing in [`authorization-capabilities-overview.md`](authorization-capabilities-overview.md). Implementation notes at the end (§16).

The original evaluation is the intent-*scorer* model: a cascade of cheap deterministic detectors that produce **boosts, never hard switches**, with one universal fuzzy scorer as the fallback, two-tier latency, grouped/capped results, and frecency. This document re-plans it on top of the now-designed `YFex.Capabilities` + `YFex.Authorization` foundations, which absorb large parts of the original plan.

---

## 1. What the foundations already give us

| Original-plan concern | Now provided by | Net-new work left |
|---|---|---|
| Static catalog (commands, screens, actions) | **`YFex.Capabilities.ISearchRegistry`** — every capability across the whole system (VMs, CQRS, jobs, routes, APIs), already keyword-indexed and permission-tagged | Fuzzy-scan + rank it; nothing to author |
| Permission filter (filter-before-rank) | **`YFex.Authorization.IPolicyEvaluator.IsAllowed(in Permission, IAuthContext)`** — cheap sync predicate; `ISearchRegistry` entries carry `Permission?` | Call it per candidate; entity providers route through CQRS to inherit authz |
| Format validators + checksums (CPF/CNPJ/EAN/RG) | **`YFex.Entity.Public`** value types — `Cpf`/`Cnpj`/`CpfCnpj`/`Rg`/`Email`/`Telefone` `TryParse` **is** the shape+checksum test | Wrap each `TryParse` as an `IFormatDetector` |
| Keywords, bilingual, accent-fold | **`ICapabilityLocalizer.Keywords(key)`** — union across all configured cultures | Accent-fold at query tokenization |
| LRU query→results cache | **`YFex.Persistence.ICache`** | Wire a small keyed cache |
| Persisted per-user frecency | **`YFex.Persistence.IKeyValueStore`** | Frecency math + read/write |
| Streaming results | **`YFex.Primitives.Collections.PagedList<T> : IAsyncEnumerable<T>`**; core `Fresh/Cached/Stale/Miss` union | Sequence-ID race handling |
| Navigate / invoke a chosen result | **`RouteRegistry`** + capability `CapabilityKey` → the same invoke path the AI uses | Map a hit to its `CapabilityKey` |

**Consequence:** the search bar's Tier-0 static catalog is a thin wrapper over `ISearchRegistry` + a fuzzy scorer + frecency. The genuinely new library is the **orchestrator** (query analysis, two-tier latency, sequence IDs, merge/rank) plus the **fuzzy scorer / normalizers**, and **entity providers** (DB-backed) that live in the domain.

---

## 2. Project layout & dependency layering

```
YFex (core: value objects, Union, PagedList, ArrayPool)
└── YFex.Search.Public            contracts only: ISearchProvider, QuerySignature, IFormatDetector,
    │                             SearchHit, MatchScore, SuggestionKey, ISearchOrchestrator.
    │                             deps: YFex core + YFex.Authorization.Public (Permission/IAuthContext).
    ├── YFex.Search               orchestrator (two-tier, sequence IDs, merge/rank), fuzzy scorer,
    │                             normalizers, frecency store, LRU cache. deps: .Public + YFex.Persistence.
    ├── YFex.Search.Capabilities  the STATIC-CATALOG provider: wraps YFex.Capabilities.ISearchRegistry
    │                             as an ISearchProvider (fuzzy over display+keywords, permission-filtered).
    │                             deps: .Public + YFex.Capabilities + YFex.Authorization.
    └── YFex.Search.Mvvm          SearchBoxViewModel (State/Mvvm): debounce, cancellation, grouped results,
                                  prefix operators, MRU. deps: .Public + YFex.Mvvm.

YFex.Domain repo (sibling)
└── YFex.Search.Entity            domain-side: CPF/CNPJ/RG/Email IFormatDetectors (wrap Entity.Public),
                                  Customer/Product ISearchProviders (CQRS-backed). deps: YFex.Search.Public
                                  + YFex.Entity.Public + the domain's CQRS queries.
```

**Layering rules:** the search core (`.Public`/`.Search`) is **domain-agnostic** — detectors and entity providers are pluggable via DI and live in the domain. This keeps the framework free of any customer/product knowledge (the plan's open/closed point) and keeps the dependency arrow pointing Domain → Framework.

---

## 3. Pipeline (two-tier latency)

```
keystroke
   │
   ├─► Tier 0  (every keystroke, <16ms, no debounce)
   │     QueryAnalyzer → QuerySignature
   │     Static-catalog provider: fuzzy scan ISearchRegistry, permission-filter, frecency-boost
   │     → instant partial results (one frame)
   │
   ├─► Tier 1  (~100ms idle, debounced, cancellable, sequence-numbered)
   │     Fan-out to entity providers (Customer, Product, …) routed by QuerySignature capability
   │     → results stream in via IAsyncEnumerable as each provider returns
   │
   └─► Tier 2  (~300ms / on pause, optional)
         expensive fallbacks: typo-tolerant (SymSpell/Levenshtein), cross-entity
   │
   ▼
Merge & Rank → grouped, capped, frecency-boosted, permission-filtered list
```

Never wait for the slowest provider to paint: Tier-0 catalog results appear within a frame of every keystroke; DB results fill in behind them.

---

## 4. Stage 1 — Query analysis (`QuerySignature`)

Every query passes through `IQueryAnalyzer` producing a **`QuerySignature`** — plain data, not a decision. Detectors are pluggable (`IFormatDetector`); the domain registers the ones wrapping `Entity.Public`:

```csharp
public sealed record QuerySignature(
    string Raw,
    string NormalizedFolded,                 // lowercase + accent-folded
    string DigitsOnly, int DigitCount,
    ImmutableArray<string> Tokens,
    double DigitRatio, int TokenCount,
    ImmutableArray<FormatFlag> Formats);     // detector hits with confidence

public readonly record struct FormatFlag(FormatKind Kind, Confidence Confidence);
public enum Confidence : byte { Weak, Strong, Certain }   // checksum pass = Certain

public interface IFormatDetector
{
    bool TryDetect(ReadOnlySpan<char> normalized, string digitsOnly, out FormatFlag flag);
}
```

Detectors are one-liners over `Entity.Public` (checksums make false positives ~zero → `Certain`):

```csharp
// YFex.Search.Entity (domain repo)
sealed class CpfDetector : IFormatDetector {
    public bool TryDetect(ReadOnlySpan<char> _, string digits, out FormatFlag f) {
        if (Cpf.TryParse(digits, null, out _)) { f = new(FormatKind.Cpf, Confidence.Certain); return true; }
        f = default; return false;
    }
}
// CnpjDetector, EanDetector (mod-10), EmailDetector, TelefoneDetector likewise.
// RgDetector → Rg.TryParse validates SHAPE only → Confidence.Weak (no universal checksum).
```

**Signals are boosts, never gates** (the core design decision). Asymmetry preserved: a `Certain` CPF justifies *skipping* expensive text search (near-zero chance a product name is a valid CPF); a `Weak` RG justifies running document-column search **and** name search in parallel.

---

## 5. Stage 2 — Provider model

The orchestrator knows nothing about customers or products — only the provider contract:

```csharp
public interface ISearchProvider
{
    SearchProviderMetadata Metadata { get; }   // category, min length, cost tier, consumed signals
    IAsyncEnumerable<SearchHit> SearchAsync(QuerySignature sig, SearchContext ctx, CancellationToken ct);
}

public sealed record SearchContext(IAuthContext Principal, long Sequence);   // sequence = keystroke id

public readonly record struct SearchHit(
    SuggestionKey Key,          // stable, dedup + frecency accumulate against this
    MatchScore Score,           // provider-normalized 0..1 (see §6)
    MatchRanges Ranges,         // for highlighting
    SearchAction Action,        // what happens on select (union, below)
    Permission? RequiredPermission);
```

`SearchAction` is a `[Union]` (mirrors `NavigationResult`/`ActionGate`) so "execute a chosen hit" is boxing-free and exhaustive, and it reuses the **capability invoke path**:

```csharp
[Union] public partial struct SearchAction
{
    public partial struct RunCapability(CapabilityKey Key);   // catalog hit → same path AI uses
    public partial struct OpenEntity(Type EntityType, object Id);   // Id is a value object (Cpf/Cnpj/…)
    public partial struct NavigateTo(IRoute Route);          // direct route
}
```

**Three provider families:**
1. **Static catalog** — `YFex.Search.Capabilities`. Fuzzy-scans `ISearchRegistry.Entries` (display + `ICapabilityLocalizer.Keywords`), permission-filters via `IPolicyEvaluator.IsAllowed`, emits `RunCapability(key)`. Sub-millisecond, in-memory, Tier 0. Excludes `CapabilityFlags.Hidden`.
2. **Entity providers** (domain) — DB-backed. Receive the signature and choose a strategy: exact document lookup when a `Certain` CPF/CNPJ/EAN is present, else trigram/prefix name search. **Route through CQRS queries** (`ICacheable` → offline-capable), so **authorization and offline caching are inherited**, not reimplemented. Each returned entity is expanded into generated actions — "Edit — Maria", "View history — Maria" — each mapping to a `CapabilityKey`, so they are permission-gated and invokable by the same machinery.
3. **Composite actions** — "Register customer" style; static at first (a catalog capability), later verb+entity composition (Stage 5).

**Orchestration rules (mandatory):**
- **Caps** — each provider returns top 5–10; ranking happens above.
- **Deadlines** — local providers synchronous-fast; DB providers get ~150–300ms and may return partial.
- **Sequence numbers** — each keystroke increments `SearchContext.Sequence`; responses with a stale sequence are discarded (cancellation alone doesn't stop a task that already completed).
- **Dedup by `SuggestionKey`** — "Edit customer Maria" produced by two paths collapses to one; frecency accumulates against the key.

---

## 6. Stage 3 — Scoring & merging

The hard problem is cross-provider score scales. **Solve it at the provider boundary:** every provider normalizes to `MatchScore` (clamped 0..1) before returning — a value object per the `Length` convention.

```csharp
public readonly record struct MatchScore(double Value) : IComparable<MatchScore>  // clamped [0,1]
{ /* Parse/TryParse/ToString; no implicit string conversion */ }
```

Merge composes:

> **final = normalized match score + intent boost (from `QuerySignature` confidence) + frecency term + small deterministic tie-breakers**

- **Intent boost** — e.g. a `Certain` CPF adds a large boost to Customer-by-document hits and deprioritizes (not kills) text hits.
- **Frecency** — `weight · log(1 + useCount) · recencyDecay` (decay over ~2 weeks), persisted per user via `IKeyValueStore`, keyed by `SuggestionKey`. This is what makes the bar feel like it learns.
- **Tie-breakers** — category, then title. Deterministic + monotonic so typing one more character never reshuffles unrelated results (flicker is the #1 way these bars feel broken).

**Presentation (decided early):** grouped sections — **Actions / Navigate / Customers / Products** — each internally ranked, sections ordered by their best member, each section capped. Grouped lists make "the system understood both interpretations" visible and cheap to scan.

---

## 7. Stage 4 — Speed plan

- **DB groundwork (V1 migration item, not an afterthought):** normalized search columns — digits-only CPF column with a unique index (O(1) exact lookup), accent-folded lowercase name column.
- **Indexing:** prefix via B-tree text-pattern ops; fuzzy name via trigram (`pg_trgm`); full-text/BM25 only if the corpus grows large.
- **Caching:** small LRU `query → results` on `ICache` (users backspace → the previous query's results are exactly what's needed), plus short-TTL cache on recent entity lookups. Reuse the core `Fresh/Cached/Stale/Miss` union for cache state.
- **Prefilters before the expensive fuzzy DP** (per candidate, nanoseconds): character-set bitmask containment, first-char containment, length floor — eliminates ~90% of candidates.
- **Typo tolerance is a fallback tier (Tier 2), never the primary path:** run SymSpell/Levenshtein only when primary tiers return nothing.

---

## 8. Permissions (already solved — just wire it)

- **Catalog hits:** `ISearchRegistry` entries carry `Permission?`; the static-catalog provider calls `IPolicyEvaluator.IsAllowed(perm, ctx)` and drops denied entries **before ranking** (filter-before-rank; never rank-then-filter). `SearchContext.Principal` comes from `IPrincipalAccessor.Current`.
- **Entity hits:** entity providers route through CQRS queries, so the CQRS authorizer (Gate 1) already filters unauthorized rows at execution — unauthorized entities never come back. The generated entity-actions carry `CapabilityKey`s whose permission gates invocation.
- The search bar is a privilege-escalation surface; this filter-before-rank rule is a hard requirement of the provider contract, and it is the **same permission** enforced at execution.

---

## 9. Stage 5 — Verb + subject composition (later phase)

Once basics work, parse `verb + rest`: leading token(s) hitting a verb dictionary make the remainder a *targeted* subject search — "histórico maria" searches Maria but shows only history-type actions (filter capabilities by kind/keyword). No verb → whole string is a subject. Start leading-verb-only; trailing verbs ("maria histórico") are a V3 nicety. The verb dictionary is the same cross-culture keyword data the capabilities keywords already provide.

---

## 10. Search-box ViewModel (`YFex.Search.Mvvm`)

A `StateObject`-based `SearchBoxViewModel`:
- `[Observable] Query` with a **debounced** `[ReactsTo]` driving Tier 1; Tier 0 runs synchronously on each change.
- `[StateCommand]` cancellable async that consumes the orchestrator's `IAsyncEnumerable<SearchHit>`, honoring the current sequence.
- Grouped, capped `StateList<SearchGroup>` for the UI; monotonic updates to avoid flicker.
- **Prefix operators** (`>` commands-only, `@` customers, `#` products — VS Code/Slack pattern): a documented escape hatch that eliminates ambiguity with zero classification logic; parsed into a `QuerySignature` provider filter.
- **Empty-query MRU:** most-recently-used from frecency store.
- Selecting a hit executes its `SearchAction` — `RunCapability` via the capability invoke path, `OpenEntity`/`NavigateTo` via `Navigator`.

---

## 11. i18n & telemetry

- **i18n:** query tokenization lowercases + accent-folds (non-negotiable day one); keyword matching uses `ICapabilityLocalizer.Keywords` (union across configured cultures), so a pt-BR user typing "save" matches an en keyword. New language = resource entries, no code change.
- **Telemetry:** keystroke-to-paint latency percentiles, zero-result rate, top selection per query, abandoned queries (feeds synonym/keyword expansion back into capability profiles).

---

## 12. Testing

- **Golden ranking tests** — `"cust"` ranks "Customer List" above "Custodian"; frecency changes order deterministically.
- **Detector fixture table** — valid/invalid/formatted CPF, CNPJ, EAN, RG shapes (reuse `Entity.Public` test vectors); assert confidence levels.
- **Cancellation/race tests** — rapid typing; stale-sequence responses are discarded even when the task completed.
- **Latency budget tests** — Tier 0 < 16ms local; Tier 1 ~100ms debounce; Tier 2 300ms hard deadline.
- **Permission tests** — denied catalog entries never surface; entity rows filtered by CQRS authz.
- **Merge stability** — one more character never reshuffles unrelated results (monotonic).

**Do not start with ML/NLP.** Dictionaries + checksums + fuzzy scoring cover this scope entirely. Revisit only for free-form natural language ("customers who bought X last month") — a different product feature.

---

## 13. Roadmap (updated — Capabilities gives V0/V1 a head start)

| Version | Scope | Goal |
|---|---|---|
| **V0** | `YFex.Search.Public` + orchestrator skeleton (debounce, cancellation, sequence IDs), static-catalog provider over `ISearchRegistry` + fuzzy scorer, flat merged list, one Customer provider with naive `Contains` | Feel the UX, validate the scorer — catalog + permissions already real |
| **V1** | `QuerySignature` + `Entity.Public` detectors, capability-signal routing, grouped UI, two-tier latency, normalized DB columns + trigram, caps/deadlines/sequence, LRU cache, prefilters | Production-grade core |
| **V2** | Persisted frecency (`IKeyValueStore`), prefix operators, typo-tolerant Tier 2, empty-query MRU | Feels smart |
| **V3** | Verb+subject grammar, per-entity action generation rules, selection-learning, telemetry dashboards | Feels psychic |

---

## 14. Decisions to lock before coding

1. **Score normalization** — providers normalize to `MatchScore` 0..1; boosts added on top. (Recommended, locked in original plan.)
2. **`SuggestionKey` format** — `(CapabilityKey)` for catalog hits; `(entityType, valueObjectId, actionCapabilityKey)` for entity hits. Used for dedup + frecency.
3. **Grouping policy & per-section caps** — Actions / Navigate / Customers / Products; cap 5–8 each.
4. **Latency budgets** — <16ms local / ~100ms debounce / 300ms hard deadline.
5. **Detector/provider set for V0–V1** — CPF/CNPJ/RG/Email/phone detectors + one Customer provider; where the domain registers them.
6. **Streaming shape** — `IAsyncEnumerable<SearchHit>` (via `PagedList<T>`), sequence-ID race handling local to the orchestrator. (Recommended over `IEventBus` for a single search box.)

---

## 15. Reused / critical pieces

- `YFex.Capabilities.ISearchRegistry` + `ICapabilityLocalizer` — the static catalog + cross-culture keywords (Tier 0).
- `YFex.Authorization.IPolicyEvaluator.IsAllowed` + `IPrincipalAccessor` — filter-before-rank.
- `YFex.Entity.Public` (`Cpf`/`Cnpj`/`CpfCnpj`/`Rg`/`Email`/`Telefone`) — checksum detectors (Stage 1).
- `YFex.Persistence.IKeyValueStore` (frecency), `ICache` (LRU query cache).
- `YFex.Primitives.Collections.PagedList<T> : IAsyncEnumerable<T>`; core `Fresh/Cached/Stale/Miss`, `[Union]`, `ArrayPoolBufferWriter`.
- `YFex.NavigatR.RouteRegistry` — resolve/navigate on select; capability `CapabilityKey` invoke path for actions.
- Value-object convention exemplar: `src/YFex/Length.cs` (for `MatchScore`, `SuggestionKey`).

---

## 16. V0 implementation status (`C:\Dev\YFex\YFex.Search`)

Scaffolded as a sibling repo (own `search.slnx`, `src/` + `tests/` split) mirroring `YFex.Authorization`/`YFex.Capabilities`. All projects target `net11.0`, `LangVersion=preview`. **Build + 37 tests green.**

**Shipped (V0):**
- **`YFex.Search.Public`** — **contracts + value objects only** (no logic). `ISearchProvider`/`ISearchOrchestrator`/`IQueryAnalyzer`/`IFormatDetector`, `QuerySignature`/`FormatFlag`/`FormatKind`/`Confidence`, `SearchHit`/`MatchRanges`/`SearchCategory`, `SearchContext`/`SearchProviderMetadata`/`CostTier`, `SearchAction` union, value objects `MatchScore`/`SuggestionKey`. Deps: YFex core + `YFex.Authorization.Public`.
- **`YFex.Search`** — shared implementation: `QueryAnalyzer` (runs injected detectors), `SearchOrchestrator` (tiered fan-out, dedup-by-key, **composite rank = match + intent boost + frecency**, fallback-only-when-empty), the reusable `TextNormalization` (fold/tokenize/digits) + `FuzzyScorer`, the ranking module (`RankingWeights`/`RankingContext`), frecency (`IFrecencyStore`/`IFrecencyView`, `FrecencyMath`, `InMemoryFrecencyStore`, `KeyValueFrecencyStore` over `IKeyValueStore`), the `CachingSearchOrchestrator` LRU query-cache decorator over `ICache`, and `AddYFexSearch`/`AddSearchProvider`/`AddFormatDetector`/`AddInMemoryFrecency`/`AddPersistentFrecency`/`AddSearchQueryCache`. Deps: `.Public` + `YFex.Persistence`.
- **`YFex.Search.Capabilities`** — `CapabilityCatalogProvider : ISearchProvider` (Tier-0) over `ISearchRegistry` + `IPolicyEvaluator` + `ICapabilityLocalizer`; filter-before-rank, excludes `Hidden`, maps hits to `SearchAction.Capability(key)`. Deps: `.Public` + **`.Search`** (for the shared scorer/normalizer) + `YFex.Capabilities` + `YFex.Authorization.Public`. `AddCapabilityCatalogProvider`.
- **`tests/YFex.Search.Tests`** — value objects, normalization, analyzer (+ stub detector), fuzzy scorer, orchestrator (tiering/dedup/rank/min-length/fallback), catalog provider (display/keyword match, permission filter, hidden exclusion, action mapping).

**Deviations from the plan above (deliberate, to keep layering honest):**
1. **`SearchAction` lives in `.Public` with core-typed payloads** (`RunCapability(string CapabilityId)` / `OpenEntity(Type,object)` / `NavigateTo(Type,object?)`) rather than referencing `CapabilityKey`/`IRoute` directly — `.Public` must depend only on core + `Authorization.Public` (§2). The `CapabilityId` string is `CapabilityKey.ToString()`; the Mvvm host re-hydrates it. Uses the **native `union`** keyword (net11 BCL), matched via `.Value` (as `AuthorizationResult` is).
2. **`TextNormalization` + `FuzzyScorer` are in `.Search`** (public), as the plan intends — `.Public` stays contracts-only. Providers that want the shared scan (e.g. `.Search.Capabilities`) take a `.Search` dependency; the orchestrator lib never depends on a provider, so the edge is benign. Query tokens arrive already folded on the `QuerySignature`, so DB-backed entity providers generally don't need these at all.
3. **`SearchHit` is a `sealed record`** carrying `Display` + `Category` (needed by the grouped UI and the merger) — richer than the §5 sketch, still normalized `MatchScore` + stable `SuggestionKey`.
4. **Name clash:** `YFex.Capabilities` already ships a `SearchHit` + `CapabilitySearchService`; consumers importing both namespaces alias `using SearchHit = YFex.Search.Public.SearchHit;`.
5. **Orchestrator streams per-tier, not per-provider** — it emits the Instant tier ranked first, then Fast/Remote merged, then Fallback only if nothing matched. True per-provider incremental streaming + sequence-ID stale-drop is V1/ViewModel work.

**Ranking (§6) — now complete.** The orchestrator ranks on the full composite `match + intent boost + frecency` (was match + tie-breakers). The **intent boost** is driven by `SearchHit.Satisfies` (the query shape a hit directly answers): when the `QuerySignature` recognized that shape, the hit is lifted by the detector's `Confidence` (Certain/Strong/Weak weights) — additive and positive only, so unaligned text hits are merely not lifted, never gated. **Frecency** is a saturating use-count × recency-decay term (`FrecencyMath`, ~2-week half-life), per-user, loaded once per query as an `IFrecencyView` and read synchronously per candidate. Weights are tunable via `RankingWeights` (passed to `AddYFexSearch`). The **LRU query cache** (`CachingSearchOrchestrator`) keys on principal + accent-folded query, streams inner results through on a miss while accumulating, and caches only on full completion (an abandoned/early-broken query is never cached) — pair with an in-process `ICache` (`InMemoryCache`) since `SearchHit` carries `Type`/union payloads that don't round-trip a serializing backend. *Gotcha:* a `record struct` with a primary ctor zero-inits on `new()`, so default weights are spelled out explicitly in `RankingWeights.Default`, not via `new()`.

**Not yet built (follow-ups):** `YFex.Search.Mvvm` (SearchBoxViewModel — pulls in the State generators; also the natural home for empty-query MRU via `IFrecencyView.Recent` and recording selections via `IFrecencyStore.RecordAsync`), domain `YFex.Search.Entity` (CPF/CNPJ/RG/Email detectors — which set `SearchHit.Satisfies` to light up the intent boost — + Customer/Product CQRS providers), prefix operators, telemetry, and wiring `search.slnx` into the root `yfex.slnx`.
