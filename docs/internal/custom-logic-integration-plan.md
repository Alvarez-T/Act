# CustomLogic / Tenancy — Cross-Project Integration Evaluation & Plan

**Status:** Evaluation + plan; §7 decisions settled 2026-09-24; I1 done 2026-09-25; §8–§10 (implicit
tenancy, YFex.Data, optionality audit) + I2a/I2b done 2026-09-28. Companion to
[custom-logic-design.md](custom-logic-design.md), which built `YFex.Tenancy` (scope model) and
`YFex.CustomLogic` (per-scope override engine), wired CQRS, NavigatR routes, and Avalonia view/
composition. This doc evaluates every other YFex library for whether it should integrate, and how.

> All repos live under `C:\Dev\YFex\*` (siblings).
>
> **Build state (2026-09-24):** `YFex.Tenancy` and `YFex.CustomLogic` were extracted from the main
> `YFex` repo into their **own sibling solutions** — `C:\Dev\YFex\YFex.Tenancy\` (`tenancy.slnx`) and
> `C:\Dev\YFex\YFex.CustomLogic\` (`customlogic.slnx`, holding `.CustomLogic` + `.Generator` + `.Cqrs`
> + tests). Cross-repo `ProjectReference`s use the `..\..\..\..\` sibling convention; `yfex.slnx`
> references the five moved projects under folder `/Tenancy/`. So any library integrating below
> references `..\..\..\..\YFex.Tenancy\src\YFex.Tenancy\YFex.Tenancy.csproj` (Kind B/C) or the
> `YFex.CustomLogic` sibling (Kind A) — **not** an in-repo `src/Tenancy/` path.
>
> `YFex.Web` is its own solution too — nothing here can break its build; the Web question is
> conceptual overlap, now planned in
> [web-tenancy-consolidation-plan.md](web-tenancy-consolidation-plan.md).

---

## 0. The key framing — three kinds of integration

The single most important realization from the survey: "integrate with tenancy" means **three
different things**, and a library usually wants exactly one of them. Keeping them apart is what
keeps this clean.

| Kind | What it means | Depends on | Mechanism |
|---|---|---|---|
| **A — Behavioral override** | *Different code runs* for a scope | `YFex.CustomLogic` | `[CustomLogic]` + the frozen registry / dispatch hook |
| **B — Data isolation** | *Same code, partitioned storage/keys* so tenants don't leak | `YFex.Tenancy` only | Prefix/namespace keys by the scope chain (`IScopeContext`) |
| **C — Scope provider** | *Supplies* the current identity | `YFex.Tenancy` only | Implement `IScopeContext` from the host's notion of "current user/tenant" |

**A** is the customization feature. **B** is a multi-tenancy correctness concern (shared-device
offline data, shared server DB) that is *not* CustomLogic — it only needs the identity. **C** is the
adapter layer. A library that wants B or C must **not** take a dependency on `YFex.CustomLogic` — only
on `YFex.Tenancy`.

---

## 1. Per-project verdict

| Project | Kind | Priority | Needs |
|---|---|---|---|
| **YFex.Cqrs** (commands) | A | ✅ done | `ICommandCustomLogic` seam + `LocalDispatcher` hook (shipped in P1) |
| **YFex.Cqrs** (queries) | A | Med | Symmetric `IQueryCustomLogic` seam — currently queries have no override |
| **YFex.Cqrs** (cache) | B | Med | Query cache keys must include tenant/org, not just `ScopedByUser` |
| **YFex.Messaging.Rpc** (dispatch) | A | ✅ done | Hosts the command hook (P1) |
| **YFex.Messaging.Rpc** (outbox/cache/sync) | B | **High** | Partition offline outbox + client cache + sync cursors by scope — shared-device leak risk |
| **YFex.Messaging** (events/`[Subscribe]`) | A | Low | Optional scope-filtered subscribers; niche |
| **YFex.EventSourcing** (aggregate handlers) | A | ✅ free | ES aggregate command handlers are ordinary `ICommandHandler` → already flow through the dispatcher hook |
| **YFex.EventSourcing** (`StreamId`) | B | **High** | `StreamId` is `"order-1042"` — **no tenant partition**; cross-tenant stream collision. ~~Needs scope prefix~~ → in-house store is covered by `IsolatePersistence()`; only Marten remains (§11.2) |
| **YFex.Jobs** | B/C | ✅ done | Job scope = `ScopeIdentity` via `[ScopedTo<TScope>]`; body runs in `ScopeOverride` (§11.3) |
| **YFex.Orchestration** (saga steps) | A | ✅ free | Steps dispatch via `IDispatcher` → per-scope command overrides apply per step automatically |
| **YFex.Orchestration** (saga definition) | A | Low | Optional scope-aware saga *selection* (different step graph per tenant) |
| **YFex.Orchestration** (saga store) | B | Med | Partition saga envelopes by scope |
| **YFex.Capabilities** | A | **High** | Capabilities become scope-aware — see §2 (the one the user flagged) |
| **YFex.Search** | A | Med | Scope-specific `ISearchProvider` selection (a tenant's custom source) |
| **YFex.Persistence** | B | **High** | Namespace local app-state keys by scope — shared-device isolation |
| **YFex.Authorization** | C | Med | Source of the identity: adapter maps `ClaimsPrincipal` → `ScopeIdentity` (org/tenant/user claims) |
| **YFex.Web** (+ .Data/.AspNetCore/.Refit) | C | **High** | Implement `IScopeContext` from `ICurrentTenantProvider` + principal; reconcile the two tenancy models (§3) |
| **YFex.Profiling** | B | Low | Consent is per-user; key consent store by (tenant,user) when multi-tenant |
| **YFex.Resilience** | A | Low | Edge case — per-tenant policy via a behavioral wrap; skip for now |
| **YFex.System** | — | none | OS/host services; no tenancy relevance |
| **YFex.Security** | — | none | Machine/OS-level (Windows interception); no tenancy relevance |

**"Free" rows are the payoff of putting the hook in the dispatcher:** anything dispatched as a
command — plain CQRS, an event-sourced aggregate decider, a saga step — inherits per-scope override
with zero new code. The hook sits before `_invoker.InvokeAsync` in `LocalDispatcher`, so ES and
Orchestration get A automatically.

---

## 2. YFex.Capabilities — how it changes (the one you asked about)

Today `CompiledCapabilityRegistry.Build(IEnumerable<ICapabilitySource>)` fans in sources, projects
`CapabilityDescriptor`s into three frozen registries (Search / Describe / Authz), and **throws on a
duplicate `CapabilityKey`**. Capabilities are global — one manifest for everyone. It already
consumes NavigatR's `RouteRegistry` for route resolution (which is now scope-aware, §P2).

**The change:** add a **scope dimension** so the *same* capability can vary — or exist only — per
organization / tenant / user.

1. **Key gains scope.** `CapabilityKey` → `(existing key, Scope, string value)`, or a parallel
   scoped overlay keyed like `CustomLogicKey`. Same-capability-different-scope stops being a
   "duplicate" error and becomes an override.
2. **Build keeps a base table + a scoped overlay.** Base = today's global manifest. Overlay =
   scoped descriptors. No behavioral change when no scoped descriptor exists.
3. **Query becomes scope-filtered — with BOTH resolution modes (OD1, decided: keep both).** The
   scoped overlay supports two composition modes, chosen per descriptor via a flag:
   - **Union (additive)** — a scope's capabilities are *added to* the base manifest. This is the
     default for "this tenant/org *also* has capability X" (e.g. Acme's "Bulk import").
   - **Most-specific override** — a scope entry *replaces* the base for that key. Used to **hide**
     (`CapabilityFlags.Hidden` per scope) or **re-permission** an existing capability.
   Query walks User→Tenant→Org: union entries accumulate least-specific→most-specific; override
   entries take the most-specific match. No scoped descriptor → base manifest, unchanged.
4. **A scope-aware `ICapabilitySource`.** The generator that emits capability sources learns the
   `[CustomLogic(Scope,value)]`-style annotation (or a capability-native `[CapabilityFor(...)]`) so
   authors declare scoped capabilities the same way.

**Why this is the natural fit:** `CompiledCapabilityRegistry` is already a register→compile→frozen
pipeline that mirrors `CustomLogicRegistry`, and it already depends on the scope-aware
`RouteRegistry`. Capabilities does **not** need `YFex.CustomLogic` — it grows its own scope
dimension using `YFex.Tenancy`'s `Scope`/`ScopeIdentity`/`IScopeContext` (Kind A, but self-hosted,
because the capability manifest is its own frozen structure, not a command dispatch).

**Concrete "capability profile" example:** `ICapabilityProfile<TViewModel>` stays per-VM; a new
`ICapabilityProfile<TViewModel>` variant (or attribute) tagged with a scope contributes descriptors
only active for that scope — e.g. `AcmeOrderEditorCapabilities` adds a "Bulk import" action visible
only to the `acme` org.

---

## 3. YFex.Web — no breakage, but two tenancy models to reconcile (Kind C)

> **⚠ Superseded.** This section proposed the *conservative* "keep two models + one adapter"
> approach. It is **withdrawn** in favour of a full consolidation — the host-neutral half of
> `YFex.Web`'s tenancy moves down into a new `YFex.Tenancy.Resolution`, and `YFex.Web` keeps only its
> ASP.NET adapters. See [web-tenancy-consolidation-plan.md](web-tenancy-consolidation-plan.md), which
> also owns OD4 and OD5. The text below is kept only for the breakage analysis (still accurate: no
> collision, `YFex.Web` is a separate solution).

`YFex.Web` **already has a tenancy stack**: `ITenantInfo` (Id/Identifier/Name), `TenantContext<T>`,
`ICurrentTenantProvider`, `ITenantResolver` + host strategies (host/header/claim), `ITenantStore`,
`AsyncLocalTenantContextAccessor`, `TenantResolutionMiddleware`. `YFex.Tenancy` introduces a
**parallel, richer** model: `ScopeIdentity` (Org/Tenant/User) + `IScopeContext`.

- **Breakage: none.** `YFex.Web` is a separate solution and does not reference `YFex.Tenancy`. No
  namespace collision (`YFex.Web.Tenant.*` vs `YFex.Tenancy.*`). Adding the reference is opt-in.
- **The reconciliation (recommended):** `YFex.Web` implements `IScopeContext` as a thin adapter over
  what it already resolves:
  - **Tenant rung** ← `ICurrentTenantProvider.Current.Id` (its existing resolution stack stays the
    authority for *how* the tenant is discovered).
  - **User rung** ← the authenticated principal (`ClaimsPrincipal` subject).
  - **Org rung** ← only when the app's tenant implements the richer shape its own doc comment
    anticipates ("organization + establishment"); add the optional
    `IOrganizationScoped : ITenantInfo { string OrganizationId; }` from the design doc (§2.4) so the
    adapter can fill it. Absent → Org rung omitted, and resolution still works with Tenant+User.
- **Do NOT merge the two models.** `YFex.Web.Tenant.*` owns *discovery* (middleware/strategies/
  stores — legitimately web infrastructure). `YFex.Tenancy` owns the *scope abstraction* consumed by
  CQRS/UI/Capabilities. One adapter class (`HttpScopeContext : IScopeContext`) is the whole bridge.
- **Relatives** (`YFex.Web.Data`, `.AspNetCore`, `.Refit`): unaffected except that `.AspNetCore`
  hosts the adapter registration (`AddYFexWebScope()`), and `.Data`/`.Refit` that key caches/tokens
  by tenant become Kind-B candidates (namespace by the scope chain).

---

## 4. YFex.EventSourcing — two touchpoints

- **A (free): behavioral override.** An `[AggregateCommandHandler]` compiles to an ordinary
  `ICommandHandler<TCommand,TResult>` routed by `LocalDispatcher`, so a `[CustomLogic]` override for
  that command is consulted before the aggregate decider runs. No ES change needed — verify by test.
- **B (optional, opt-in — OD2, decided): stream partitioning.** `StreamId.For<T>(id)` yields
  `"order-1042"` with **no tenant segment**. On a shared server DB (or a synced multi-tenant client)
  two tenants' `order-1042` collide. **This is opt-in, off by default** — a single-tenant app is
  never forced to prefix. Plan: introduce a pluggable `IStreamPartitioner` (default = identity,
  no-op) that, when registered, makes the stream prefix scope-aware — `"t:{tenantId}:order-1042"` —
  by consulting `IScopeContext`. An app that *might* use tenants wires the partitioner once; one that
  doesn't pays nothing and stream ids are byte-for-byte unchanged. Data isolation, not CustomLogic;
  `YFex.EventSourcing` takes a `YFex.Tenancy` reference only. When enabled, sync
  (`StreamReconciler`/`EventIngest`) carries the tenant so streams reconcile within a tenant.

---

## 5. Kind-B data-isolation backlog (Tenancy-only, no CustomLogic)

These share one pattern: **prefix the storage key / partition with the scope chain** so offline or
shared storage never leaks across tenants. Each takes a `YFex.Tenancy` reference and reads
`IScopeContext`.

> **Optional by default (OD3, decided — same treatment as OD2).** Every Kind-B integration ships
> behind an **opt-in seam that is off by default**: a pluggable partitioner/key-namespacer that
> resolves to a no-op identity until the app registers tenant partitioning. A single-tenant app
> gets today's exact keys/streams, zero overhead; an app that "might use tenants" flips one switch
> per subsystem (or one global `AddYFexTenantIsolation()`) to turn them all on. The scope chain
> feeds these as **raw strings — no interning** on this path (interning is only the CustomLogic /
> Capabilities hot loop).

- **YFex.Persistence** — namespace local app-state keys by scope (highest priority: shared-device).
- **YFex.Messaging.Rpc** — outbox entries, client cache, and sync cursors partitioned by scope
  (offline command/query data must not cross tenants on one device).
- **YFex.Cqrs** query cache — extend the existing `CacheScope`/`ScopedByUser` to
  `ScopedByTenant`/`ScopedByOrg` so cache keys include the tenant.
- **YFex.EventSourcing** streams — §4 B.
- **YFex.Orchestration** saga store — partition envelopes by scope.
- **YFex.Profiling** consent — key the consent store by (tenant, user).

---

## 6. Phasing

- **I0 — Scope providers (Kind C).** `YFex.Web` `HttpScopeContext` + `IOrganizationScoped`;
  `YFex.Authorization` `ClaimsPrincipal → ScopeIdentity` helper; Avalonia already has
  `MutableScopeContext`. *Unlocks everything else — nothing scopes correctly without a real identity.*
- **I1 — Capabilities scope dimension (§2). ✅ DONE (2026-09-25).** `CapabilityScope` + optional
  `CapabilityDescriptor.Scope`; `CompiledCapabilityRegistry` splits base vs scoped and adds
  `DescribeFor`/`SearchFor`/`AuthzFor(in ScopeIdentity)` composing base+overlay (union + override per
  OD1; most-specific wins; Hidden→drop from search; base returned when unscoped — zero overhead).
  **Authoring (a):** `ScopedCapabilitySource` decorator (`AddScopedCapabilitySource<TScope>(…)`) stamps
  a scope onto any inner source's descriptors, and `IScopedCapabilityProfile` scopes a whole VM profile
  (ViewModelCapabilitySource stamps + dedupes by (VM, scope)). *(Chosen over a reflection-read
  `[CapabilityFor]` attribute — source-pluggable + AoT-safe; a generator attribute would just
  auto-register a `ScopedCapabilitySource` and can be added later.)* **Consumers (b):**
  `CapabilitySearchService` + `CapabilityAgentApi` gained a scope-aware ctor (`CompiledCapabilityRegistry`
  + optional `IScopeContext`) resolving the per-identity manifest each call; DI wires the optional
  `IScopeContext` (absent → base, zero overhead). YFex.Capabilities refs `YFex.Tenancy`. **38 Capabilities
  tests green** (9 new). Deferred refinement: a generator `[CapabilityFor]` attribute; per-member
  selective profile scoping (today a whole scoped profile or a dedicated scoped source).
- **I2 — Data isolation (Kind B, §5), implicit-by-default per §8.** Split (2026-09-28):
  - **I2a — foundation in `YFex.Tenancy`** (§8.2) — **✅ DONE (2026-09-28)**, `YFex.Tenancy.Tests` 11/11
    (new test project in `tenancy.slnx`); `HttpScopeContext` honors the override (YFex.Web 77/77 green): `ScopeOverride` (explicit, nestable, AsyncLocal),
    `IScopeContext.EffectiveIdentity()` (override → provider → None), `IScopePartitioner` +
    `ScopeKeyPartitioner` + `ScopePartitionOptions` (rank window, unscoped policy), the fluent
    `AddYFexTenancy(t => …)` builder that is the single configuration point. Shipped providers
    (`AsyncLocal`/`Mutable`/`HttpScopeContext`) honor the override.
  - **I2b — `YFex.Persistence.Tenancy` bridge** — **✅ DONE (2026-09-28)**, `YFex.Persistence.Tenancy.Tests`
    8/8 (both in `yfex.slnx` `/Persistence/`). New package, mirrors `.Encryption`: scoping
    decorators for `IKeyValueStore` / `ISyncLocalStore` / `ICache`; `t.IsolatePersistence()`.
    `YFex.Persistence` core gains **no** tenancy reference.
  - **I2c — Cqrs cache + Messaging.Rpc**: a tenancy-backed `ICacheScopeContext` (retires the third,
    separately-wired tenant source, §8.4) + outbox/sync-cursor partitioning.
  - **I2d — ES `IStreamPartitioner`, saga store, consent** — each a bridge over `IScopePartitioner`.
- **I5 — YFex.Data tenancy (optional, §9).** I5a connection routing (db/schema-per-tenant) +
  schema session hook. ~~I5b shared-DB tenant column helpers~~ is **superseded**: RLS `Application`
  mode already filters shared-DB tables on every engine, including SQLite. **I5c RLS adapter — ✅ DONE (2026-09-28)**, built in the
  `YFex.Data` repo: `src/YFex.Data.Tenancy` (`TenancyRowSecurityContext`, `t.IsolateData(d => d.Map(...))`,
  optional `RowSecurity` singleton) + `tests/YFex.Data.Tenancy.Tests` 7/7 (real SQLite); the parameter-type
  fix is in RLS itself (bindings carry `DbType`; `RowSecurityValues.ConvertTo`) with 3 new tests. Whole
  `data.slnx` 76/76.
  **I5a + generator + DI — ✅ DONE (2026-09-28, later).** All in `YFex.Data`; the core stays tenancy-free:
  - **Generator-declared levels:** `[RowSecurityKey<TScope>(level)]` makes the generator emit
    `RowSecurityKey.ScopeRank` (a plain `byte`, plus the `YFRLS006` byte-range diagnostic).
    `TenancyRowSecurityContext` reads levels from the catalog (explicit `Map` wins; conflicting levels
    for one key throw), so `d.Map` is optional.
  - **DI:** `d.AddConnection(create, dialect, key?, rowSecurity = true)` registers a (keyed)
    `YFexConnectionFactory` with the tenancy `RowSecurity` attached.
  - **Routing:** `d.AddTenantConnection(level, tenant => DbConnection, dialect, key?, rowSecurity = false,
    onOpened?)` resolves the tenant at `Create()` and fails closed (`ScopeIsolationException`). Schema per
    tenant goes through the connection string (Npgsql `Search Path`) or `onOpened`. No new
    connection-open seam was needed: `onOpened` uses `DbConnection.StateChange`.
  - **Tests:** YFex.Data.Tests 92 (+2 generator), YFex.Data.Tenancy.Tests 12 (routing against real
    per-tenant SQLite files); YFex.Web 77 still green.
- **I3 — Query overrides + Search providers.** `IQueryCustomLogic` symmetric to commands;
  scope-aware `ISearchProvider` selection.
- **I4 — Nice-to-haves.** Scope-aware saga *definition* selection; per-tenant resilience; scoped
  event subscribers.

**Dependency rule enforced throughout:** Kind-B/C libraries reference **`YFex.Tenancy` only**;
`YFex.CustomLogic` stays a dependency of the behavioral (Kind-A) consumers alone. `YFex.System` and
`YFex.Security` get nothing.

---

## 7. Decisions (settled 2026-09-24)

- **OD1 — Capabilities scope resolution → DECIDED: keep BOTH.** Union (additive) *and*
  most-specific override, chosen per descriptor via a flag. Union covers "this scope also has X";
  override covers hide / re-permission. See §2.3 (updated). Union accumulates least→most specific;
  override takes the most-specific match.
- **OD2 — Stream-id partitioning → DECIDED: optional, opt-in.** Tenant partitioning is off by
  default and pluggable (an `IStreamPartitioner`, default no-op). An app "might use tenants or might
  not" — wiring the partitioner is one call; not wiring it leaves stream ids unchanged. Design an
  easy integration seam rather than baking tenant into `StreamId`. See §4 (updated).
- **OD3 — Kind-B data isolation → DECIDED: optional, opt-in (same as OD2).** Every Kind-B subsystem
  (Persistence, Messaging.Rpc offline, ES streams, CQRS cache, saga store, consent) ships behind an
  off-by-default partitioner/key-namespacer seam; opt in per subsystem or globally
  (`AddYFexTenantIsolation()`). Kind-B keys use raw strings — **no interning** on this path. See §5
  (updated).
- **OD4 — Web `IScopeContext` ownership → MOVED to
  [web-tenancy-consolidation-plan.md](web-tenancy-consolidation-plan.md).** Resolved there:
  `HttpScopeContext : IScopeContext` lives in `YFex.Web.AspNetCore` (the ASP.NET adapter), projecting
  the resolved tenant + principal; the host-neutral resolution machinery moves into
  `YFex.Tenancy.Resolution`. That plan owns the final decision and wiring.
- **OD5 — Cross-tenant admin → DECIDED: YES, must be supported; designed in
  [web-tenancy-consolidation-plan.md](web-tenancy-consolidation-plan.md).** A support/admin user
  acting across tenants is a first-class requirement (explicit, audited impersonation of a target
  tenant while the User rung stays the real admin). The scope-chain/impersonation mechanics belong in
  the Web consolidation plan since that is where the identity is projected.

---

## 8. Implicit tenancy — ambient by default, explicit when needed (2026-09-28)

**Requirement.** Developers must not thread a tenant id through every call. Configure tenancy in
**one place**; every integrated library picks it up automatically. Explicit calls (passing a tenant)
must still be possible. Every integration stays **optional** (§10).

### 8.1 Verdict — supported, and the core already has the right spine

`IScopeContext` *is* the ambient "current tenant" and every Kind-A/B/C consumer already reads it
optionally. What was missing: (1) an **explicit override** primitive, (2) one **key-partitioning
policy** shared by all Kind-B consumers, (3) a **single configuration entry point**, (4) collapsing a
**third tenant source** (Cqrs `ICacheScopeContext.TenantId`, wired separately today — §8.4).

**Where the "automatic" lives — boundary decorators, not generated call sites.** A source generator
runs at *compile time*; "which service holds the tenant" is a *runtime/DI* fact. So generators never
locate the tenancy service themselves. The implicit behavior is placed at each library's **storage
boundary** — a store decorator, a cache-key builder, a connection-open hook — reading
`IScopeContext` at call time. A boundary decorator also catches hand-written code; generated-only
scoping would leak on the first hand-written call. Generators add value in three narrower ways:

1. **Emit against a well-known seam, resolved from DI** (what `YFex.CustomLogic.Generator` already
   does) — never a concrete tenancy type.
2. **Compile-time presence detection** — `compilation.GetTypeByMetadataName("YFex.Tenancy.IScopeContext")`;
   emit scope-aware code only when the tenancy assembly is referenced → zero cost / zero dependency
   when absent. Attributes are matched **by metadata name**, so the consuming library needs no ref.
3. **Declarative per-type policy + diagnostics** — e.g. `[TenantScoped]` / `[TenantShared]` on a
   table/entity/persisted type (read by the Data / Persistence / Cqrs generators); diagnostics such as
   "`[TenantScoped]` used but no tenancy bridge referenced".

### 8.2 Shape (I2a, `YFex.Tenancy`)

```csharp
// ONE place — the app's composition root:
services.AddYFexTenancy(t => t
    .Isolation(o => o.UpTo(MyScope.Tenant))               // partition policy (also From/Between/FailClosed)
    .IsolatePersistence()                                // from YFex.Persistence.Tenancy   (I2b)
    .IsolateQueryCache()                                 // from a Cqrs bridge              (I2c)
    .IsolateData(...));                                  // from YFex.Data.Tenancy          (I5)
