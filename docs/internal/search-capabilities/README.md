# YFex.Search + YFex.Capabilities — one plan

> Status: **proposal** (2026-11-18), no code. Decisions **⚑SK1–⚑SK54**, all pending (prefix `SK` checked unused in `docs/`; ⚑SK45–⚑SK54 added 2026-11-18 by 05).
> Repos stay `YFex.Search` and `YFex.Capabilities` (⚑SK1). This folder owns the contracts that YFex Forms consumes
> ([forms README](../forms/README.md) ⚑FM20, ⚑FM23, ⚑FM44): Forms stays the consumer side.

| File | Holds |
|---|---|
| [01-inventory](01-inventory.md) | designed versus built in both repos, overlaps and duplication with file:line evidence, gaps from Forms |
| [02-record-lookup](02-record-lookup.md) | "pick one record of type X": core record-id marker, `IRecordLookup<TId>`, CQRS bridge, Forms editor, AI and universal search use |
| [03-capabilities-everywhere](03-capabilities-everywhere.md) | stable capability id, one invoke path, context-bound view-model commands, CRUD/form commands, record actions, AI |
| [04-search-engine](04-search-engine.md) | changes to YFex.Search: one catalog search, folding owner, app-declared formats, typed actions, streaming + deadlines, view models, privacy |
| [05-decision-models](05-decision-models.md) | research on decision models (Jev, Microsoft-Decision-1.1, open local alternatives), where model signals fit Search (boost, rerank, catalog embeddings, latency, privacy, fallback), and the shared [YFex.Inference proposal](../inference/README.md) |

## 1. In one paragraph

**Capabilities** says what can be done (described, permission-tagged, side-effect-classed, AI-describable); **Search** finds
things by text and ranks them. They meet in the search bar (capabilities are its static catalog), in "verb + subject" and in
the AI agent. Both repos are built further than their designs say, but they search the same registry twice, fold text twice,
and nothing runs a capability except Jobs' own invoker. Forms adds a third need: an id-typed field (`SupplierId`) edited by
search-as-you-type that shows the name. This plan (1) removes the duplicates, (2) adds one **record lookup** contract that the
Forms editor, CRUD grid columns, universal search, verb + subject and the AI agent all use, (3) adds **one invoke path** and
context for view-model commands, so search, command bars, AI and CRUD screens run capabilities the same way, and (4) makes
the standard CRUD commands capabilities without per-screen profiles.

```csharp
services.AddYFexCapabilities().AddCqrsCapabilitySource().AddCrudCapabilities();       // describe + gate
services.AddYFexSearch().AddCapabilityCatalogProvider();                             // find
services.AddRecordLookup<SupplierId>(l => l.Search(q => new SearchSuppliers(…)).Resolve(ids => new SupplierNames(ids)));
// Forms: SupplierId : IRecordId<SupplierId>  →  YLookup<SupplierId> editor, no view code
```

## 2. Package map (proposed)

```
 YFex core                    + IRecordId<TSelf>, RecordKind, RecordKey                         (request, ⚑SK8)
 YFex.Capabilities.Public     new: CapabilityKey/Id, kinds, SideEffect, flags, ActionGate,       (⚑SK3)
                              ICapabilityInvoker, CapabilityResult, IActiveSurfaces     → core, Authorization.Public
 YFex.Capabilities            registries, sources, agent API, invoker dispatcher        → .Public, NavigatR.Public (⚑SK22)
 YFex.Capabilities.Generator  view-model skeletons (unchanged)
 YFex.Capabilities.Cqrs       CQRS source + handler                                     → Cqrs, Authorization, Validation
 YFex.Capabilities.Mvvm       new: CRUD capability source, active surfaces from page lifecycle   (⚑SK14, ⚑SK15)
 YFex.Search.Public           + IRecordLookup<TId>, RecordChoice, LookupQuery, QueryFormat; typed SearchAction
                                                                                        → core, Authorization.Public, Capabilities.Public
 YFex.Search                  engine + RecordLookupSearchProvider + typo tier            → .Public, Persistence, Resilience
 YFex.Search.Capabilities     catalog provider (scope-aware), record-action expansion
 YFex.Search.Cqrs             new: lookups from two CQRS queries                        (⚑SK12)
 YFex.Search.Mvvm             new: SearchBoxViewModel, LookupViewModel<TId>             (⚑SK16)
 YFex.Jobs.Capabilities       source + handler (adapts JobCapabilityInvoker)            (⚑SK6)
 YFex.UI / YFex.Avalonia      YSearchBox, YCommandPalette, YLookup<TId>, YCommandBar binding (UI's own plan)
```

