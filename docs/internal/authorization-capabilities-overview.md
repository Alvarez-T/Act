# Authorization + Capabilities — Design Overview

> **2026-10-02:** most of this design is implemented. Current state, defects and the consolidation proposal are in [`authorization-consolidation/`](authorization-consolidation/README.md); read that first.

**Status:** Proposed (design/plan, not yet implemented). **Date:** 2026-09-01.

This overview frames the proposed framework concerns and the seams between them. It is the entry point for the detailed system-design documents:

- [`authorization-design.md`](authorization-design.md) — **YFex.Authorization**
- [`capabilities-design.md`](capabilities-design.md) — **YFex.Capabilities**
- [`universal-search-design.md`](universal-search-design.md) — **Universal Search Bar** (the original driver; consumes both foundations)

## Why these exist

They came out of evaluating a **multi-provider Universal Search Bar** (intent *scorer*, not classifier — cascade of cheap detectors producing boosts, one fuzzy fallback, two-tier latency, frecency, permission-filtered results). That evaluation surfaced a prerequisite: the search bar, a future AI action layer, and menus all need to answer *"what can this user see/do here?"* cheaply and consistently. Today there is **no shared authorization primitive** — CQRS has a private authorizer vocabulary, NavigatR has none, and permission strings would otherwise be re-expressed per consumer. So before building search, we extract two foundations:

1. **YFex.Authorization** — one library owning app-authorization (permissions, roles, claims, policies, tokens) across four call surfaces: CQRS commands/queries, NavigatR routes, inbound HTTP, outbound HTTP.
2. **YFex.Capabilities** — a system-wide capability manifest (descriptions, params, side-effect class, permission, keywords) fed by pluggable **capability sources** — ViewModels, CQRS, messaging, HTTP APIs, jobs, routes — projected into three frozen registries for search / AI / authorization-visibility. ViewModels are just one source; any subsystem becomes AI-describable and searchable by adding one `ICapabilitySource`.

The Universal Search Bar itself is a **later** consumer built on top of both.

## Dependency layering

```
YFex (core: value-object convention, Union component)
└── YFex.Authorization.Public          (value objects, Policy/Requirement, IPolicyEvaluator, attributes)
    ├── YFex.Authorization             (evaluator, CompiledAuthorizationRegistry, DI)
    ├── YFex.Authorization.Generator   (catalog + [RequirePermission] generator, analyzer)
    ├── YFex.Authorization.AspNetCore  (inbound JWT + endpoint filter)
    ├── YFex.Authorization.Http        (outbound principal/token propagation)
    │
    ├── YFex.Cqrs            ── adds down-edge ──▶ .Public   (authorizer becomes an adapter)
    ├── YFex.NavigatR        ── adds down-edge ──▶ .Public   (declarative route permissions)
    └── YFex.Capabilities    ──────────────────▶ .Public + YFex.State + YFex.NavigatR
        └── YFex.Capabilities.Generator
```

Authorization sits **below** CQRS and NavigatR (they add one down-edge each into `.Public`). Capabilities sits above, referencing `.Public` for the shared attribute/value object. No cycles.

## The shared seam

Permission is declared **exactly once**, and both tracks read the same declaration:

- The attribute `[RequirePermission("orders.edit")]` is **owned by YFex.Authorization**, applied to a ViewModel class or a `[StateCommand]` method.
- Its argument is a **string literal** — a `const` from the permission catalog. This is the **only** place a raw permission string appears in source.
- Both source generators are read-only observers of that attribute (the authz generator emits gate glue; the capabilities generator folds the permission into its skeleton). Generators are pure/additive, so they never conflict.
- The raw string is wrapped into the `Permission` **value object** at exactly one point per track (authz: `CompiledAuthorizationRegistry.Build`; capabilities: `CompiledCapabilityRegistry.Build`). Everything downstream is value-object-only.

## Locked decisions