// + the host's provider: AddYFexWebTenant<T>() / MutableScopeContext / AsyncLocal (default)

// Implicit — nothing to pass:
await store.SetAsync("draft", bytes);                    // → "~s/0=acme/draft"

// Explicit — pass a tenant for a block (nestable; restores on dispose; flows across await):
using (ScopeOverride.Enter(ScopeValue.Of(MyScope.Tenant, "globex")))
    await store.SetAsync("draft", bytes);                // → "~s/0=globex/draft"
using (ScopeOverride.EnterUnscoped())                    // explicit "global/device" operation
    await store.SetAsync("device-id", bytes);            // → "device-id"
```

- **`ScopeOverride`** — static, `AsyncLocal`-backed stack. `Enter(...)` is synchronous (so the value
  flows *into* the awaited work, never lost back to the caller). Works **even with no
  `IScopeContext` registered** — explicit calls need no provider.
- **`IScopeContext.EffectiveIdentity()`** (extension; tolerates a null context) — override → provider
  → `None`. Every Kind-B bridge reads this, so a third-party `IScopeContext` that forgets the override
  is still correct. Shipped providers honor the override in `Current`/`Resolve` too, so Kind-A
  (CustomLogic/Capabilities/NavigatR) also sees explicit overrides.
- **`IScopePartitioner`** — `PrefixFor(in ScopeIdentity)` / `CurrentPrefix()`. Default
  `ScopeKeyPartitioner`: prefix `~s/{rank}={value}/…` **least-specific-first**, so a tenant prefix is a
  string-prefix of all its users' prefixes (enables "wipe tenant X" by prefix). `/` and `%` in values
  are escaped. `ScopePartitionOptions`: `MinRank`/`MaxRank` window (e.g. partition the cache by
  tenant but user-state by tenant+user), `WhenUnscoped = Shared | Throw` (fail-closed opt-in).
- **Security note.** `ScopeOverride` is in-process trusted code (same trust as calling the store
  directly). Untrusted input (HTTP headers) still goes through the gated/audited impersonation path
  (web plan §4.5); never map a request value straight into `ScopeOverride`.
- **Off by default (OD3).** No `Isolate*()` call → no decorator → keys byte-for-byte unchanged.

### 8.3 Per-library "automatic" mechanism

| Library | Implicit mechanism | Explicit | Generator role |
|---|---|---|---|
| Persistence | `IKeyValueStore`/`ISyncLocalStore`/`ICache` scoping decorators (I2b) | `ScopeOverride`; `SharedKeyPrefixes` for device-global keys; `CachePartition` gives the cache its own window (e.g. drafts per operator, catalog per store) | none needed (decorator catches all) |
| Cqrs cache | tenancy-backed `ICacheScopeContext`; `ScopedByTenant()` w/o a lambda reads it | `ScopeOverride` | Cqrs generator may default tenant scoping when isolation is on (later) |
| Messaging.Rpc | outbox/sync-cursor keys via `IScopePartitioner` | `ScopeOverride` | none |
| EventSourcing | `IStreamPartitioner` over `IScopePartitioner` (OD2) | `ScopeOverride` | none |
| CustomLogic / Capabilities / NavigatR | already read `IScopeContext` (override-aware after I2a) | `ScopeOverride` | CustomLogic gen already seam-based |
| YFex.Data | connection router + connection-open session hook (§9) | `ScopeOverride`; `factory.Create(identity)` | optional `[TenantScoped]` table metadata |

### 8.4 One source of truth — retire the third tenant source

`YFex.Cqrs.ICacheScopeContext { User, TenantId, SessionId }` is resolved by
`Messaging.Rpc.QueryCacheKey` via `GetService` and today is wired independently of `IScopeContext`.
Keep the seam (Cqrs correctly takes no tenancy ref) and supply a **bridge implementation** whose
`TenantId` = the configured tenant rung of `EffectiveIdentity()`. After that every tenant read in the
stack derives from one resolution.

---

## 9. YFex.Data — optional tenancy (I5)

`YFex.Data` is a raw-SQL builder + Dapper over `YFexConnection`; no entity model, no generator. Every
DB call goes through `YFexConnectionFactory.Create()` → `YFexConnection.OpenAsync()`, so those two
are the natural implicit hooks. **All optional**: a database that does not use tenancy never registers
anything and `YFex.Data` takes no tenancy reference — the bridge is a new `YFex.Data.Tenancy` package.

Three isolation modes (per database — an app can mix: a shared catalog DB + per-tenant DBs):

| Mode | Implicit mechanism | Notes |
|---|---|---|
| **Database-per-tenant** | tenancy-aware connection factory: resolves the connection string for `EffectiveIdentity()` via an app-supplied `ITenantConnectionResolver` | Strongest isolation; pooling is per-connection-string. |
| **Schema-per-tenant** | same router + dialect-specific default schema on open (`search_path`, `ALTER SESSION SET CURRENT_SCHEMA`, …) | Needs a per-dialect hook in `IQueryDialect`. |
| **Shared DB, tenant column** | (a) **RLS** — push the tenant into the DB session on open (`set_config('app.tenant_id',…)`, `sp_set_session_context`, Oracle VPD context); the DB enforces it. (b) app-level helpers — `SqlQueryBuilder.WhereTenant()` / insert column fill for engines without RLS (SQLite). | (a) is the robust one: raw SQL can't bypass it. (b) only covers builder-composed SQL. |

**Seams needed in `YFex.Data` core (tenancy-free, all default no-op):** an `IConnectionInitializer`
invoked after `OpenAsync` (also what RLS needs), and an optional connection-string selector on the
factory. `YFex.Data.Tenancy` implements both over `IScopeContext`. Explicit: `ScopeOverride` or a
`factory.Create(in ScopeIdentity)` overload. Pooling caveat: session context must be set on **every**
open (pooled connections carry the previous tenant's session state) and cleared/reset on return.

**RLS status (checked 2026-09-28, later the same day).** The RLS session is idle and its work is in
the `YFex.Data` tree **uncommitted**: `src/YFex.Data/Security/`, `src/YFex.Data.Generator/` (the
`[RowLevelSecurity]` / `[RowSecurityKey]` generator), `docs/ROW-LEVEL-SECURITY.md`, and
`tests/YFex.Data.Tests` (**64/64 green**, including SQLite integration). Modes: `Application` (lexical
SQL rewrite, all engines), `Database` (native Postgres / SQL Server / Oracle VPD policies + session
push before each command, tracked per physical open), or both. **It has no Tenancy integration**, and
correctly takes no Tenancy reference. Its seam is `IRowSecurityContext { TryGetValue(string key, out
object?), IsBypassed }`, fed by `RowSecurityContext` (its own AsyncLocal `Set`/`Bypass`) or a
`DelegateRowSecurityContext`.

**Consequence for this plan.** RLS already pushes session values before each command, which handles
pooled connections. So **I5c needs no new connection-open seam**: it is just an adapter. The
`IConnectionInitializer` idea above stays only for schema-per-tenant (I5a).

**I5c design, `YFex.Data.Tenancy` bridge:**
- `TenancyRowSecurityContext : IRowSecurityContext` maps RLS context keys to scope levels
  (`Map("TenantId", MyScope.Tenant)`, optional `Map<int>(…)` with a converter). It reads
  `EffectiveIdentity()`, so `ScopeOverride` and web impersonation apply automatically.
- It checks `RowSecurityContext.Ambient` **first**, so explicit `RowSecurityContext.Set(...)` and
  `Bypass()` keep working (also for non-tenant keys like `Region`).
- "Not in scope" is left to RLS's own `MissingValue` (`Throw` / `DenyAll`), so there is one policy
  for missing values, not two.
- `t.IsolateData(...)` registers the adapter; apps pass it into `new RowSecurity(options, context: …)`.

**Issues found in the RLS work (fixed 2026-09-28 at the user's request, in `YFex.Data`):**
1. ✅ **Parameter type in application mode.** `RowSecurity.Secure` binds the context value exactly as
   given; the key's `DbType` is used only for native DDL. Tenancy values are strings, so an `int`
   `TENANT_ID` gets a text parameter. That is likely an error on PostgreSQL (`integer = text`) and an
   implicit conversion elsewhere. Fix inside RLS: convert bound values using the key's `DbType`
   (the generated `Stamp`/`Matches` already convert via `RowSecurityValues.Convert<T>`). Otherwise the
   bridge must convert per key.
2. ✅ (doc + XML comment now point at `YFex.Data.Tenancy`) The doc and XML comment pointed at "YFex.Tenancy's `ICurrentTenantProvider`". That type lives in
   `YFex.Tenancy.Resolution` and only knows the web-resolved tenant; the generic source is
   `IScopeContext` via the bridge above.

---

## 10. Optionality audit (2026-09-28)

**Rule:** every integration is **runtime-optional** (absent tenancy ⇒ today's behavior, zero
overhead). At the **package** level a consumer may reference only the tiny `YFex.Tenancy` core
(YFex + DI.Abstractions, AoT); anything that brings behavior (decorators, DB hooks) ships in a
`YFex.<Lib>.Tenancy` bridge so the library itself has no tenancy dependency.

| Library | Package dep | Runtime | Verdict |
|---|---|---|---|
| YFex.Cqrs | none (own `ICommandCustomLogic` / `ICacheScopeContext` seams) | optional | ✅ gold standard |
| YFex.Messaging.Rpc | none | optional (`GetService`) | ✅ |
| YFex.NavigatR | `YFex.Tenancy` | optional (`GetService<IScopeContext>`) | ✅ acceptable (core only) |
| YFex.Capabilities | `YFex.Tenancy` | optional (nullable ctor arg, `GetService`) | ✅ acceptable |
| YFex.Avalonia | `YFex.Tenancy` | optional (`ScopeContext` nullable) | ✅ acceptable |
| YFex.CustomLogic | `YFex.Tenancy` | required *by design* (it is the per-scope feature) | ✅ — opting into CustomLogic = opting into scopes |
| YFex.Web | `YFex.Tenancy.Resolution` from core `YFex.Web` | optional (only `AddYFexWebTenant` wires it) | ⚠ package-level: every YFex.Web user pulls Resolution because `ICurrentTenantProvider` moved there. Acceptable (small, AoT); revisit only if YFex.Web ships without tenancy |
| YFex.Persistence | none — bridge `YFex.Persistence.Tenancy` (I2b) | off until `IsolatePersistence()` | ✅ by construction |
| YFex.Data | none — bridge `YFex.Data.Tenancy` (I5) | off per database | ✅ by construction |
| YFex.EventSourcing / Orchestration / Profiling | none yet — bridges in I2d | off by default (OD2/OD3) | ✅ planned |
| YFex.Jobs | `YFex.Tenancy` (core; job scope **is** `ScopeIdentity`, by decision §11.3) | a job without `[ScopedTo]` behaves as before (runs unscoped) | ✅ done |

---

## 11. Cqrs, EventSourcing, Jobs — re-evaluation incl. source generation (2026-09-28)

Re-checked against the code after I2a/I2b/I5 landed. **Headline: none of the three needs a
source-generator change to be tenant-correct.** Since I2b, anything stored through `IKeyValueStore` / `ICache`
is already partitioned once `IsolatePersistence()` is on. The gaps that remain are at **runtime seams**, and
§8.1 already puts the implicit behaviour there.

| Library | Already isolated by I2b? | Real gap | Generator change? |
|---|---|---|---|
| **YFex.Cqrs** (query cache) | ✅ yes, wholesale, if `IsolatePersistence(o => o.IsolateCache = true)`: `LocalDispatcher`/`FusionMessageBus` write through the decorated `ICache` (tags partitioned too) | `ICacheScopeContext` has **no implementation anywhere**, so `ScopedByUser()`/`ScopedByTenant(..)`/`ScopedBySession()` quietly produce **global** keys (`QueryCacheKey.ResolveScope` → null) | **No.** Cache scope is runtime fluent config (`QueryConfigurationBuilder`); `CQRSGenerator` only reads `ICacheable` |
| **YFex.Cqrs** (compensation journal) | ✅ `StorageBackedCommandJournal` is on `IKeyValueStore` | none | No |
| **YFex.Messaging.Rpc** (outbox, failure log) | ✅ `StorageBackedOutbox` / `StorageBackedSyncFailureLog` are on `IKeyValueStore` | the replayer drains only the **current** partition (A's queued commands wait until A is current again; that is the intended semantics, since they must replay under A's auth). `StorageBackedOutbox._pendingCount` is cached with no scope awareness, so the count is stale after a scope switch | No |
| **YFex.EventSourcing** (in-house) | ✅ `KeyValueEventStore` (`es:`/`es-head:`/`es-snap:`), `KeyValueEventInbox`, `GlobalPositionSequence` are all on `IKeyValueStore`; `DefaultEventBus` delivers synchronously, so projections fold under the publisher's scope | none for correctness. `GlobalPositionSequence` becomes per partition (acceptable: positions order within a tenant) | **No.** `[AggregateCommandHandler]` → ordinary `ICommandHandler` (CustomLogic hook free); `[Projection]` subscriptions inherit scope |
| **YFex.EventSourcing.Marten** (server) | ❌ `MartenAggregateSession(IDocumentSession)`, session from Marten's default factory, **no tenant id** | open the Marten session for the effective tenant (Marten conjoined / per-database tenancy) | No |
| **YFex.Jobs** | ❌ not applicable: jobs run on the scheduler's background loop with **no ambient scope** | Jobs has its **own flat tenant** (`[Job(TenantId=…)]`, `[MultiJobs(TenantId=…)]` → `JobDescriptor.TenantId` → `ScheduleRow`/`ClaimedJob`/`JobContext.TenantId`), but the dispatcher never enters it, so a tenant job's storage/Data/Cqrs calls run **unscoped** (shared partition, or throws under `FailClosed`) | **No** for the basic bridge. Optional later: per-tenant fan-out (§11.3) |

**`IStreamPartitioner` (OD2) is superseded for the in-house store.** Prefixing `StreamId` would double-partition
what the scoped `IKeyValueStore` already partitions. OD2 ("optional, off by default") still holds: the switch is
`IsolatePersistence()`. The only ES bridge left is Marten.

### 11.1 Cqrs — `YFex.Cqrs.Tenancy` (I2c, part 1) — ✅ DONE (2026-09-28)

`YFex.Cqrs/src/YFex.Cqrs.Tenancy` (`TenancyCacheScopeContext`, `QueryCacheScopeOptions`, `t.IsolateQueryCache(..)`) and
`YFex.Cqrs/tests/YFex.Cqrs.Tenancy.Tests` 11/11, both in `yfex.slnx` `/Cqrs/`. YFex.Cqrs gained only a lambda-free
`ScopedByTenant()`, which reads `ICacheScopeContext.TenantId` (the `CacheScope.Tenant` branch of `QueryCacheKey` was
unreachable before). Compensation 17 and SourceGenerator 6 still green.

The tenancy-backed `ICacheScopeContext` from §8.4. Cqrs keeps its seam and gains no tenancy reference.

```csharp
services.AddYFexTenancy(t => t
    .IsolateQueryCache(q => q.Tenant(MyScope.Tenant).User(MyScope.User)));