## 3. Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md); outcomes per [ecosystem-fit §1](../../agent/rules/ecosystem-fit.md).

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core (`src/YFex`) | used; **lacks a feature** | `PagedList<T>`, `[Union]`, `Contacts` (email/phone detectors), `Paths`; needs a record-id marker, `RecordKind`, `RecordKey` | SK8 |
| core `Localization` | **better placement** | `ICapabilityLocalizer` over `IStringLocalizer` duplicates `IMessageCatalog` (atlas F12); lookup and CRUD verb labels are catalog keys | SK19 |
| core `Diagnostics` | used | probes and meters for search latency, deadline misses, zero results; no raw text | SK32 |
| core `Consent` | not a fit | frecency is functional, not telemetry (Profiling row) | SK25 |
| YFex.Persistence | used | `ICache` query cache, `IKeyValueStore` frecency (built) | — |
| YFex.Data | fits, **lacks a feature** | lookup queries run on Data; folded-name / digits-only columns and trigram index helpers for migrations | SK30 |
| YFex.Data.Placement (design) | not a fit | which database holds a tenant is below the CQRS query | — |
| YFex.Backup | not a fit | no search state worth backing up (frecency is rebuildable) | — |
| YFex.Tenancy | used; **better placement** | per-scope capability manifests built on `ScopeIdentity`; `CapabilityScope(byte, string)` should be Tenancy's scope form; hard reference vs optional (atlas F18) | SK20 |
| YFex.Tenancy.Registry (design) | not a fit | tenant placement only | — |
| YFex.CustomLogic | fits | a tenant-specific lookup implementation or ranking weights selected by scope | SK35 |
| YFex.Validation | used; **lacks a feature** | `JsonSchemas` shapes → `ParametersSchema` (built); invoke arguments validated by the pipeline; schema should mark record references | SK29 |
| YFex.Rules | used (through Cqrs) | approvals → `ActionGate.NeedsApproval` (built); no direct reference | — |
| YFex.Expressions | not a fit now | a filter/query language for advanced search is a non-goal (natural-language queries out of scope) | — |
| YFex.Composer (design) | fits (consumer) | end-user screens pick actions from the capability manifest and use lookups as field editors | SK34 |
| YFex.Xml | not a fit | no XML concern | — |
| YFex.Cqrs | used; **lacks a feature** | CQRS capability source (built), CQRS-backed lookups, invoke handler; needs AOT-safe "message from JSON" | SK6, SK12, SK40 |
| YFex.Messaging | fits later | Messaging capability source (designed, not built); remote lookups over RPC for handhelds; offline-queued commands surface as `Accepted` | SK37 |
| YFex.State | used; **lacks a feature** | view models of search box and lookup; `[StateCommand]` has no parameters (needed for view-model invoke with arguments); command-name rule copied in two generators (atlas F20) | SK21, SK41 |
| YFex.NavigatR | used; **better placement** | routes on capabilities, navigation on select, page lifecycle for active surfaces; Capabilities references the engine instead of `YFex.NavigatR.Public` | SK14, SK22 |
| YFex.Mvvm | used | CRUD bases (Forms ⚑FM40) declare their standard commands; `SelectorViewModel` stub may become the lookup base | SK15 |
| YFex.UI | used | `YSearchBox`, `YCommandPalette`, `YLookup<TId>` on searchable `YComboBox` (C7), `YCommandBar` from capabilities; text-matcher seam in `YFex.UI.Public` | SK4, SK16 |
| YFex Forms (design) | consumer | `Lookup<T>` editor over `IRecordLookup<T>` (⚑FM20), commands as capabilities (⚑FM23, ⚑FM44), AI fill with `find_record` | SK9, SK15 |
| YFex.Authentication | not a fit | identity only; search uses the authorization context | — |
| YFex.Authorization | used | `IPolicyEvaluator.IsVisible` filter-before-rank, `CompiledPolicy` per capability, approval tokens (built) | — |
| YFex.Licensing | fits through Authorization | unlicensed capabilities hidden by the availability hook in `IsVisible` (verify in SK-P0) | SK36 |
| YFex.Installations | not a fit | device-specific action sets come from scopes or hosting profiles, not from search | — |
| YFex.Offline | fits, **lacks a feature?** | capability availability offline; lookups from cached names while offline | SK28 |
| YFex.Privacy | used; **lacks a feature** | `[Sensitive]` masks on lookup display; erasure must reach frecency entries keyed by a record | SK24 |
| YFex.Audit | used (indirectly) | invocations are audited by the pipeline that executes them, not by Capabilities | SK26 |
| YFex.Actors | used | invocation origin (agent on behalf of user, search box) through origin writers | SK26 |
| YFex.Logging | used via probes | ships search meters; never queries | SK32 |
| YFex.Time | used | `TimeProvider` for frecency decay (built) | — |
| YFex.Profiling | **better placement?** | per-user frecency vs behaviour signals | SK25 |
| YFex.System | not a fit | machine telemetry | — |
| YFex.Security | not a fit | the search bar is guarded by filter-before-rank, no inspection concern | — |
| YFex.Cryptography | not a fit | no keys or signatures | — |
| YFex.Networking | not a fit | network mechanics; its "platform support" type is deliberately not named Capabilities (⚑N21) | — |
| YFex.Web | fits later | Web capability source for inbound endpoints (designed); external registries (e.g. company lookup) as a domain `IRecordLookup` over YFex.Web | SK38 |
| YFex.Resilience | used | per-provider deadlines through its per-attempt timeout; no retries | SK31 |
| YFex.Orchestration | not a fit | multi-step AI plans belong to Automation; a capability runs one step | — |
| YFex.EventSourcing | not a fit | projections may feed an app's lookup tables; no library concern | — |
| YFex.Jobs | used; **better placement** | Jobs source (built in Jobs); `JobCapabilityInvoker` becomes the Jobs handler of the shared invoker | SK6, SK23 |
| YFex.Cluster (design) | not a fit | registries are per process, built at start | — |
| YFex.Provisioning (design) | fits later | control-plane actions as capabilities for an admin agent | SK39 |
| YFex.Hosting (design) | fits later | host actions as capabilities (hosting 05 "not planned") | SK39 |
| YFex.Installer (design) | not a fit | installs products | — |
| YFex.Notifications | not a fit | no searchable messages in scope | — |
| YFex.Files (design) | not a fit | file search is not in scope | — |
| YFex.Devices | fits | barcode/scale input routed to the focused lookup or the search box through the analyzer | SK33 |
| YFex.Automation | fits, **lacks a feature** | its AI agent steps should take app actions from the capability manifest + invoker + `find_record` | SK27 |
| YFex.Inference (proposal, [inference](../inference/README.md)) | fits; **new library** | model-backed judgments (intent boost, rerank), catalog embeddings; rules-only fallback; Search references only `.Public` through optional `YFex.Search.Inference` | SK45, SK54 |
| YFex.Domain / YFex.Farma (domain) | consumers | id types get `IRecordId<T>`; lookups and format detectors (`br.cpf`…) live there | SK8, SK18 |

