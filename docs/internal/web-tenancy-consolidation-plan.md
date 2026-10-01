# YFex.Web ↔ YFex.Tenancy — Tenancy Consolidation Plan

**Status:** Plan only — no code (2026-09-24). Supersedes §3 of
[custom-logic-integration-plan.md](custom-logic-integration-plan.md) (the conservative "keep two
models + one adapter" recommendation). That section is now **withdrawn**: instead of bridging two
parallel tenancy stacks, we **collapse them into one**. The host-neutral half of `YFex.Web`'s
tenancy moves down into the `YFex.Tenancy` layer; `YFex.Web` keeps only its ASP.NET-specific
adapters. Companion to [custom-logic-design.md](custom-logic-design.md) (which built `YFex.Tenancy`
+ `YFex.CustomLogic`).

> `YFex.Web` is a **separate solution** (`C:\Dev\YFex\YFex.Web\YFex.Web.slnx`), a sibling repo of
> the `YFex` repo. Breaking changes are **acceptable** — `YFex.Web` is under active development and
> we favor the clean end-state over back-compat. Both repos currently target `net11.0`.

---

## 0. Requirement (2026-09-24): the scope model must be GENERIC — no baked-in hierarchy

**Problem.** The shipped core hardcodes **one** hierarchy — `Scope { Organization, Tenant, User }`,
`ScopeIdentity(OrganizationId, TenantId, UserId)`, and this plan's proposed `IOrganizationScoped`.
That is *one company's* chosen structure. As a shared framework consumed by very different projects
(`Region → Store → Terminal`; `Company → Department → Team → User`; a single tenant level; …), the
library must **not name or fix the levels**. `IOrganizationScoped` especially bakes a domain concept
into a generic library and is **removed**.

**Principle.** The library owns the *mechanism* — an ordered set of scope **levels** resolved
most-specific-first — never the *levels themselves*. The consuming project defines its levels.

**Generic model (revises the core `YFex.Tenancy` shape — see the design-doc note below):**

- Replace the `Scope` enum with an **opaque `byte` rank** (higher = more specific). The library never
  names ranks: `ScopeKey(byte Rank, int ValueId)`, `ScopeValue(byte Rank, string Value)`.
- `ScopeIdentity` becomes an **ordered set of rungs** (up to a `MaxScopeLevels`, e.g. 8), not three
  named fields.
- The app declares its levels with **its own enum** (`enum MyScope : byte { Company, Branch, Operator }`)
  and annotates with a **generic attribute** `[CustomLogic<MyScope>(MyScope.Company, "acme")]` (C# 11
  generic attributes; the generator reads the underlying byte). The framework sees only ranks/values.
- **`IOrganizationScoped` is removed.** The tenant → scope projection is **app-specific**: the app
  supplies an `ITenantScopeProjector` (or a delegate) mapping its resolved `ITenantInfo` + principal
  to scope rungs. The library provides the plumbing; the app owns the mapping.

**Cross-library impact:** `YFex.Tenancy` (core types), `YFex.CustomLogic` (attribute + generator +
key), NavigatR/Avalonia overlays (walk rungs generically), and this plan's Web projection
(app-supplied). **This supersedes every `Organization/Tenant/User` literal and `IOrganizationScoped`
reference in the sections below** — where they appear, read them as *example* levels a particular app
might choose, not framework types. It also revises `custom-logic-design.md` §2 (the `Scope` enum),
which must be updated in lockstep.

---

## 1. Goal & principles

Today there are **two tenancy models** that do not know about each other:

| | `YFex.Web` model | `YFex.Tenancy` model |
|---|---|---|
| Core type | `ITenantInfo` / `TenantContext<TTenant>` | `ScopeIdentity` / `IScopeContext` |
| Shape | A **resolved tenant entity** (Id/Identifier/Name), generic over `TTenant : ITenantInfo`, with a store + resolution strategies | A lightweight **Org/Tenant/User keying identity** for customization + data isolation |
| Concern | *Discovery* — "who is the tenant, and where do I load it from?" | *Selection / isolation* — "what is the current scope chain?" |
| Consumers | Web request pipeline, Refit auth, Dapper stores | CQRS override hook, NavigatR routes, Capabilities, Avalonia grains |

Both answer "who is the current tenant" — from opposite ends. The consolidation makes them **two
faces of one resolved state**.

**Principles (in priority order):**

1. **One model, one resolution.** A single pipeline yields **both** the rich `ITenantInfo` /
   `TenantContext` **and** the `ScopeIdentity` / `IScopeContext` chain. No second, parallel
   resolution of "the current tenant".
2. **Host-neutral core, Web = adapters only.** Everything that does not touch `HttpContext`,
   Dapper, or ASP.NET moves into the `YFex.Tenancy` layer (the `YFex` repo). `YFex.Web` retains
   only what reads an `HttpRequest` / `HttpContext.Items` / `ClaimsPrincipal`.
3. **AoT-first.** The moved contracts and default resolver stay `<IsAotCompatible>true</IsAotCompatible>`
   (no reflection, no `Expression.Compile`) — they already are in both repos today.
4. **Dependency direction only ever points down.** `YFex.Web → YFex.Tenancy(.Resolution) → YFex`.
   The scope layer never learns about the web.
5. **Additive to `YFex.Tenancy`'s hot path.** The blittable scope core (`Scope`/`ScopeKey`/
   `ScopeIdentity`/`IScopeContext`/`ScopeValueInterner`) is untouched; the generic resolution
   machinery is a *separate* concern that must not bloat it (see §3, project split).