```

- `TenantId` = the scope chain **up to** the tenant level, as an opaque partition segment (not globally unique
  tenant values stay unique). `null` when the identity has no rung at or below that level.
- `User` = the inner `ICacheScopeContext`'s principal if the host registered one (web), else a synthesized
  principal named by the chain up to the user level (unauthenticated when absent).
- `SessionId` = the inner context's, else null.
- Decorates an existing `ICacheScopeContext` (keeps User/Session from it), otherwise registers one.
- Use it with **or without** persistence cache isolation. With it, the bridge adds per-query granularity when the
  cache window is coarser (e.g. `CachePartition = UpTo(Tenant)` but a query is `ScopedByUser()`). Without it,
  it is the lighter option: only queries that ask for a scope get one.

**Follow-up in Messaging.Rpc (not tenancy-specific):** a query configured `ScopedByUser/Tenant/Session`
whose scope resolves to null is cached under the **global** key. It should not be cached, or it should use a
distinct `anon` segment. Also reset `_pendingCount` in `StorageBackedOutbox` when the scope changes, or stop caching it.

### 11.2 EventSourcing — `YFex.EventSourcing.Marten` tenant sessions (I2d)

- In-house/client: nothing to build. Add one ES test proving `IsolatePersistence()` keeps two tenants'
  `order-1042` apart (append, read, and project).
- Server: register a tenancy-aware Marten `ISessionFactory` (or open `store.LightweightSession(tenantId)` in the
  `IAggregateSession` factory) that takes the tenant from `EffectiveIdentity()` at the configured level. It fails
  closed when the store is multi-tenanted and no tenant is in scope. It lives in an opt-in
  `YFex.EventSourcing.Marten.Tenancy` (or an optional method on the Marten package that takes a `YFex.Tenancy`
  reference). With Wolverine, map the envelope's `TenantId` into `ScopeOverride` so handlers and the session agree.
  Can't be built offline: the Marten project is excluded from `eventsourcing.slnx`.

### 11.3 Jobs — uses YFex.Tenancy directly (I2e) — ✅ DONE (2026-09-28)

**Decision (user, 2026-09-28):** no seam and no bridge. Jobs uses the tenancy library itself and drops its own
tenant concept to avoid duplication. Breaking changes are accepted.

- **Model.** `JobDescriptor` / `ScheduleRow` / `ClaimedJob` / `JobView` / `JobContext` / `JobExecutionRequest` /
  `JobObsContext` carry `ScopeIdentity Scope`. `TenantId` is gone from Product A.
  `JobSelector.Scope` (`ScopeIdentity?`) matches **at or beneath** (`ScopeIdentity.IsWithin`).
- **Authoring.** `[ScopedTo<TScope>(level, value)]`, a new **shared YFex.Tenancy attribute**, repeatable, on the
  method or class. The method's rungs win per level and the class fills the rest. It replaces
  `[Job(TenantId=…)]` / `[MultiJobs(TenantId=…)]`.
- **Generator.** Matches `YFex.Tenancy.ScopedToAttribute`1` by metadata name and emits
  `Scope = ScopeIdentity.Of(new ScopeValue(rank, "value"), …)`. New **YJOB006** covers a duplicate level on one
  symbol, a null value, or a non-byte level. The generator no longer references `YFex.Jobs.Abstractions` (it only
  ever used metadata-name strings), which freed Abstractions from netstandard2.0.