Outside libraries: `Microsoft.Extensions.Localization.Abstractions` (used today, ⚑SK19); SymSpell and Lucene.NET studied for
typo tolerance and rejected in ⚑SK43; PostgreSQL `pg_trgm` / SQL Server full-text are database features reached through
YFex.Data (⚑SK30); Vogen generates Farma's id types and must satisfy `IRecordId<T>` (spike in SK-P1).

## 4. Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex core | `IRecordId<TSelf>` (static `RecordKind Kind`), `RecordKind`, `RecordKey` | no (additive) | SK8 | SK-P1 (Forms FM-P2 editor choice) |
| YFex.Inference (new, proposal) | `IJudge` / `ISpanFinder` / `IEmbedder` in `.Public`, rules-only provider, abstain outcome, per-provider deadline | no (new library) | SK45 | SK-P8 |
| YFex.Cqrs | create a message from JSON arguments AOT-safe (generated `JsonTypeInfo` per registered message) | no | SK40 | SK-P3 |
| YFex.State | `[StateCommand]` with a parameter; one shared command-name rule for State and Capabilities generators | no | SK41, SK21 | SK-P3, SK-P7 |
| YFex.NavigatR | the route lookup Capabilities needs in `YFex.NavigatR.Public`; page activation events (including docked pages and dialogs) for active surfaces | no | SK22, SK14 | SK-P1, SK-P4 |
| YFex.Mvvm | CRUD base records which message each standard command dispatches (part of Forms ⚑FM40) | no (stubs unused) | SK15 | SK-P5 |
| YFex.Validation | `x-yfex-record: <kind>` in JSON schemas for `IRecordId<T>` members | no | SK29 | SK-P6 |
| YFex.Data | migration helpers for folded and digits-only search columns, trigram/full-text index | no | SK30 | SK-P2 (optional) |
| YFex.Privacy | erasure of a record also calls registered "forget" hooks (frecency) | no | SK24 | SK-P2 |
| YFex.Privacy | `PseudonymizationSession` + `AiDataPolicy` (⚑P19, designed) before a remote "ask" mode | no | SK51 | SK-P8 (ask mode only) |
| YFex.Jobs | `JobCapabilityInvoker` exposed as an `ICapabilityHandler` | no (additive adapter) | SK6 | SK-P3 |
| YFex.Automation | AI agent tool catalog from capabilities | no | SK27 | SK-P6 |
| YFex.UI | `YSearchBox`, `YCommandPalette`, `YLookup<TId>`, command bar from capabilities, text-matcher seam in `YFex.UI.Public` | no | SK4, SK16 | SK-P4 |
| YFex.Tenancy | capability scope tag in Tenancy's scope form; Capabilities references Tenancy optionally (atlas F18) | yes for `CapabilityScope` users (none outside tests) | SK20 | SK-P7 |
| core `Localization` | capability localizer as an adapter over `IMessageCatalog` (atlas F12) | yes for `AddStringLocalizerCapabilityLocalizer` callers | SK19 | SK-P7 |
| YFex Forms (design) | ⚑FM20 points to `IRecordLookup<T>`; `IEditorFactory.Lookup<T>` constrained to `IRecordId<T>` | design only | SK9 | FM-P5 |