---

## 2. Precise inventory — MOVE / STAY / MERGE / DELETE

Destination shorthand: **`Res`** = the moved-down resolution project (name decided in §3, OD-C);
**`Tcy`** = existing `YFex.Tenancy` scope core; **`Web.AspNetCore`** / **`Web.Data`** = stay put.

### 2.1 `YFex.Web/Tenant/` (core contracts — all host-neutral)

| Type | Verdict | Destination | Notes |
|---|---|---|---|
| `ITenantInfo` | **MOVE** | `Res` | The tenant-entity contract (Id/Identifier/Name). Namespace `YFex.Web.Tenant` → `YFex.Tenancy.Resolution`. |
| `TenantContext<TTenant>` | **MOVE** | `Res` | Immutable resolved snapshot. Generic over `TTenant : class, ITenantInfo`. |
| `ITenantContextAccessor<TTenant>` | **MOVE** | `Res` | Ambient get/set of the context. |
| `ICurrentTenantProvider` | **MOVE + MERGE** | `Res` | Non-generic face of "current tenant". Becomes one face of the same resolved state as `IScopeContext.Current` (§4). Kept as a distinct interface (Refit/host-neutral code depends on it) but relocated. |
| `ITenantResolver<TTenant>` | **MOVE** | `Res` | Orchestrates strategies → store → `TenantContext`. |
| `ITenantResolveStrategy` | **MOVE** | `Res` | `object context` stays intentionally untyped so the core is host-neutral; ASP.NET strategies narrow it. |
| `ITenantStore<TTenant>` | **MOVE** | `Res` | Storage contract. Dapper impl stays in `Web.Data`. |
| `TenantResolveOptions` | **MOVE** | `Res` | Strategy list + ignored identifiers. |

### 2.2 `YFex.Web.Data/Tenant/` (host-neutral impls — no Dapper dependency)

| Type | Verdict | Destination | Notes |
|---|---|---|---|
| `DefaultTenantResolver<TTenant>` | **MOVE** | `Res` | Pure logic over the contracts; no Dapper/HTTP. Belongs with the abstractions it implements. |
| `AsyncLocalTenantContextAccessor<TTenant>` | **MOVE + MERGE-candidate** | `Res` | `AsyncLocal`-backed accessor; also implements `ICurrentTenantProvider`. Near-duplicate of `Tcy.AsyncLocalScopeContext` — see §4.3 (unify or keep both, OD-F). |
| `DelegateTenantStrategy` | **MOVE** | `Res` | Host-neutral strategy. |
| `StaticTenantStrategy` | **MOVE** | `Res` | Host-neutral strategy. |

### 2.3 `YFex.Web.Data/Storage/`

| Type | Verdict | Destination | Notes |
|---|---|---|---|
| `DapperTenantStore<TTenant,TRow>` | **STAY** | `Web.Data` | Hard Dapper + `YFexConnectionFactory` dependency. Re-targets its `using YFex.Web.Tenant;` to `using YFex.Tenancy.Resolution;`. |
| `ServiceCollectionExtensions.AddMultiTenant<TTenant,TRow>` | **STAY + reconcile** | `Web.Data` | Wires the Dapper store + AsyncLocal accessor + default resolver. Reconciled with `AddYFexTenancy` so there is one wiring story (§5.3). |