- **Runtime.** `JobDispatcher` wraps **only the job body** in `ScopeOverride.Enter(claimed.Scope)`. Store and audit
  calls stay unpartitioned, because they are the scheduler's own state. A global job runs explicitly unscoped, so no
  ambient tenant leaks in, unless `SchedulerOptions.UnscopedJobsInheritAmbientScope = true` (a device app's
  signed-in user).
- **Persistence.** EF stores the `Scope` column as `ScopeIdentity.ToString()` (`0=acme/1=store7`, max 512,
  indexed). The selector is `Scope == x || Scope.StartsWith(x + "/")`. **Schema change:** column
  `TenantId(128)` → `Scope(512)`.
- **Hub.** The frozen wire field `ProcessIdentity.TenantId` stays a string and now carries the canonical scope text.
  `EnrollmentOptions.TenantId` and the Worker's host identity are process-level Hub config and were left alone.
- **TFM (breaking).** YFex.Tenancy depends on the YFex core (net11.0), so Jobs Product A moved to **net11.0**:
  Abstractions (was netstandard2.0), core, EF, Hub.Client, Cli, tests and sample (were net8.0 / net8.0;net11.0).
  `Hub.Contracts` stays net8.0;net11.0.
- **Tenancy additions** (reusable, not Jobs-specific): `ScopeIdentity` got value equality, a canonical
  `ToString()`/`Parse`/`TryParse`, and `IsWithin`. `ScopeKeyPartitioner` now shares that formatter (`ScopeFormat`).
  The generic `ScopedToAttribute<TScope>` is new. Tenancy.Tests 21 (+9).
