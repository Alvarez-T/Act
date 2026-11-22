# 01 — Inventory: designed versus built, overlaps

> Checked against source on 2026-11-18 (`YFex.Search` HEAD `e2c0310`, `YFex.Capabilities` HEAD `d753ba0`; both have only an
> uncommitted `.csproj` change that removes an explicit `Microsoft.Extensions.DependencyInjection.Abstractions` reference).
> Current source wins over the two old designs ([universal-search-design](../universal-search-design.md),
> [capabilities-design](../capabilities-design.md)).

## 1. What each library is for

| | YFex.Capabilities | YFex.Search |
|---|---|---|
| Question it answers | "What can be done here, by whom, with what arguments, and how risky is it?" | "What matches what the user typed, ranked?" |
| Unit | `CapabilityDescriptor` keyed by `CapabilityKey` (kind + owner type + member) | `SearchHit` keyed by `SuggestionKey`, produced by `ISearchProvider`s |
| Consumers | AI agent (`ICapabilityAgentApi`), search bar, command bars, Jobs admin | search box, command palette, (new) lookup editors |
| Gate | `IPolicyEvaluator.IsVisible` + side effect + approval → `ActionGate` | providers filter by permission before ranking |

The two meet in three places: (1) capabilities are one search provider (the static catalog); (2) "verb + subject"
needs capabilities (verbs) and record search (subjects) together; (3) the AI agent needs both to turn "approve Maria's
order" into a capability call with a resolved id. This design treats them as one plan with two packages.

## 2. YFex.Capabilities

Projects: `YFex.Capabilities` (refs NavigatR **engine**, Authorization.Public, Tenancy, `Microsoft.Extensions.Localization.Abstractions`),
`YFex.Capabilities.Generator` (netstandard2.0), `YFex.Capabilities.Cqrs` (refs Cqrs, Authorization engine, Validation),
`samples/YFex.Capabilities.AotSmoke`. Outside this repo: `YFex.Jobs.Capabilities` (source + invoker, Jobs Plan D §7A).

| Designed (capabilities-design) | Built | Notes |
|---|---|---|
| Value objects `CapabilityKind`, `SideEffect`, `CapabilityFlags`, `MemberName`, `CapabilityKey`, `ResourceKey` | ✅ | `CapabilityFlags.Reversible` added (advisory). `CapabilityKey.ToString()` uses `Type.Name` (`ValueObjects.cs:99`): not unique across namespaces and not parseable back |
| `ICapabilitySource` + `CapabilityDescriptor` | ✅ | descriptor grew `Scope`, `ParametersSchema`, `PolicyKey`, `Approval` |
| ViewModel source: profile builder + generator skeleton | ✅ | generator copies the State command-naming rule (`Discovery.cs:86-88` vs `YFex.State/.../CommandParser.cs:121-124`); the designed shared `YFex.State.Generator.Abstractions` was not built (atlas F20) |
| `CompiledCapabilityRegistry.Build` + fail-fast rules | ✅ | resolves `CompiledPolicy` per key from `IPolicyRegistry` (authorization-consolidation 04 §5) |
| Three registries `ISearchRegistry`, `IAiDescribeRegistry`, `IAuthorizationView` | ✅ / changed | `IAuthorizationView` became `ICapabilityPolicies` (key → `CompiledPolicy`); permission is now a display hint |
| `ICapabilityAgentApi` + `ActionGate` | ✅ | gate has `NeedsApproval` (Cqrs `.RequireApproval`) and `Denied(AuthorizationVerdict)`; custom non-boxing `[Union]` struct |
| Localization by resource keys, `ICapabilityLocalizer`, neutral seed export | ✅ | own localizer over `IStringLocalizer` (atlas F12: core has `IMessageCatalog`) |
| Search bar consumer (§10) | ✅ **duplicate** | `ICapabilitySearch` / `CapabilitySearchService` with its own `SearchHit`, fold and scoring (`Search/CapabilitySearchService.cs:9,27,156`) |
| Scoped capabilities (not in the design; integration-plan I1) | ✅ | `CapabilityScope(byte Rank, string Value, bool Additive)` + `SearchFor/DescribeFor/PoliciesFor(ScopeIdentity)` |
| CQRS source (P6) | ✅ | reads `CompiledMessagingRegistry.Capabilities`; Validation shape → `ParametersSchema` |
| Jobs source | ✅ in YFex.Jobs | plus `JobCapabilityInvoker` (JSON args → `IJobInstances`) — the only invoke path that exists |
| Messaging source, Web (inbound endpoint) source | ❌ | designed only |
| Route source ("folded into the ViewModel source") | partial | routes attached as metadata (`ResolveRoutes`, 0..1); no route capabilities of their own |
| Invoking a capability | ❌ | design assumed "the UI obtains the live VM"; no contract. Jobs built its own invoker |
| Open decisions §14 (default side effect, `.Requires`, profile discovery attribute, view mirror/delegate, sources in v1, side-effect per source) | mostly settled by the code | default side effect = `Mutating` when a permission exists, else `ReadOnly` (`ViewModelCapabilitySource.cs:137`); `.Requires` kept |

## 3. YFex.Search