## 5. Decisions (all pending)

Recommended option first. Integration and placement decisions first (⚑SK1–⚑SK41), then engine internals.

| ⚑ | Topic | Proposal | Rejected alternatives | See |
|---|---|---|---|---|
| SK1 | Repos and documents | keep two repos (describe vs find), one design here; mark `universal-search-design.md` and `capabilities-design.md` **superseded** with a banner pointing here and keep them as history (rationale: generator over reflection, three registries, ranking math) | merge into one repo `YFex.Discovery`; fold Search into Capabilities; keep the old docs as live designs | 01 §1 |
| SK2 | Duplicate catalog search | remove `ICapabilitySearch`/`CapabilitySearchService`/`Capabilities.SearchHit`; Search's catalog provider is the only one and becomes scope-aware (fixes scoped capabilities never appearing) | keep both; move scoring into Capabilities | 04 §1 |
| SK3 | Capabilities contracts package | extract `YFex.Capabilities.Public` (key, id, kinds, side effect, gate, invoker contracts) so Search.Public, Forms, Automation and Jobs name keys without NavigatR/Tenancy/localization | one package; put `CapabilityKey` in core | §2 |
| SK4 | Text folding owner | `YFex.Search` owns fold/tokenize/digits (span-based); UI filter controls use it through a matcher seam in `YFex.UI.Public` | core (behaviour does not go to core); a copy in UI | 04 §2 |
| SK5 | Stable capability id | `CapabilityId` (kind prefix + full name, parseable through the registry) + optional authored id; used by frecency, AI tool names, audit, telemetry | `Type.Name` text (today, collides); `Type.FullName` only; GUIDs | 03 §2 |
| SK6 | One invoke path | `ICapabilityInvoker` + per-source `ICapabilityHandler` (CQRS, Jobs adapter, route, live view model); `CanInvoke` stays the pre-check | each consumer invokes per kind (today: Jobs only); invoker inside Automation | 03 §3 |
| SK7 | Typed search actions | `RunCapability(CapabilityKey, arguments)`, `OpenRecord(RecordKey)`, `NavigateTo` | string id + boxed `object` id (today) | 04 §4 |
| SK8 | Record-id marker | core `IRecordId<TSelf>` with `static RecordKind Kind` + `RecordKey` | `[RecordId]` attribute; `*Id` naming convention; runtime registration only | 02 §2 |
| SK9 | Lookup contract | `IRecordLookup<TId>` in `YFex.Search.Public`: search, resolve, resolve-many, metadata; Forms ⚑FM20 consumes it | in YFex.UI.Forms (UI only); in Cqrs (query convention only); in Capabilities | 02 §3 |
| SK10 | Entity providers | a lookup is also a universal-search provider (adapter, opt-out); the separate "entity provider" family is dropped | two contracts for "find records of type X" | 02 §7 |
| SK11 | Lookup filters | typed filter object per lookup (dependent lookups: City by State), re-queried on change | untyped dictionary; no filters | 02 §5 |
| SK12 | Lookup implementation | `YFex.Search.Cqrs` builds lookups from two CQRS queries (search, names by ids): authorization, cache and offline inherited | lookups reading Data directly (skips the authorizer); hand-written only | 02 §4 |
| SK13 | Record actions / verb + subject | Capabilities indexes capabilities by subject record type (`BySubject`); search expands record hits into actions | hand-authored action lists per entity provider | 03 §6 |
| SK14 | View-model commands | context-bound: offered only for live instances from `IActiveSurfaces` (NavigatR page lifecycle) | list them globally; drop them from search | 03 §4 |
| SK15 | CRUD and form commands | a CRUD capability source in `YFex.Capabilities.Mvvm` contributes New/Save/Delete/Duplicate/Export/Refresh for every CRUD view model, labels from catalog keys, policies from the dispatched messages; command bars bind to `ByOwner` | a profile per screen; command bars without capabilities | 03 §5 |
| SK16 | View-model placement | `YFex.Search.Mvvm` (`SearchBoxViewModel`, `LookupViewModel<TId>`); controls bind to interfaces in `YFex.UI.Public` | view models inside YFex.UI; inside YFex.Mvvm | 04 §6 |
| SK17 | Create from lookup | lookup metadata names a create capability; "Create supplier 'Acme'" opens the Forms quick profile in a dialog and returns the id | no create from lookups | 02 §3 |
| SK18 | Query formats | app-declared `QueryFormat` value object replaces the `FormatKind` enum of Brazilian documents (domain neutrality) | keep the enum + `Custom`; plain strings | 04 §3 |
| SK19 | Localization | capability localizer becomes an adapter over core `IMessageCatalog` (atlas F12) | keep `IStringLocalizer` as the base | 01 §4 D6 |
| SK20 | Capability scopes | scope tag in Tenancy's form (`TenancyPath`/`ScopeIdentity`), Tenancy reference made optional (atlas F18) | keep `CapabilityScope(byte, string)` | 01 §4 D7 |
| SK21 | Generator naming rule | one shared command-name rule (State generator abstractions, atlas F20); parity test until then | keep two copies without a test | 01 §4 D5 |
| SK22 | NavigatR reference | Capabilities references `YFex.NavigatR.Public`, not the engine | keep the engine reference | §2 |
| SK23 | Bridge placement | apply the atlas F25 rule once decided; recommended: source bridges with the library that knows the subsystem's internals (Jobs.Capabilities stays in Jobs, Capabilities.Cqrs in Capabilities), Search.* bridges in Search | move every bridge into Capabilities | atlas F25 |
| SK24 | Privacy | queries never logged; results masked by `[Sensitive]`; Privacy erasure reaches frecency entries; query cache in-process with short TTL | log queries for tuning; no erasure of frecency | 04 §8 |
| SK25 | Frecency vs Profiling | frecency stays in Search (functional, local); Profiling may consume selection events with consent | frecency owned by Profiling | 04 §8 |
| SK26 | Audit and actors | invocations carry an origin; the executing pipeline stamps and audits; Capabilities writes no audit | Capabilities writes its own audit records | 03 §8 |
| SK27 | AI agent tools | YFex.Automation takes app actions from the manifest + invoker + `find_record` | Automation keeps its own action registry | 03 §7 |
| SK28 | Offline | capabilities carry offline availability; lookups resolve from cached names offline | no offline signal (actions fail when run) | 04 §8 |
| SK29 | Schema record marker | Validation schemas mark `IRecordId<T>` members (`x-yfex-record`) | Capabilities patches schemas after the fact | 02 §6 |
| SK30 | Search columns | Data migration helpers for folded/digits columns and trigram index (optional for apps) | every app writes them by hand | 02 §4 |
| SK31 | Provider deadlines | Resilience per-attempt timeout per provider, no retries | own timers in the orchestrator | 04 §5 |
| SK32 | Telemetry | Diagnostics probes + meters, no raw text | none; log queries | 04 §8 |
| SK33 | Device input | scanner/scale input goes to the focused lookup or search box through the analyzer | separate device-to-field wiring per screen | §3 |
| SK34 | Composer | Composer consumes the capability manifest (action palette) and lookups (field editor); no change now | Composer keeps its own action list | §3 |
| SK35 | Tenant overrides | CustomLogic selects a tenant lookup implementation; ranking weights as Tenancy scoped settings | one implementation for all tenants | §3 |
| SK36 | Licensing | through Authorization's availability hook in `IsVisible`; verify coverage in SK-P0 | a license check in Capabilities | §3 |
| SK37 | Messaging | build the Messaging capability source later; remote lookups over RPC for handhelds | drop the Messaging source | §3 |
| SK38 | Web | inbound-endpoint capability source later; external lookups are domain `IRecordLookup`s over YFex.Web | drop the Web source | §3 |
| SK39 | Hosting / Provisioning | their admin actions become capabilities later (no work now) | separate admin action lists | §3 |
| SK40 | Message from JSON | request to Cqrs for AOT-safe JSON → message | reflection-based deserialization in the handler | 03 §3 |
| SK41 | View-model command arguments | request to State: `[StateCommand]` with a parameter | AI cannot pass arguments to view-model commands | 03 §3 |
| SK42 | Orchestrator streaming | per-provider streaming as ranked replacement lists, deadlines, stale-sequence drop inside the orchestrator | per-tier `WhenAll` (today) | 04 §5 |
| SK43 | Typo tolerance | own bounded Damerau-Levenshtein for the catalog; databases do typo tolerance for records | SymSpell package; Lucene.NET | 04 §7 |
| SK44 | `CapabilityKind` | stays a closed enum (the library ships every source bridge; a new bridge adds a member and breaks switches on purpose) | open, app-declared kinds | 01 §2 |
| SK45 | Model signals: placement | new library **YFex.Inference** ([proposal](../inference/README.md), ⚑IF); Search references only `YFex.Inference.Public` through optional bridge `YFex.Search.Inference` | models inside Search, Privacy or Automation; `Microsoft.Extensions.AI` in Search | 05 §4 |
| SK46 | Model query intent | boost only: `IntentSignal` (app-declared intent, probability, source), capped below `IntentCertain` | intent as a gate; model replaces detectors; closed intent enum | 05 §3.2 |
| SK47 | Model latency placement | never Tier 0; Tier 1 only in-process with p95 ≤ 20 ms measured; else Tier 2 with deadline; remote APIs only in an explicit "ask" mode | remote API per keystroke | 05 §3.1 |
| SK48 | Semantic rerank | optional Tier 2 over top 20, desktop/server, off by default, replacement list, one bounded term | always on; rerank all; reranker owns the order | 05 §3.4 |
| SK49 | Embeddings | capability catalog only (vectors built with the registry, stored via Persistence); records stay in the database | record embeddings now; external vector DB | 05 §3.5 |
| SK50 | Verb + subject parse | deterministic (capability verbs, app record-kind words, stop words, date words with `TimeProvider` + tenant zone); a model only adds boosted candidates | model-only parse | 05 §3.3, §5.3 |
| SK51 | Query text privacy | keystroke text only to in-process/on-machine providers; remote only in "ask" mode through Privacy ⚑P19; no persisted cache; no raw text in probes | queries to cloud decision APIs | 05 §3.7 |
| SK52 | Offline and handhelds | handhelds rules only; offline desktops local models; site-broker model later (Offline/Hosting) | model files on handhelds | 05 §3.6 |
| SK53 | Determinism and tests | session cache by model id + version + normalized query; tests with no model and a fixed fake | uncached calls; tests on a real model | 05 §3.7 |
| SK54 | Jev / Microsoft-Decision-1.1 | optional remote providers behind one HTTP adapter for the shared `state + questions` shape (⚑IF26); background and "ask" only; per tenant after PT-BR calibration (⚑IF29) | adopt one as Search's engine; vendor SDKs | 05 §1–§2 |