- **Tests.** Jobs.Tests 28 (+5: generator merge, scope entered and restored across an await, global job doesn't see
  the ambient scope, the opt-in inherit, the selector). Worker.Tests 20. Regressions green: Persistence.Tenancy 9,
  Cqrs.Tenancy 11, CustomLogic 8, NavigatR.Tenancy 6, Data.Tenancy 13, Capabilities 28+5, Web 77.
- **Still open:** per-tenant fan-out (schedule identity is one row per `JobKey`; the EF index `JobKey` is unique);
  ad-hoc enqueue capturing the caller's scope; the dispatcher's `EmptyProvider` service provider; a generator test
  for YJOB006.

### 11.4 Where source generation *would* add value (all optional, none blocking)

Per §8.1 (generators emit against seams, detect by metadata name, carry policy + diagnostics):

- **Jobs:** ✅ `[ScopedTo<TScope>]` → descriptor `Scope` (+YJOB006). Later: a `PerTenant` flag, once schedule identity
  is `(key, scope)`.
- **Cqrs analyzer:** a warning when `ScopedByUser()`/`ScopedByTenant()` is configured in an assembly where no
  `ICacheScopeContext` implementation is visible. This is advisory only: DI is runtime, so it can't be proven.
- **EventSourcing:** none.

### 11.5 Phasing update