### 2.4 `YFex.Web.AspNetCore/Tenant/` (genuine web adapters — all STAY)

| Type | Verdict | Destination | Notes |
|---|---|---|---|
| `TenantResolutionMiddleware<TTenant>` | **STAY** | `Web.AspNetCore` | `IMiddleware` over `HttpContext`. Gains one responsibility: after resolving, also publish the `ScopeIdentity` (§4.2). |
| `HttpContextTenantContextAccessor<TTenant>` | **STAY** | `Web.AspNetCore` | Backed by `HttpContext.Items`. |
| `HostTenantStrategy` | **STAY** | `Web.AspNetCore` | Reads `HttpRequest.Host`. |
| `ClaimTenantStrategy` | **STAY** | `Web.AspNetCore` | Reads `ClaimsPrincipal`. |
| `HeaderTenantStrategy` | **STAY** | `Web.AspNetCore` | Reads `HttpRequest.Headers`. |
| `YFexWebTenantServiceExtensions.AddYFexWebTenant<TTenant>` | **STAY + extend** | `Web.AspNetCore` | Also registers the `HttpScopeContext` adapter as `IScopeContext` (§5.3). |

### 2.5 New types introduced by this plan

| Type | Verdict | Destination | Notes |
|---|---|---|---|
| `ITenantScopeProjector` (app-supplied) | **NEW** | `Res` (contract) / app (impl) | Replaces `IOrganizationScoped` (§0). App-defined mapping `(ITenantInfo?, string? userId) → ScopeIdentity rungs`. The library ships the *contract* + a no-op/one-level default; the app provides the real projection (which levels, from which tenant fields). Keeps ALL hierarchy naming out of the library. |
| `HttpScopeContext : IScopeContext` | **NEW** | `Web.AspNetCore` | The single web adapter that runs the app's `ITenantScopeProjector` over the resolved tenant + principal and publishes the resulting rungs (§4). |

**Nothing is DELETEd outright.** The only "deletions" are the *files* leaving `YFex.Web` /
`YFex.Web.Data` as they relocate — the types survive under new namespaces. The one merge worth
calling out (OD-F) is whether `AsyncLocalTenantContextAccessor<T>` and `AsyncLocalScopeContext`
collapse into a single ambient holder.

---

## 3. Target layering & project layout

### 3.1 The split decision — `YFex.Tenancy` vs a new `YFex.Tenancy.Resolution`

The moved types are **not** the hot path. `ITenantInfo`/`TenantContext<T>`/`ITenantResolver<T>` are
generic-over-`TTenant`, `Task`-returning, DB-backed **discovery** machinery. The existing
`YFex.Tenancy` core is deliberately blittable, allocation-free, and consumed on the CQRS dispatch
hot loop by CQRS/Capabilities that want **only** `IScopeContext`.

> **Location update (2026-09-24):** `YFex.Tenancy` was extracted from the `YFex` repo into its own
> sibling solution at `C:\Dev\YFex\YFex.Tenancy\` (`tenancy.slnx`, project under `src/`). So the new
> `YFex.Tenancy.Resolution` project lands **beside it** in that sibling —
> `C:\Dev\YFex\YFex.Tenancy\src\YFex.Tenancy.Resolution\` — and is added to `tenancy.slnx` (and
> optionally referenced from `yfex.slnx`'s `/Tenancy/` folder), **not** to an in-`YFex`-repo
> `src/Tenancy/` path. All paths below are updated for this.

**Recommendation: a new sibling project `YFex.Tenancy.Resolution`** rather than dropping the
resolver into `YFex.Tenancy`. Rationale:

- Keeps `YFex.Tenancy` minimal — CQRS/NavigatR/Capabilities keep referencing just the scope core
  and never pull in the generic tenant-entity/resolver surface they don't use.
- Separates concerns cleanly: `YFex.Tenancy` = *identity/scope chain*; `YFex.Tenancy.Resolution` =
  *tenant discovery that produces that identity*.
- `YFex.Tenancy.Resolution → YFex.Tenancy` is a legal L1→L1/L2 edge; it adds the `ScopeIdentity`
  projection (§4) in exactly the layer that owns both halves.

(If the team prefers fewer projects, everything can land in `YFex.Tenancy` directly — the type set
and namespaces are identical; only the assembly boundary differs. Tracked as **OD-C**.)

### 3.2 Layering (bottom → top)

```
L0  YFex                          core primitives
L1  YFex.Tenancy                  Scope, ScopeKey, ScopeValue, ScopeIdentity, IScopeContext,
                                  AsyncLocalScopeContext, MutableScopeContext, ScopeValueInterner,
                                  ScopeChain.  refs: YFex + M.E.DI.Abstractions.  AoT. (UNCHANGED)