Projects: `YFex.Search.Public` (refs core + Authorization.Public), `YFex.Search` (refs Persistence), `YFex.Search.Capabilities`
(refs `.Public`, `.Search`, Capabilities engine, Authorization.Public), tests (37). No `README.md`; not in the root `yfex.slnx`.

| Designed (universal-search-design) | Built | Notes |
|---|---|---|
| `QuerySignature`, `IQueryAnalyzer`, `IFormatDetector`, confidence | ✅ | **`FormatKind` names Brazilian documents (`Cpf`, `Cnpj`, `Rg`, `Telefone`, `Ean`)** in an infrastructure contract (`QuerySignature.cs:11-20`) — breaks the domain-neutrality rule |
| `ISearchProvider`, metadata, cost tiers | ✅ | |
| `SearchHit`, `MatchScore`, `SuggestionKey`, `SearchCategory` | ✅ | `SearchHit` is a record with `Display`, `Category`, `Satisfies` |
| `SearchAction` union (run capability / open entity / navigate) | ✅ weak | payloads are `string` / `Type` + `object` (`SearchAction.cs:26,29`): the capability id is the lossy `CapabilityKey.ToString()`; the entity id is boxed |
| Orchestrator: tiers, dedup, composite ranking (match + intent + frecency) | ✅ partial | streams **per tier**, and each tier waits for its slowest provider (`Task.WhenAll`, `SearchOrchestrator.cs:88`); no deadlines, no per-provider streaming |
| Fuzzy scorer, prefilters, text folding | ✅ partial | token exact/prefix/substring scorer; no bitmask prefilters; `TextNormalization.Fold` allocates per call |
| Frecency (in-memory, `IKeyValueStore`) | ✅ | `FrecencyMath`, two-week half-life, `TimeProvider` |
| LRU query cache over `ICache` | ✅ | in-process only (hits carry `Type`) |
| Static catalog provider over `ISearchRegistry` | ✅ **gap** | reads the base registry only (`CapabilityCatalogProvider.cs:23,52`): **scoped capabilities never appear**, unlike `CapabilitySearchService` which calls `SearchFor(scope)` |
| `YFex.Search.Mvvm` (search box view model, prefix operators, MRU) | ❌ | |
| Entity providers, detectors (`YFex.Search.Entity` in the domain) | ❌ | |
| Typo-tolerant tier, verb + subject, grouped sections, telemetry | ❌ | |

## 4. Overlaps and duplication (each is a decision in the README)

| # | Same thing twice | Evidence | ⚑ |
|---|---|---|---|
| D1 | Two text searches over the same capability registry, two `SearchHit` types (consumers alias one) | `YFex.Capabilities/.../CapabilitySearchService.cs` vs `YFex.Search.Capabilities/CapabilityCatalogProvider.cs` | SK2 |
| D2 | Two accent-fold + tokenize implementations; UI filter controls (searchable `YComboBox`, NavBar search, `YIconPicker`) are a likely third | `CapabilitySearchService.cs:156` vs `TextNormalization.cs:15` | SK4 |
| D3 | Two "is this hit visible?" checks (policy, else permission hint) copied line for line | `CapabilitySearchService.cs` `Query` vs `CapabilityCatalogProvider.cs:56-59` | SK2 |
| D4 | Capability invocation: Jobs has `JobCapabilityInvoker`; search (`RunCapability`), AI and command bars have none | `YFex.Jobs.Capabilities/JobCapabilityInvoker.cs:42` | SK6 |
| D5 | Command-name rule copied in two generators | `Discovery.cs:86-88`, `CommandParser.cs:121-124` | SK21 |
| D6 | Capability localizer vs core message catalogs (atlas F12) | `Localization.cs:10`, core `Localization/IMessageSource.cs:29` | SK19 |
| D7 | Scope tag `CapabilityScope(byte, string)` next to Tenancy's `TenancyPath` / `ScopeIdentity` (atlas F18) | `CapabilityScope.cs:22` | SK20 |
| D8 | "Entity provider" (search) and "lookup" (Forms ⚑FM20) are the same "find records of type X by text" | design §5 family 2; forms README ⚑FM20 | SK9, SK10 |
| D9 | Per-user frecency (search) vs user behaviour signals (YFex.Profiling) | `Frecency.cs` | SK25 |

## 5. Gaps the Forms design adds

- **Record lookup** (⚑FM20): an id-typed field (`SupplierId`, `ProductId`) is edited by search-as-you-type and shows the
  name. Nothing in core marks a type as "the id of a record" (Farma's `CustomerId` is a Vogen `[ValueObject<int>]`), and
  Search has no "find one record of type X" or "name for this id" contract. → [02](02-record-lookup.md).
- **Form and CRUD commands as capabilities** (⚑FM23, ⚑FM44): `New / Save / Delete / Duplicate / Export / Refresh` on every
  CRUD screen, findable by "novo cliente", runnable by AI, shown in the command bar, gated by Authorization. Needs an invoke
  path and context-bound view-model commands. → [03](03-capabilities-everywhere.md).
- **AI / paste fill** (⚑FM23): the AI fills fields from the shape's JSON schema; id fields need the same lookup to turn
  "Distribuidora Acme" into a `SupplierId`. → [02 §6](02-record-lookup.md).