- **Catalog** = `public const string` ids carrying `[PermissionMeta(DisplayName, Description)]`; attribute-legal, IntelliSense-navigable, rename-safe. Analyzer flags a misspelled/unknown permission literal as a build error.
- **Value objects** (`Permission`, `PermissionKey`, `Role`, `PolicyName`, `Claim`, `ClaimType`, `AccessToken`, and the capabilities keys) follow the `YFex.Length` convention (`Parse`/`TryParse`/`ToString`/`.Value` + nested `TypeConverter` + `JsonConverter`). **No implicit string conversions** — implicit is reserved for total, non-throwing conversions (per `Length`'s `decimal→Length`); `string→identifier` validates, so it is explicit only. Migration off `string[]` uses temporary builder overloads, not implicit operators.
- **`.Requires(...)`** fluent override is kept as an **override-only escape hatch**; permission normally comes from the attribute. `Build()` fails fast if a `Mutating`/`Irreversible` command has no permission from either source.
- **System-wide, source-pluggable** (updated 2026-09-02): capabilities come from any `ICapabilitySource` — ViewModels, CQRS, messaging, HTTP APIs, jobs, routes — not only ViewModels. Key = `CapabilityKey = (CapabilityKind, OwnerType, MemberName)`; `Member` is `None` for type-level capabilities (a CQRS command, a job, an endpoint). Non-VM sources are thin bridges over each subsystem's already-compiled metadata; the subsystem never depends on `YFex.Capabilities`. Routes attach as 0..N metadata (today `IRouteRegistry.ResolveRouteType` is 1:1). `Deferred`/`Background` flags mark `IQueueable`/job capabilities so the AI surfaces "runs later/offline." See `capabilities-design.md` §3A.
- **Localization** (confirmed 2026-09-02): the capability manifest is **language-neutral** — every display/description/keyword slot is a `ResourceKey`, never embedded translation. The framework depends only on `Microsoft.Extensions.Localization.Abstractions`; the app plugs in the backing store (`.resx` standard, or a source-generated/JSON `IStringLocalizerFactory` for NativeAOT). Display resolves current-culture; **keywords resolve across all configured cultures at once**. Confirmed against Microsoft's Localization docs (updated 2026-03): `.resx`/`IStringLocalizer` is still standard, but the dependency to take is the abstraction and the store is pluggable. See `capabilities-design.md` §4.6.

## Cross-track reconciliations (authoritative)

The two designs were produced independently and disagreed on a few shared contracts. Resolved as follows — **YFex.Authorization owns the shared types**, so its definitions win:

1. **`Permission`** is defined by `YFex.Authorization.Public` as a `readonly record struct` wrapping a `PermissionKey` with the full value-object interface set (see [`authorization-design.md`](authorization-design.md) §4.2). YFex.Capabilities **references** it; it does not redefine it.
2. **`IPolicyEvaluator`** authoritative signature is `bool IsAllowed(in Permission, IAuthContext)` + `bool IsAllowed(Policy, IAuthContext)` + `ValueTask<AuthorizationResult> AuthorizeAsync<TResource>(Policy, IAuthContext<TResource>, CancellationToken)`. Capabilities' search/AI consumers call the sync `IsAllowed(in Permission, IAuthContext)`, obtaining `IAuthContext` from `IPrincipalAccessor.Current` (not a bare `ClaimsPrincipal`, not an `IsGranted` method).
3. **`AuthorizationManifest`** (authz-owned) is **catalog display metadata** — `PermissionMetaEntry[]` of id/display/description/declaring-member. It is **not** a per-capability required-permission map. The per-command required map is `IAuthorizationView.RequiredPermissions : FrozenDictionary<CapabilityKey, Permission>` (capabilities-owned). These are complementary with authz's `CompiledAuthorizationRegistry.ByViewModelType : FrozenDictionary<Type, Policy>`: authz's map gates **whole-screen navigation** by VM type; capabilities' `IAuthorizationView` gates **individual commands** for search/AI visibility. Both derive from the same `[RequirePermission]` attributes.
4. **`RequirePermissionAttribute`** declaration follows the authz owner: `AttributeTargets.Class | AttributeTargets.Method`. Class-level acts as an inherited default; method-level overrides. Resolution order (per command): profile `.Requires` override → method attribute → class attribute → none.
5. **Target frameworks** follow the repo's `Directory.Build.props` multi-targeting; the per-doc TFMs are indicative, not prescriptive. Generators target `netstandard2.0`.

## Notable finding to verify before implementation

The authz track reports that CQRS's `AuthorizerKind.PolicyName` and `TypeRef` currently compile to a `null` predicate (`CompiledMessagingRegistry.cs:289`), so `.RequireAuthorization("policy")` / `.Authorize<T>()` may be **silently unenforced** on the local dispatch path (`LocalDispatcher.cs:133/197`). **Verify against the live dispatch flow** (a comment implies a dispatcher was meant to do a DI lookup). If confirmed, it is an independent security fix; the authz rollout closes it in phase 3 with a release-note behavior change.

## Consolidated open decisions

- Permission → claims resolution: resolve once at principal construction via `IPermissionProvider` (role/claim → permission-key expansion), cached as a `FrozenSet<PermissionKey>` on `IAuthContext` (keeps the sync visibility path allocation-free). Confirm claim shape.
- Move CQRS off ambient `ClaimsPrincipal.Current` onto injected `IPrincipalAccessor` (with a back-compat adapter during transition).
- Default `SideEffect` when a profile omits a command entry: `Mutating` if `[RequirePermission]` present, else `ReadOnly`.
- Whether `IAuthorizationView` mirrors or delegates to the authz side (single source vs. two aligned projections) — see reconciliation #3.
- Profile discovery: `CreateSyntaxProvider` now, migrate to a `[CapabilityProfile]` attribute + `ForAttributeWithMetadataName` for a cheaper gate.
- Outbound on-behalf-of may need a new `OnBehalfOfCredential` variant in the Web `ApiCredential` union.

## Reuses of existing infrastructure

- **NavigatR**: `IRouteRegistry` / `RouteAttribute` / `IRoute`, `NavigationResult`/`NavigationDenied` union, `Navigator` nav+prefetch insertion points.
- **YFex.Entity.Public**: `Cpf`/`Cnpj`/`CpfCnpj`/`Rg` etc. — their `TryParse` (checksums) is the search format-detector layer.
- **YFex.Persistence**: `IKeyValueStore` (frecency), `ICache` (LRU query→results).
- **YFex core**: `Fresh/Cached/Stale/Miss`, `[Union]`/`union`, `FrozenDictionary`, `PagedList<T> : IAsyncEnumerable<T>`.
- **State generator**: existing `[Observable]`/`[StateCommand]` member + XML-doc discovery, reused (ideally via an extracted `YFex.State.Generator.Abstractions`).
- **CQRS**: `CompiledMessagingRegistry` is the register→compile→frozen blueprint both compiled registries mirror; `IAggregateConfiguration<T>` is the authoring-surface pattern both builder APIs mirror.