L1/L2  YFex.Tenancy.Resolution    ITenantInfo, IOrganizationScoped, TenantContext<T>,
        (NEW)                     ITenantContextAccessor<T>, AsyncLocalTenantContextAccessor<T>,
                                  ICurrentTenantProvider, ITenantResolver<T>, ITenantResolveStrategy,
                                  ITenantStore<T>, TenantResolveOptions, DefaultTenantResolver<T>,
                                  DelegateTenantStrategy, StaticTenantStrategy, TenantScopeProjector.
                                  refs: YFex.Tenancy + M.E.DI.Abstractions.  AoT.
──────  solution boundary: YFex.Tenancy sibling (C:\Dev\YFex\YFex.Tenancy\tenancy.slnx) ↑ / YFex.Web repo ↓  ──────
     YFex.Web                     (unchanged role: Result reuse etc.)  newly refs YFex.Tenancy.Resolution
     YFex.Web.Data                DapperTenantStore, AddMultiTenant.  refs YFex.Tenancy.Resolution
     YFex.Web.AspNetCore          middleware, HttpContext accessor, host/claim/header strategies,
                                  HttpScopeContext.  refs YFex.Tenancy.Resolution + YFex.Tenancy
```

### 3.3 Dependency-direction block

```
YFex.Web.AspNetCore ─► YFex.Tenancy.Resolution ─► YFex.Tenancy ─► YFex
YFex.Web.AspNetCore ─► YFex.Tenancy            (for IScopeContext directly, HttpScopeContext)
YFex.Web.Data       ─► YFex.Tenancy.Resolution
YFex.Web            ─► YFex.Tenancy.Resolution