## 6. Phases (proposed)

Each step builds; tests and AOT smoke at the end of each phase, per the working method.

| Phase | Content | Done when |
|---|---|---|
| SK-P0 Cleanup | ⚑SK2 (one catalog search, scope-aware), ⚑SK18 (`QueryFormat`), ⚑SK4 (fold owner), Search `README.md`, `search.slnx` in `yfex.slnx`; verify Licensing availability in `IsVisible` (⚑SK36) | one search over capabilities; scoped capabilities found; no document names in infrastructure |
| SK-P1 Contracts | `YFex.Capabilities.Public` (⚑SK3), `CapabilityId` (⚑SK5), typed `SearchAction` (⚑SK7), NavigatR.Public reference (⚑SK22); core `IRecordId<T>` request (⚑SK8) + Vogen spike | Search.Public names `CapabilityKey`; a Vogen id implements `IRecordId<T>` |
| SK-P2 Record lookup | `IRecordLookup<TId>` (⚑SK9), contract test kit, `YFex.Search.Cqrs` (⚑SK12), lookup-as-provider (⚑SK10), filters (⚑SK11), frecency forget hook (⚑SK24) | a Farma supplier lookup searches, resolves 50 ids in one query, honours permissions — **unblocks Forms FM-P5 (⚑FM20)** |
| SK-P3 Invoke | `ICapabilityInvoker` + CQRS, Jobs, route handlers (⚑SK6, ⚑SK40), origin (⚑SK26) | AI, search and command bar run a CQRS command through one call |
| SK-P4 Surfaces | `YFex.Search.Mvvm` (⚑SK16), active surfaces (⚑SK14), orchestrator streaming + deadlines (⚑SK42, ⚑SK31); controls in YFex.UI's plan | search box and `YLookup` in the gallery with a slow fake provider |
| SK-P5 Forms and CRUD | CRUD capability source (⚑SK15), create from lookup (⚑SK17), `BySubject` + record actions (⚑SK13) | "novo cliente" and "histórico maria" work; CRUD screens need no profile — **unblocks Forms part 2 (⚑FM44)** |
| SK-P6 AI | Automation tool catalog (⚑SK27), `find_record`, schema record marker (⚑SK29) | an agent fills a form with a supplier by name and asks when the name is ambiguous |
| SK-P7 Later | typo tier (⚑SK43), telemetry (⚑SK32), offline facts (⚑SK28), localization adapter (⚑SK19), scope form (⚑SK20), shared generator rule (⚑SK21), Messaging/Web sources (⚑SK37, ⚑SK38) | — |
| SK-P8 Model signals (optional, after YFex.Inference IF-P1) | `YFex.Search.Inference`: intent signals (⚑SK46), lexicon signals (⚑SK50), catalog embeddings (⚑SK49), optional rerank (⚑SK48), session cache (⚑SK53) | ranking identical with no model; a fake provider lifts "Notas fiscais — Maria" for "notas da maria de ontem"; p95 per tier within budget on the reference desktop |

## 7. Per-OS differences

None in these libraries: no OS code, no file formats of their own (frecency goes through Persistence). Text folding uses
`string.Normalize`, available on every runtime; the span-based fast path must keep ICU/NLS results identical (test on Linux
in SK-P0 with `linuxKit`).
