# CustomLogic / Tenancy — Cross-Project Integration Evaluation & Plan

**Status:** Evaluation + plan (no code yet); §7 decisions settled 2026-09-24. Companion to
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
| **YFex.EventSourcing** (`StreamId`) | B | **High** | `StreamId` is `"order-1042"` — **no tenant partition**; cross-tenant stream collision. Needs scope prefix |
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
- **I1 — Capabilities scope dimension (§2).** Highest-value A after commands; self-hosted overlay.
- **I2 — Data isolation (Kind B, §5).** Persistence + Messaging.Rpc offline partitioning first
  (correctness/leak), then ES streams, CQRS cache, saga store, consent.
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