# unchanged, and still legal:
YFex.Cqrs / NavigatR / Capabilities ─► YFex.Tenancy   (scope core only; never .Resolution)
```

No cycles. `YFex.Tenancy.Resolution` never references anything web/HTTP/Dapper.

### 3.4 Cross-repo ProjectReference paths

`YFex.Web` already crosses the boundary with the established `..\..\..\..\YFex\src\...` convention
(it references `YFex.Cqrs` and `YFex.Authorization.Public` today). The new references follow the
same shape:

```xml
<!-- from YFex.Web.AspNetCore/.Data/.Web  (YFex.Tenancy is now its own sibling repo) -->
<ProjectReference Include="..\..\..\..\YFex.Tenancy\src\YFex.Tenancy.Resolution\YFex.Tenancy.Resolution.csproj" />
<!-- YFex.Web.AspNetCore additionally: -->
<ProjectReference Include="..\..\..\..\YFex.Tenancy\src\YFex.Tenancy\YFex.Tenancy.csproj" />
```

`YFex.Tenancy.Resolution` is added to **`tenancy.slnx`** (the `YFex.Tenancy` sibling solution),
alongside `YFex.Tenancy`; `yfex.slnx` may also reference it under its `/Tenancy/` folder. `YFex.Web.slnx`
gains only the new cross-repo `ProjectReference`s (matching how `YFex.Cqrs` is already pulled in);
it does not add the projects to its own solution list — same as today.

---

## 4. The unified identity design

The invariant: **`ITenantInfo`/`TenantContext<T>` and `ScopeIdentity`/`IScopeContext` are two views
of one resolved state.** The `YFex.Web` resolution stack remains the sole authority for *how* a
tenant is discovered (strategies → store). From its output we *project* the `ScopeIdentity`; we do
not re-resolve.

### 4.1 The projection (`ITenantInfo` + principal → scope rungs) — APP-SUPPLIED (§0)

The library does **not** know which levels an app has, so it does not hardcode the mapping. It
defines the contract; the app implements it:

```csharp
// In Res (library) — no level names, no HttpContext/ClaimsPrincipal.
public interface ITenantScopeProjector
{
    // Returns the app's scope rungs (rank + value) for the resolved tenant + user.
    ScopeIdentity Project(ITenantInfo? tenant, string? userId);
}
```

An app that chose `Company → Branch → User` implements it to read *its* tenant fields into *its*
ranks; an app with a single tenant level emits one rung. The library ships only a **default
projector** that emits the tenant `Id` at rank 0 and the `userId` at the highest rank (a sensible
zero-config baseline), overridable. The example table below (`Tenant`/`Org`/`User`) is illustrative
of what one app's projector might produce — not framework types:

| Example rung | Example source | Rule |
|---|---|---|
| tenant level | `ICurrentTenantProvider.Current.Id` | `Id` (canonical key), not `Identifier` — see OD-E. |
| an org/parent level | an app-defined field on the app's own `ITenantInfo` implementation | The app's projector reads it; the library has no `IOrganizationScoped`. |
| user level | the authenticated principal's subject | Read from `ClaimsPrincipal` in the web adapter; passed to the projector as a plain `string?` so `Res` stays ASP.NET-free. |

The projector lives in `Res` as a contract (host-neutral: no `HttpContext`, no `ClaimsPrincipal`);
the concrete mapping is the app's, reused by every host (ASP.NET, Avalonia, background).

### 4.2 Where the single pipeline lives

`TenantResolutionMiddleware<TTenant>` already runs once per request and stashes the
`TenantContext`. It gains **one** extra step: after setting `_accessor.CurrentContext`, it computes
the `ScopeIdentity` (via `TenantScopeProjector` + the request principal) and publishes it to the
ambient `IScopeContext`. One resolution, both faces populated. No second lookup.

`HttpScopeContext : IScopeContext` is the read side: `Current` returns the projected `ScopeIdentity`
(either cached from the middleware write, or computed lazily from `ICurrentTenantProvider` +
`IHttpContextAccessor.HttpContext.User`), and `Resolve(Span<ScopeKey>)` fills the chain through the
interner exactly like `AsyncLocalScopeContext`/`MutableScopeContext` already do (via `ScopeChain.Fill`).

### 4.3 Reconciling the two ambient accessors

Both repos have an `AsyncLocal` ambient holder: `Res.AsyncLocalTenantContextAccessor<T>` (holds
`TenantContext<T>`) and `Tcy.AsyncLocalScopeContext` (holds `ScopeIdentity`). Two options (**OD-F**):

- **(Recommended, minimal) Keep both, one writer.** The middleware (web) / host startup (background)
  writes both. Non-web hosts that only need the scope chain use `AsyncLocalScopeContext` alone.
  Web hosts use `HttpContextTenantContextAccessor` (tenant) + `HttpScopeContext` (scope) over the
  same `HttpContext`. Simplest; zero behavior change to existing accessors.
- **(Aggressive) Single ambient.** Make `ScopeIdentity` the canonical ambient and derive
  `ICurrentTenantProvider.Current` from a resolved-tenant cache keyed by the ambient identity. More
  unification but couples the scope core to tenant-entity loading — rejected for L1 purity.

### 4.4 Extra levels are an app concern (no `IOrganizationScoped`)

Per §0, the library does **not** define an `IOrganizationScoped` (or any named level). An app that
needs a level above "tenant" (an organization, a region, a parent company) puts that field on **its
own** `ITenantInfo` implementation and reads it in **its own** `ITenantScopeProjector`. The library
never sees the concept. Apps with a single tenant level emit one rung; apps with a deep hierarchy
emit several — all through the same generic rung mechanism.

### 4.5 Cross-tenant admin / impersonation (integration-plan OD5 — DECIDED: must support)

A support/admin user must be able to operate **inside a tenant they don't belong to** — read that
tenant's data, exercise that tenant's customizations — while the system still knows *who is really
acting* for audit and authorization. The scope model already separates the rungs, which makes this
clean:

- **Tenant/Org rungs → the TARGET (impersonated) tenant.** So every downstream consumer that keys on
  the scope chain — Kind-B data isolation, Kind-A CustomLogic overrides, Capabilities — automatically
  operates against the target tenant with **no special-casing**. Impersonation is just "the scope
  chain points at another tenant."
- **User rung → the REAL admin subject** (never the impersonated tenant's user). Authorization and
  audit see the actual actor.

**Mechanism (web, server-validated — this is why it lives in this plan):**

1. The admin supplies a target tenant via an **explicit, admin-only channel** — a validated claim or
   a header (e.g. `X-Act-As-Tenant`) — **never** inferred from untrusted client state.
2. `TenantResolutionMiddleware` gates it: the acting principal must hold a dedicated permission
   (e.g. `Tenant.Impersonate`, checked via `YFex.Authorization`). Missing/denied → the header is
   ignored and normal resolution applies (the admin's own tenant, or none). No silent cross-tenant
   access.
3. When allowed, the middleware resolves the **target** `TenantContext` (via the same resolver/store)
   and projects the `ScopeIdentity` with **target** Tenant/Org rungs + **real-admin** User rung.
4. `TenantScopeProjector` gains an overload that takes an explicit acting-tenant override:
   `Project(ITenantInfo? actingTenant, string? realUserId) → ScopeIdentity`. The neutral projector
   stays ASP.NET-free; the *authorization gate + header reading* live in the web middleware.
5. **Audit is mandatory:** every impersonated request is logged as `(realUserId, targetTenantId)` so
   cross-tenant actions are traceable. (Impersonation must never be indistinguishable from a normal
   session in the audit trail.)

Optional refinement (deferred): a `ScopeIdentity.ImpersonatedBy` marker so a consumer can *detect*
impersonation (e.g. to show a banner or restrict destructive ops). Not required for correctness —
data isolation and customization already work purely off the projected rungs — so it is left out of
the core shape unless a consumer needs it.

Non-web hosts (a background admin job) express the same thing by setting the ambient
`ScopeIdentity` directly with target Tenant/Org + real-admin User; the authorization gate is the
job's own responsibility.

---

## 5. Migration / breakage impact

### 5.1 Namespace moves

| From | To |
|---|---|
| `YFex.Web.Tenant` (contracts in `YFex.Web`) | `YFex.Tenancy.Resolution` |
| `YFex.Web.Data.Tenant` (resolver, accessor, strategies) | `YFex.Tenancy.Resolution` |
| `YFex.Web.Data.Tenant.Strategies` | `YFex.Tenancy.Resolution` (flat) or `.Strategies` |
| `YFex.Web.AspNetCore.Tenant.*` | **unchanged** (stays) |
| `YFex.Web.Data.Storage.DapperTenantStore` | **unchanged** (stays) |

### 5.2 What breaks (all acceptable, all mechanical)

- Every `using YFex.Web.Tenant;` and `using YFex.Web.Data.Tenant;` across `YFex.Web`,
  `YFex.Web.Data`, `YFex.Web.AspNetCore`, `YFex.Web.Refit`, and their `*.Tests` projects →
  re-target to `using YFex.Tenancy.Resolution;`.
- Test fixtures (`YFex.Web.Data.Tests/Tenant/`, `YFex.Web.Tests/Tenant/`) reference the moved
  contracts (`ITenantInfo`, `ITenantStore<T>`, `TenantContext<T>`, `DefaultTenantResolver<T>`,
  `StaticTenantStrategy`) — same namespace re-target. Logic unchanged; the tests still pass against
  the relocated types (they assert first-match-wins, ignored-identifier skip, empty-on-no-match —
  behavior preserved by a pure move).
- `DapperApiEndpointStore` / Refit auth handler that consume `ICurrentTenantProvider` — namespace
  re-target only; the interface is unchanged.

### 5.3 DI reconciliation — one wiring story

Today two entry points exist and don't know about each other: `AddYFexTenancy()` (scope core) and
`AddMultiTenant<TTenant,TRow>()` + `AddYFexWebTenant<TTenant>()` (web tenancy). Target composition:

```csharp
// 1. scope core (interner + default ambient IScopeContext) — TryAdd, yields to a host override
services.AddYFexTenancy();