- **I2c** = §11.1 Cqrs bridge + the two Messaging.Rpc follow-ups.
- **I2d** = §11.2 (ES test + Marten tenant sessions), saga store, consent.
- **I2e** = §11.3 — ✅ done (Jobs on YFex.Tenancy directly). Fan-out/ad-hoc enqueue later.

---

## 12. Ecosystem sweep — every library under `C:\Dev\YFex` (2026-09-28)

A scan of every non-test project for owned storage, Persistence-backed storage, background loops (which lose the
ambient scope), existing tenant notions and generators, followed by reading the flagged code.

**The one rule that explains most findings.** Since I2b, anything written through `IKeyValueStore` / `ISyncLocalStore`
/ `ICache` is partitioned by the scope **of the flow that writes it**. That is right for request/UI flows. It breaks for
work that is **written in one flow and drained by a background loop**: the loop has no scope, so it reads the
shared partition and never sees tenant entries. The fix pattern is always the same: **capture
`EffectiveIdentity().ToString()` with the item, keep the queue itself in shared keys, and `ScopeOverride.Enter(Parse(..))`
when processing it.** Jobs now does this natively.

### 12.1 YFex.Persistence — ✅ OK, with one gap

Covered by `IsolatePersistence()`: `IKeyValueStore`, `ISyncLocalStore` and `ICache` (`IsolateCache` defaults to true),
and everything built on them. That includes snapshots (`PersistenceService` / generated `CaptureSnapshot`), the
`KeyValueStoreDistributedCache` adapter, `FusionCacheAdapter`, and `EncryptedKeyValueStore` (either decorator order
works). The Persistence generator emits no storage code, so there is nothing to change there.