// 2. tenant discovery (Dapper store + AsyncLocal accessor + default resolver) — STAYS in Web.Data
services.AddMultiTenant<OrgTenant, TenantRow>("TENANTS", row => ...);

// 3. ASP.NET adapters: HttpContext accessor + middleware + the unifying scope adapter
services.AddYFexWebTenant<OrgTenant>();   // now ALSO: Replace IScopeContext with HttpScopeContext
app.UseYFexMultiTenant<OrgTenant>();      // middleware now ALSO publishes ScopeIdentity
```

- `AddMultiTenant` calls `AddYFexTenancy()` internally (idempotent `TryAdd`) so the interner always
  exists — the scope adapter needs it.
- `AddYFexWebTenant<TTenant>` additionally does
  `services.Replace(ServiceDescriptor.Singleton<IScopeContext, HttpScopeContext<TTenant>>())`,
  mirroring how it already replaces `ITenantContextAccessor`. Result: one obvious call sequence,
  and `IScopeContext` in a web host is the HTTP-backed projection of the resolved tenant.

The `DelegateTenantStrategy` / `StaticTenantStrategy` a background host uses still work — such a
host calls `AddYFexTenancy()` + `AddMultiTenant(...)` and sets the `AsyncLocal` accessor(s)
directly, no ASP.NET.

---

## 6. Phasing (each step ends build-green on both solutions)

> **Progress (2026-09-25):** Phase G ✅, Phase 0 ✅, Phase 1 ✅ (move complete — `YFex.Tenancy.Resolution`
> holds the 12 host-neutral types; YFex.Web keeps DapperTenantStore + the ASP.NET adapters; whole
> `YFex.Web.slnx` builds and **72/72 tests pass**). Phase 2 ✅ (core) — `ITenantScopeProjector` +
> `DefaultTenantScopeProjector` in `Res`, `HttpScopeContext : IScopeContext` in `Web.AspNetCore`,
> `AddYFexWebTenant` now also wires `AddYFexTenancy()` + the default projector + registers
> `HttpScopeContext` as `IScopeContext`. **Cross-tenant admin (§4.5) ✅** — host-neutral
> `ITenantImpersonationGate` (+ default `PermissionTenantImpersonationGate` requiring the
> `Tenant.Impersonate` permission claim) and `ITenantImpersonationAuditor` (+ `NullTenantImpersonationAuditor`)
> in `Res`; `TenantImpersonationOptions` (header `X-Act-As-Tenant`) + an extended
> `TenantResolutionMiddleware` in `Web.AspNetCore` that, for a gated actor, resolves the target via the
> store and stashes it (target tenant/org rungs, real-admin user rung) with an audit call; wired by
> `AddYFexWebTenant` (all TryAdd). 5 new tests (impersonate/ignored/no-header/unknown-target/gate),
> `YFex.Web` **77/77** green. Phase 3 (reconcile accessors — the recommended "keep both" is a no-op) still
> pending. Incidental fixes made in passing: a **pre-existing**
> stale `YFex.Data` ProjectReference (`YFex\src\Database\YFex.Data` → sibling `..\YFex.Data\src\YFex.Data`)
> and a `QueryBuilder`→parameterized-SQL rewrite in `DapperTenantStore` (the migrated YFex.Data renamed
> `QueryBuilder`→`SqlQueryBuilder`) — both unrelated to tenancy but blocked the build.

- **Phase 0 — scaffold.** Create empty `YFex.Tenancy.Resolution` at
  `C:\Dev\YFex\YFex.Tenancy\src\YFex.Tenancy.Resolution\` (`net11.0`, `IsAotCompatible`, refs
  `YFex.Tenancy` + `M.E.DI.Abstractions`); add to `tenancy.slnx` (and optionally the `/Tenancy/`
  folder of `yfex.slnx`). Build green (nothing consumes it yet).
- **Phase 1 — move the contracts.** Relocate the eight `YFex.Web/Tenant/` contracts + the four
  host-neutral impls from `YFex.Web.Data/Tenant/` into `YFex.Tenancy.Resolution`, renamespaced.
  Add the cross-repo `ProjectReference` from `YFex.Web`, `YFex.Web.Data`, `YFex.Web.AspNetCore`.
  Re-target all `using`s (incl. tests). No behavior change. Build + test green on `YFex.Web.slnx`.
  *This is the big, purely-mechanical step; do it in one shot so the tree is never half-moved.*
- **Phase G (prerequisite, §0) — genericize the core scope model.** In `YFex.Tenancy` /
  `YFex.CustomLogic`: replace the `Scope` enum with an opaque `byte` rank; make `ScopeIdentity` an
  ordered rung set; switch the attribute to the generic `[CustomLogic<TScope>]` form and update the
  generator/key; walk rungs generically in the NavigatR/Avalonia overlays; update
  `custom-logic-design.md` §2. Must land **before** Phase 2 (and ideally before the big move) so the
  Web projection targets the generic shape. Re-run all existing tenancy/customlogic/navigatr tests.
- **Phase 2 — add the unified identity.** Add the `ITenantScopeProjector` contract + a default
  projector to `Res` (no `IOrganizationScoped`); add `HttpScopeContext` to `Web.AspNetCore`; extend
  the middleware to run the app's projector and publish the rung set; extend `AddYFexWebTenant` to
  register `HttpScopeContext` as `IScopeContext`. New tests: the default projector (tenant `Id` →
  rank 0, user → top rank), an app projector with a custom level set, and middleware publishing a
  chain matching the resolved tenant.
- **Phase 3 — reconcile the ambient holders (OD-F).** Either keep both accessors (recommended,
  no-op beyond docs) or unify. Decide, then lock.
- **Phase 4 — downstream opt-in (out of scope here, tracked in the integration plan).** Now that
  `YFex.Web` yields a real `IScopeContext`, the Kind-B data-isolation and Kind-A override consumers
  (CQRS cache, Messaging.Rpc outbox, Capabilities) can light up per that plan's phasing.

---

## 7. Open decisions

- **OD-A — library name (`YFex.Tenancy` vs `YFex.Tenant`).** The built assembly is **`YFex.Tenancy`**;
  the user's messages repeatedly write **`YFex.Tenant`** (and `YFex.Web`'s existing namespace is the
  singular `YFex.Web.Tenant`). This is a structural no-op but must be settled before the resolution
  project is named (it would be `YFex.Tenancy.Resolution` vs `YFex.Tenant.Resolution`).
  **Recommend: keep `YFex.Tenancy`** — it is the name already shipped, tested, and referenced across
  CQRS/NavigatR/Avalonia; renaming now churns working code for a cosmetic preference. If the singular
  is truly wanted, do a single global rename in its own commit, not folded into this move.
- **OD-B — does `ITenantInfo` stay generic (`TTenant : ITenantInfo`) or collapse?**
  **Recommend: keep generic.** The whole stack (`TenantContext<T>`, accessor, resolver, store,
  middleware) is generic over `TTenant` so apps carry richer tenant data without casting, and
  `IOrganizationScoped` extends rather than replaces it. Collapsing to a non-generic `ITenantInfo`
  would force downcasts everywhere the Org rung / app fields are read. The non-generic
  `ICurrentTenantProvider` already exists for host-neutral consumers that don't want the type param.
- **OD-C — one project or two (`YFex.Tenancy` vs `YFex.Tenancy.Resolution`).**
  **Recommend: two** (§3.1) — keeps the blittable scope core free of the generic resolver surface
  and lets CQRS/Capabilities reference only what they use. Fallback: single project if the team wants
  fewer assemblies; types/namespaces are identical either way.
- **OD-D / D5 — which claim is the User rung** (`sub`, app user id, `NameIdentifier`)? Affects only
  `HttpScopeContext` (and the background host). Not blocking the move; pick before Phase 2 tests.
- **OD-E — Tenant rung value: `ITenantInfo.Id` vs `.Identifier`.** `Id` is the canonical key,
  `Identifier` the matched token (may be an alias). **Recommend `Id`** so the scope chain is stable
  regardless of which strategy matched.
- **OD-F — collapse the two `AsyncLocal` accessors?** §4.3. **Recommend keep both, one writer.**
- **`ScopeValueInterner` on the resolution path — NOT needed.** Confirmed: interning exists only for
  the CustomLogic/Capabilities *hot* dispatch (int compares instead of string hashing). The
  resolution pipeline deals in raw strings and produces a `ScopeIdentity` of strings; interning
  happens later, lazily, inside `IScopeContext.Resolve` via `ScopeChain.Fill` — exactly as the
  existing `AsyncLocalScopeContext`/`MutableScopeContext` already do. `HttpScopeContext` reuses that
  same fill path; it does not intern eagerly and does not need the interner beyond what `Resolve`
  already borrows. (Matches integration-plan OD3.)

### Integration-plan decisions owned by this plan

- **integration-plan OD4 (Web `IScopeContext` ownership) → RESOLVED here.** The adapter
  `HttpScopeContext : IScopeContext` lives in **`YFex.Web.AspNetCore`** (§2.4, §4.2); the
  host-neutral projection helper `TenantScopeProjector` lives in `YFex.Tenancy.Resolution` (§2.5,
  §4.1). `YFex.Web` takes cross-repo references to `YFex.Tenancy.Resolution` (+ `YFex.Tenancy` in
  `.AspNetCore`); there is **no** separate `YFex.Web.Tenancy` bridge project.
- **integration-plan OD5 (cross-tenant admin) → RESOLVED here, §4.5.** Supported as a first-class,
  server-validated, audited impersonation: Tenant/Org rungs point at the target tenant, the User rung
  stays the real admin, gated by a `Tenant.Impersonate` permission. Optional `ImpersonatedBy` marker
  deferred.