- ⚠ **`IDurableQueue<T>` is not decorated** (`SqliteDurableQueue`, one table per queue). A FIFO drained in the
  background can't be partitioned by the reader's scope. It needs the capture/restore pattern (the item carries its
  scope, or one queue per scope). Current users: Profiling telemetry (device-level, acceptable) and nothing
  tenant-sensitive yet.
- Ordering gotcha: `IsolatePersistence()` decorates the **last** registration. A library that `TryAdd`s its own store
  later (e.g. Profiling registers `FileSystemLocalStore` as `ISyncLocalStore`) must be registered **before**
  `IsolatePersistence()`, or it bypasses isolation.
- Optional: per-tenant encryption keys (`IKeyProvider` keyed by scope) for crypto isolation on shared devices.

### 12.2 Findings by severity

| # | Library | Finding | Severity | Fix |
|---|---|---|---|---|
| 1 | **Messaging.Rpc** `EventChannelHost<TEvent>` | `GetEventsAsync(EventFilter filter, …)` **ignores the filter**: every subscriber gets every event. `EventFilter(TenantId, UserId)` is also **client-supplied** (untrusted), and a Fusion `[ComputeMethod]` result is cached by its arguments, so a server-side filter that isn't an argument would be shared across callers. | **High** (cross-tenant push leak on multi-tenant servers) | Stamp each event with the publisher's `EffectiveIdentity()` at `Append`. Filter by the **server-side** caller scope (`IsWithin`), and make that scope part of the compute-method key (or bypass caching). Drop the tenant from the client `EventFilter`. |
| 2 | **Orchestration** saga timeouts | `KeyValueDelayScheduler` (`sched:`/`sched-idx:`) and `KeyValueSagaStore` (`saga:`) are on `IKeyValueStore`. `SchedulerHostedService` drains **unscoped**, so under `IsolatePersistence()` **a tenant's timeouts never fire**. The index key `sched-idx:{correlation}:{name}` would also collide across tenants in shared keys. | **High** (silent functional failure once isolation is on) | `ScheduleEntry.Scope` (canonical text) captured at `ScheduleAsync`. Scheduler keys go in `SharedKeyPrefixes`, and the index key includes the scope. `ClaimDueAsync` delivers inside `ScopeOverride.Enter(Parse(entry.Scope))` so the saga lookup hits the tenant partition. Orchestration takes a `YFex.Tenancy` core reference (allowed by §10). Saga steps and process managers need nothing: they run in the publisher's or dispatcher's flow. |
| 3 | **YFex.Web.Refit** (`EndpointAuthHandler`, `HeadersHandler`, `FormFieldsHandler`, `OAuthTokenSource`) | These read `ICurrentTenantProvider.Current` (the web-resolved `ITenantInfo`), **not** the scope. A Job running as `acme`, a `ScopeOverride` block, or any non-request flow calls downstream APIs with no tenant (or the request's), so it uses the wrong credentials, headers and token cache. A second tenant source again. | **High** for server-side jobs calling tenant APIs | Make the registered `ICurrentTenantProvider` scope-aware: when an override is active, resolve `ITenantInfo` for the tenant rung through `ITenantStore` (cached; the provider is synchronous). One source of truth: `EffectiveIdentity()`. |
| 4 | **YFex.Authorization** | `IPermissionProvider.PermissionsFor(ClaimsPrincipal)` has no scope, so it can't express "admin in tenant A, viewer in B" except through ad-hoc claims. Token caches (`InMemoryTokenStore`, Web's `FusionCacheTokenStore` on `IFusionCache` directly) are keyed by authority, scope and subject only. | Medium (design decision) | Decide whether permissions are per scope. If so, add a scope-aware provider overload fed by `EffectiveIdentity()`. Token keys are safe only while subjects are tenant-unique; otherwise include the scope. (There are also two `ITokenStore`s, in Authorization and in Web: duplication, not tenancy.) |
| 5 | **Messaging.Rpc** cache/outbox | §11.1 follow-ups: a scoped query with a null scope gets the global key; `_pendingCount` is stale after a scope switch. | Low | as in §11.1 |
| 6 | **YFex.Resilience** | Circuit breakers, rate limiters and bulkheads are process-wide with no partition key: one tenant's failing dependency trips the breaker for everyone (noisy neighbour). | Low/Med (servers) | Optional partition key from the scope for limiter/breaker instances. |
| 7 | **EventSourcing.Marten** | §11.2: sessions opened without a tenant. | Med (server) | §11.2 |

### 12.3 Already correct or not applicable

- ✅ **Cqrs** (§11.1 bridge), **EventSourcing in-house** (§11), **Profiling consent** (`FileConsentStore` over
  `ISyncLocalStore`, so per scope; mind the registration order in 12.1), **Search** (`CachingSearchOrchestrator` uses
  the DI `ICache`, and `KeyValueFrecencyStore` uses `IKeyValueStore`, both partitioned), **State.History.Persistence**
  (`UndoSnapshotProvider` → snapshots → `IKeyValueStore`), **Capabilities / CustomLogic / NavigatR / Avalonia /
  Data / Web** (already integrated).
- ➖ **No tenancy relevance:** YFex core, Messaging (in-process bus, synchronous, so scope flows), State, Mvvm/UI,
  Xml, System, Security, Devices (machine-level; if it ever uses Persistence, put its keys in `SharedKeyPrefixes`),
  Fiscal/TaxEngine (static reference data), Farma/Finance/Marketplace/Domain (contracts), Brain (`SourceRef.Tenant` is
  the **source system's** tenant in an ingestion model, not ours), DataMining/Csv/Excel (ERP migration tooling),
  LoggY (a log-viewer app), and the tooling repos (Axamly, RoselYn.MCP, Act.Avalonia).

### 12.4 Reuse opportunities for `[ScopedTo<TScope>]` (generators)

The shared attribute now exists in YFex.Tenancy, so the deferred generator items can use it instead of inventing
their own:
- **Capabilities generator:** `[ScopedTo]` on a VM or profile → emit a `ScopedCapabilitySource`. This replaces the
  deferred `[CapabilityFor]` idea from §6/I1.
- **NavigatR generator / Avalonia ViewLocator:** `[ScopedTo]` on a VM or View → emit `RegisterFor<…>(level, value)`.
- **CustomLogic:** `[CustomLogic<TScope>(level, value)]` overlaps. It could become `[CustomLogic] [ScopedTo<…>]`, but
  it is also the discovery marker (with `Additive`), so it is left as is unless you want the consolidation.


### 12.5 Fix log (2026-09-28) — the high and medium findings in §12.2

**High**
1. ✅ **Rpc `EventChannelHost`.** Each event is stamped with an *audience*: the publisher's `EffectiveIdentity()` inside
   `EventChannelOptions.Audience`, or the global `ScopePartitionOptions` if that isn't set. Subscribers pass a
   **server-issued, HMAC-signed** `EventFilter(AudienceToken)` from the new non-compute
   `GetAudienceTokenAsync()`. A subscriber sees global events plus events whose audience its scope is within. The
   token is unforgeable, so a Fusion-cached result keyed by it is reachable only by callers the server issued it to.
   `EventFilter(TenantId, UserId)` is removed (it was client-trusted and ignored). Also fixed an existing invalidation
   bug: `Append` now invalidates a shared `GetHeadAsync()` dependency, so subscribers at every cursor are notified
   (before, only the `(empty filter, 0)` result was). Messaging.Rpc references `YFex.Tenancy` core. +4 tests.
   *Not covered:* a live Fusion proxy / WebSocket end-to-end test. The push feature is also still unwired: the
   `EnableServerPushedEvents()` flags are never read.
2. ✅ **Orchestration timeouts.** `ScheduleEntry.Scope` (appended last, MemoryPack-tolerant) records the scheduling flow's
   scope. The scheduler's keys always live in the shared area (`ScopeOverride.EnterUnscoped()`, so no
   `SharedKeyPrefixes` config is needed). The cancel index is keyed by scope (`sched-idx@{len}:{scope}{corr}:{name}`;
   unscoped entries keep the old format). `ClaimDueAsync` delivers inside `ScopeOverride.Enter(entry scope)`.
   Orchestration references `YFex.Tenancy`. +3 tests (fail-closed isolation, two tenants with the same correlation,
   cancel isolation, keys unchanged without tenancy). The AoT smoke passes.
   **Tenancy fix found on the way:** under `FailClosed()` an explicit `ScopeOverride.EnterUnscoped()` threw, despite
   the error message recommending it. `ScopeKeyPartitioner.CurrentPrefix()` now honors an explicit unscoped override
   (fail-closed guards a *missing* scope, not an intentional one).
3. ✅ **Web.Refit tenant source.** New `ScopedTenantProvider<TTenant>` (Tenancy.Resolution) is the registered
   `ICurrentTenantProvider` in both `AddMultiTenant` and `AddYFexWebTenant`. With no override it returns the host's
   tenant (unchanged). Inside a `ScopeOverride` it returns the override's tenant: `ITenantScopeProjector.TenantIdOf`
   (new) → `ITenantStore.TryGetByIdAsync` (new; `DapperTenantStore` gains an `idColumn`, default `"Id"`), cached for
   5 minutes, misses included. `OAuthTokenSource` now falls back to `ICurrentTenantProvider` instead of
   `ITenantContextAccessor<ITenantInfo>`, which never resolved for app tenant types. `DapperApiEndpointStore` picks it up
   automatically. **Breaking:** `ITenantStore` / `ITenantScopeProjector` implementers must add the new members.
   +5 tests.

**Medium**
4. ✅ **Authorization.** `IScopedPermissionProvider` (in YFex.Authorization, so `.Public` and therefore Cqrs stay
   tenancy-free). `DefaultPermissionProvider` honors `scoped_role` / `scoped_permission` claims
   `"{scope}|{grant}"`, applied when the scope in effect is within the grant's scope. `AuthContextFactory` passes
   `EffectiveIdentity()`. Token caches need no change: the only `TokenCacheKey` producer (Web `OAuthTokenSource`) keys
   by tenant Id, which now follows the scope (item 3). +4 tests.
5. ✅ **EventSourcing.Marten.** `UseMarten(t => …)`: a tenant in scope → `store.LightweightSession(tenantId)`, enrolled in
   Wolverine's outbox (`IMartenOutbox.Enroll`, `TenantId` stamped). No tenant → the default session, or throws with
   `RequireTenant`. `DomainEventBridge` re-enters the envelope tenant's scope before publishing to `IEventBus`, because
   outbox delivery is a background path. The tenant id is the shared `ITenantScopeProjector` mapping.
   **The project had never compiled against the current core:** it was missing
   `SaveChangesAsync(bool, CancellationToken)`, and `CurrentVersion` is `long?` in Marten 7.37. There was also a
   restore conflict (WolverineFx 3.x / Oakton cap `Microsoft.Extensions.Hosting` below 10, while ActualLab needs 10
   or higher), now pinned to Hosting 10 with NU1608 warnings. It **builds now** (packages restored from nuget.org) but
   is **not run-tested** (needs Postgres). The proper fix is WolverineFx 4 / Marten 8.
6. ✅ **Resilience.** `IResourceBuilder.PerScope()` / `ResourcePolicies.PerScope` (core stays tenancy-free).
   `YFex.Resilience.Polly` keeps one breaker/limiter/bulkhead state per scope partition (effective identity inside
   the registered `ScopePartitionOptions`, or the whole identity), selected per execution. +3 tests. The AoT smoke
   passes.

**Tenancy core additions this round:** `ScopePartitionOptions.Apply(identity)` (window → identity), and the
fail-closed fix above.
**Still open:** §11.1 Low items (Rpc null-scope cache key, stale `_pendingCount`); `IDurableQueue<T>` partitioning;
Jobs per-tenant fan-out; the Wolverine 4 / Marten 8 upgrade.
