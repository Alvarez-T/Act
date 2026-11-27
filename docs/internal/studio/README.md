# YFex.Studio — user-authored screens, page changes and reactions (design family)

> **Renamed 2026-11-26** from YFex.Composer (⚑C33): folder `docs/internal/composer/` → `docs/internal/studio/`, packages
> `YFex.Composer.*` → `YFex.Studio.*`, permission and key prefixes `composer.` → `studio.`. Older designs elsewhere
> still say "Composer"; they mean this library.

> Status: **proposal** (family set up 2026-11-25; first design 2026-09-30). Working name `YFex.Studio`; **repo skeleton only** (`YFex.Studio`, `studio.slnx`, empty `.Public` + `.Reactions`, 2026-11-26), no feature code.
> Decisions: **C1–C6** in [02 §0.2](02-new-screens.md) (C1–C5 decided, C6 pending); **⚑C7–⚑C55** here (§8): ⚑C7 decided
> by the user on 2026-11-25, ⚑C8–⚑C14 (Reactions), ⚑C15, C17, C18, ⚑C32–⚑C35, ⚑C39, ⚑C45 and ⚑C46 on 2026-11-26, the rest **pending**. Prefix `⚑C` checked unused in `docs/` on 2026-11-25.
> Origin: the user, 2026-11-25. Admins should change existing pages (show, hide, move, add fields and buttons, lock who may
> change what), copy a page into a per-tenant version that can later become CustomLogic code, and set up "when X happens,
> do Y" ("notify me on WhatsApp when ACME orders more than 5k").

| File | Holds |
|---|---|
| README | the levels, shared pieces, boundaries, ecosystem fit, requests to other libraries, decisions, phases |
| [01-catalog-and-flows](01-catalog-and-flows.md) | what a user may pick (catalog), triggers, steps, the flow model shared with YFex.UI, the two flow runners (screen and server), outcomes, test runs |
| [02-new-screens](02-new-screens.md) | users compose **new** screens (IR, runtime, renderers, revisions, AI, compiled tier). Was `composer-design.md` (rev 2) |
| [03-page-design-mode](03-page-design-mode.md) | **changing existing developer pages**: extension points, design mode, overlays per scope, locks, the shield, personal layouts |
| [04-reactions](04-reactions.md) | **event → condition → steps** on the server: the WhatsApp scenario, recipients, run-as, loop guard, limits |
| [05-copy-and-eject](05-copy-and-eject.md) | copying a page into a per-tenant composed screen; ejecting it to C# as a CustomLogic override |
| [06-cr1-reactions-plan](06-cr1-reactions-plan.md) | CR1 Reactions phase plan: done-when, what exists, steps, decisions ⚑C51–C55 |

## 1. In one paragraph

A developer ships pages, commands, queries, events and rule sets. Studio lets **users** change behavior at run time
**without code** and only inside what the developer exposed. Every change is data. It is checked against the catalog,
stored as a revision for a scope (tenant, store, user), enacted by a person, audited and revertible. All of it reuses one
**expression language** (YFex.Expressions), one **flow model** (YFex.UI ⚑LB31) and one **catalog** (YFex.Capabilities).
Studio is not developer code per tenant (that is **YFex.CustomLogic**) and not desktop automation (that is
**YFex.Automation**).

## 2. The levels: go up only when the level below cannot do it

| Level | What the user does | Stored as | Runs on | Document |
|---|---|---|---|---|
| **0. Settings** | defaults, switches | Tenancy scoped settings (Forms ⚑FM22, atlas F27) | everything | — |
| **1. Overlay** | on a developer page, through declared extension points: hide, show, move, relabel, require (stricter only), add extension fields, add buttons to slots, run a flow on page events | overlay revision per scope, applied **on top of** the developer page | everything incl. Native AOT, iOS, Android (interpreted) | [03](03-page-design-mode.md) |
| **2. Copy** | copy the page's generated shape into a composed screen for one scope and restructure it freely; same commands and queries | composed screen revision (02 IR) + a route overlay | everything (interpreted) | [05](05-copy-and-eject.md) |
| **3. Eject** | when level 2 is not enough, the copy becomes C#: a `[CustomLogic]` page and view model a developer finishes and ships | code in a tenant assembly | everything, after a release | [05](05-copy-and-eject.md) |
| **New screen** | a screen that has no developer page (quick-sale form, dashboard) | composed screen revision | everything | [02](02-new-screens.md) |
| **Reaction** | when an event or a schedule fires and a condition holds, run steps on the server | reaction revision per scope | server (central, or site broker ⚑C27) | [04](04-reactions.md) |

```
               developer page (code)          developer commands, queries, events, rule sets (code)
                      │                                          │
   L1 overlay ────────┤ applied on top                           │ catalog (Capabilities, opt-in)
   L2 copy ───────────┘ shape copied once, then owned            ▼
   L3 eject ── C# from the copy ──► CustomLogic         flows (YFex.UI FlowModel) ◄── buttons, page events, reactions
                                                                 │
                                       one invoke path: CQRS validation → authorization → Rules → handler
```

A user never writes code, never loosens a contract, never reaches something the catalog does not list. Business limits
that must always hold stay in **YFex.Rules** (02 principle 4); a screen only makes things stricter or friendlier.

## 3. Shared pieces (designed once)

| Piece | Owner | Used by |
|---|---|---|
| Catalog: commands, queries, events, jobs, routes with `ParametersSchema` and `SideEffect` | YFex.Capabilities (+ Studio catalog adapter, ⚑LB33) | all levels, reactions |
| Read a record by field id, build a command from values (C3) | YFex.Validation `[Readable]` / `[Buildable]` (**built 2026-11-26**, uncommitted) + Cqrs invoker table (**not built**) | everything that invokes or reads |
| Conditions and formulas | YFex.Expressions (+ scope prefixes `trigger.`, `step.`, ⚑LB34) | visible-when, computed columns, reaction conditions |
| Flow model, condition and flow builders | YFex.UI (`FlowModel`, `YConditionBuilder`, `YFlowBuilder`, ⚑LB31–LB40) | buttons, page events, reactions |
| Revisions per scope: draft → check → enact → audit → revert → device sync | `YFex.Revisions` (to extract from Rules.Admin, C4/C5) | overlays, copies, new screens, reactions |
| Extension fields (`ExtensionBag`, `IExtensible`) | Studio.Extensions + Cqrs carrier (02 §11.2) | overlays, copies, reactions (conditions on them) |
| Scope chain, most-specific-first lookup | YFex.Tenancy (02 §16 `ScopedTable`, atlas F27) | overlays, reactions |

## 4. Boundaries

1. **Catalog only.** Only items the developer marks composable appear; the catalog is filtered by the author's permissions
   and tenant.
2. **Stricter only.** A screen or overlay can require, narrow or hide; it cannot loosen a contract constraint
   (`studio.check.looser`, 02 §6.1). Loosening is a Rules decision with its own permission.
3. **Same server path.** Every command from a button, a copy or a reaction passes CQRS stage 1–4 like any caller.
4. **No code at run time.** Level 3 always needs a developer and a release; Native AOT and iOS cannot load code anyway.
5. **Hiding is not security.** Who may *see or change a value* is server-side field access (⚑C19), not an overlay.
6. **Every change is a revision**, enacted by a person with the `*.enact` permission for that scope, with an audit diff.

## 5. Scenarios at a glance

| User wants | Level | Where |
|---|---|---|
| Hide "Discount" on the order page for store 12 | 1 | 03 §4 |
| Require "Delivery window" for ACME and add it as a new field | 1 | 03 §4 |
| Cashiers cannot see "Cost" | field access, not an overlay | 03 §6, ⚑C19 |
| Each user hides grid columns for themselves | 1 (personal layer) | 03 §5 |
| "Send by WhatsApp" in the orders row menu | 1 (slot + flow) | 03 §4, 01 |
| After saving a quote above R$10k, notify the manager | 1 (page event + flow) or reaction | 03 §4 / 04 |
| ACME gets a completely different order screen | 2, then 3 if needed | 05 |
| A quick-sale screen that does not exist | new screen | 02 |
| WhatsApp me when ACME orders more than 5k | reaction | 04 §2 |
| Daily summary of yesterday's orders | reaction (schedule) | 04 §3 |
| VIP discount cap, approval above a limit | Rules.Dynamic (authored in the Rules panel) | 02 §8.3 |

## 6. Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md); outcomes per
[ecosystem-fit §1](../../agent/rules/ecosystem-fit.md). Rows already settled in 02 are summarized.

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core | used | `RawValue`/`RawType`, `FieldPath`, `StableHash64`, `Consent`, `Contacts`, Localization keys | — |
| YFex.Localization | used | admin-written labels and button texts in the dynamic message layer (02 §5) | — |
| YFex.Validation | used; lacks a feature | `[Readable]` / `[Buildable]` → `IReadableRecord<T>` / `IBuildableRecord<T>` (C3, built 2026-11-26, uncommitted; read-only payloads are `IReadableRecord<T>`); event payload shapes for triggers | C13 |
| YFex.Expressions | used; lacks a feature | conditions, computed columns, defaults; scope prefixes (⚑LB34) | — |
| YFex.Rules / Rules.Dynamic | used | enforced limits; the Rules panel; promote-to-rule (C2) | — |
| YFex.State | used | designer view models, undo history, `ValidationBag` | — |
| YFex.Mvvm | fits, lacks a feature | page events (`Loaded`, `Saved`) a page can expose to flows | C16 |
| YFex.NavigatR | used; lacks a feature | scoped route overlay is built (code); a data-driven target "composed screen for this scope" is not | C23 |
| YFex.UI (+ Forms, DataGrid) | used; lacks features | builders (⚑LB31–40); runtime override seams in `YForm` and `YDataGrid`; design-mode surface | C20, C21 |
| YFex.Cqrs | used; lacks features | invoker table by key (C3); `IExtensible` carrier; field-access check at stage 2 | C13, C19 |
| YFex.Messaging | used | events as triggers; outbox and inbox duplicate filter for reactions | C10, C13 |
| YFex.Capabilities | used; lacks features | audiences per capability (users, AI, search) and per-parameter value policies (⚑C13, C14); overlay registry so composed buttons and screens appear in search and AI (02 §16) | C13, C14, C16, C32 |
| YFex.Search | used; small change | composed screens, buttons and reactions are findable through Capabilities; its catalog provider moves from `Hidden` to `CapabilityUse.Search` | C32 |
| YFex.Authorization | used; lacks a feature | `*.enact` and `*.customize` permissions; run-as; **field access** (who sees or changes a value) does not exist | C11, C19 |
| YFex.Authentication | not a fit | identity is consumed through Authorization and Actors | — |
| YFex.Actors | used | author of each revision; real actor `reaction:{key}` with the existing `ActorKind.Job` (no new kind) | C11 |
| YFex.Tenancy | used | scope chain and layers; scoped lookup (02 §16, atlas F27) | C18 |
| YFex.CustomLogic | used | level 3 eject target; compiled page swap and slot injection are the code-side twins of overlays | C24 |
| YFex.Licensing | fits | "customization" and "reactions" as licensed modules or seat-limited features | C28 |
| YFex.Audit | used | every enact and rejected attempt; reaction runs with outcome | — |
| YFex.Logging | used | probes through core Diagnostics: runs, skips, failures, latency | — |
| YFex.Privacy | used | classified fields in reaction payloads and test-run traces; masking reused by field access | C19, C31 |
| YFex.Time | used | `EffectiveFrom` on trusted time; schedules in the scope's time zone | — |
| YFex.Orchestration | used | multi-step reactions as durable processes (resume, waits, compensation) | C10 |
| YFex.Jobs | used | schedule triggers; `studio.flow.run` template (02 §16) | C10 |
| YFex.Resilience | used | step retries; run quota per scope (Notifications ⚑N9 quota) | C30 |
| YFex.Notifications | used | the `notifications.send` step: templates, `Recipient` (actor, reference, list, contact, typed destination), quiet hours, opt-out, consent, per-tenant provider | C14 |
| YFex.Inference | fits | a judgment as a condition input (C6) | C6 |
| YFex.Automation | fits | a desktop task exposed as a capability can be a reaction step (legacy systems without API) | C29 |
| YFex.Persistence | used | device cache of revisions | — |
| YFex.Data | used | revision store SQL (`YFex.Revisions.Data`), extension-bag column | — |
| YFex.Backup | used | overlays, screens and reactions are tenant data and go into per-tenant backup and restore | — |
| YFex.Offline | fits | overlays must work offline; reactions on a disconnected site | C26, C27 |
| YFex.Installations | fits | signed bundles to sites as the delivery road for revisions | C26 |
| YFex.Hosting (design) | fits | the reaction runner as a host module on central or the site broker | C27 |
| YFex.Cluster (design) | fits | one worker per (event partition, tenant) when central has several nodes | C27 |
| YFex.Web | not a fit | outbound calls go through Notifications (webhooks) or a capability, never a raw HTTP step | — |
| YFex.EventSourcing | not a fit | aggregate events reach reactions through Messaging, not directly | — |
| YFex.Files (design) | not a fit now | a later "export file" step would be a Files capability | — |
| YFex.Cryptography | not a fit directly | signing of distributed revisions belongs to Installations | — |
| YFex.Networking, Devices, System, Profiling, Security | not a fit | no user-authored logic over them; personal layouts are not profile signals | — |
| YFex.Provisioning, Installer, Xml | not a fit | infrastructure lifecycle or document format, nothing a user composes | — |

## 7. Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Validation | `[Readable]` / `[Buildable]` + `IBuildableRecord<T>` (C3) — **built 2026-11-26** (02 §7.2 status) | no | C3, C13 | CP0 |
| YFex.Cqrs | invoker table (C3) — **built 2026-11-26**, keyed by message type; the capability key → invoker mapping belongs to YFex.Capabilities.Cqrs (02 §7.2 status); `IExtensible` carrier; field-access check at stage 2 | no | C3, C19 | CP0, CD1 |
| YFex.Capabilities | **audiences** per capability (users composing, AI, search) replacing `Hidden`; per-parameter value policy (typed value allowed per audience); events in the catalog; dynamic overlay registry so tenant admins narrow audiences and policies at run time | yes (`Hidden` replaced, ⚑C32) | C13, C14, C16, C32 | CP0 |
| YFex.Search | `CapabilityCatalogProvider` reads `CapabilityUse.Search` instead of `CapabilityFlags.Hidden` (+ its test) | yes, with Capabilities | C32 | CP0 |
| YFex.Authorization | field access policies (see, change) evaluated on the server; run-as resolution for reactions (⚑RA2 pattern, real actor `reaction:{key}`) | no (additive) | C11, C19 | CR1, CD1 |
| YFex.UI.Forms | `IFormConfiguration<TModel>` (⚑FM62) becomes `IUiConfiguration<T>` (model or page) with customization rights, extension fields, slots and events; `FormLayer.Composer` → `Studio`; data grid and details read it | yes, on the unmerged branch only | C16, C20 | CD1 |
| YFex.UI DataGrid | runtime column overrides and extra computed columns from the owner | no | C20 | CD1 |
| YFex.UI | design-mode surface (selection adorners over a live view, inspector, palette) | no | C21 | CD1 |
| YFex.Mvvm | page events a view model exposes to flows | no | C16 | CD2 |
| YFex.NavigatR | data-driven route target per scope (composed screen) | no | C23 | CK1 |
| YFex.Messaging | event metadata for reactions: origin reaction id and cascade depth on the call context | no | C12 | CR1 |
| YFex.Tenancy | generic scoped lookup out of `CustomLogicRegistry` (02 §16) | no | C18 | CD1 |
| Rules.Admin | extract `YFex.Revisions` earlier than Studio P3 (reactions are the second consumer) | rename (C5) | C25 | CR1 |
| YFex.Installer (orientation) | release bundles name a **flavor** per scope; packages of a tenant flavor are served only to that tenant's installations | no (design not written) | C39 | first per-tenant page |

## 8. Decisions

C1–C6: [02 §0.2](02-new-screens.md). All rows below are **pending** unless marked ✅.

| ⚑ | Topic | Proposal | Rejected alternatives | Where |
|---|---|---|---|---|
| C7 ✅ | Document layout | **decided 2026-11-25 (user):** one design family, separate documents (README + 01–05) | one large file; unrelated designs | here |
| C8 ✅ | Name of event-triggered user flows | **decided 2026-11-26 (user): Reactions** | "Automations" (collides with YFex.Automation), "Workflows" (sounds like Orchestration) | 04 §1 |
| C9 ✅ | Where reactions live | **decided 2026-11-26 (user):** package `YFex.Studio.Reactions` in the Studio family | own repo; inside Jobs or Orchestration | 04 §9 |
| C10 ✅ | Server runner | **decided 2026-11-26 (user):** one-step reactions run in a Messaging handler with Messaging's idempotency key (event id, reaction id, revision); multi-step ones as an Orchestration process; schedules through the Jobs `studio.flow.run` template | a private runner with its own retries and ledger | 01 §5, 04 §7 |
| C11 ✅ | Who a reaction runs as | **decided 2026-11-26 (user):** the run-as pattern of ⚑RA2: actor = the author (their permissions, re-checked at every run; paused when they lose them), real actor = `reaction:{key}` with the existing `ActorKind.Job`, relation `Delegation`. No new actor kind | a new `ActorKind.Reaction`; a service actor per tenant; the system account | 04 §5 |
| C12 ✅ | Loop guard | **decided 2026-11-26 (user):** cascade depth on the call context (max 3) and no reaction on events its own steps caused, unless the author allows it | no guard; rate limits only | 04 §6 |
| C13 ✅ | What users and AI may use | **decided 2026-11-26 (user):** opt-in and typed: events (and commands, queries) get a read-only `[Readable]` shape, and **YFex.Capabilities says who may use each item**: an audience set per capability (users composing flows, AI agents, search), set by the developer and narrowed per scope (existing `CapabilityScope`) or by a tenant admin at run time; something may be open to users and closed to AI | all events offered automatically; raw JSON payloads; one `.Composable()` flag for every consumer | 01 §1–2 |
| C14 ✅ | Recipients and free values | **decided 2026-11-26 (user):** references (me, a user, a role, a payload field, a recipient list, a contact) **and typed values** (an address, a custom text), each allowed only when configured: the capability declares per parameter and audience whether a typed value is accepted, and a tenant admin can turn it off; Notifications already takes `Recipient.To(destination)` and still applies opt-out, consent and quiet hours | references only; users only | 04 §4, 01 §1 |
| C15 ✅ | Change developer pages? (02 open question 4) | **decided 2026-11-26 (user):** yes, only where the developer allows it. "Change" is one of three mechanisms, chosen per page: (1) **configuration layers** the page already reads (Forms ⚑FM62 layers, extension points); (2) **data loaded per tenant** from the database (overlay revisions); (3) last resort and optional, a **per-tenant CustomLogic build generated by our server only** from the same stored description (⚑C36). Never external code | only new screens | 03, 05 |
| C16 | Where the developer says what may change | **name decided 2026-11-27 (user, forms ⚑FM64 ✅): `IViewConfiguration<T>`** (read `IUiConfiguration<T>` below as that name). **revised 2026-11-26 (user prefers one configuration):** one `IUiConfiguration<T>` with one builder, where `T` is a model **or** a page view model. On a model: field rules that apply wherever it is shown (`YForm`, `YDetails`, `YDataGrid`, ⚑DG9); on a page: slots, page events, and page-only changes to the models it shows by path (`ui.Model(x => x.Order).Field(o => o.Discount).Hidden()`). Same layers and data sources as Forms ⚑FM62, which it renames and extends (⚑FM64; cheap while `Agent/forms-part2` is unmerged) | two types (`IFormConfiguration` + `IPageConfiguration`); a separate customization profile; attributes; `IViewConfiguration<T>` ("View" already means the visual class) | 03 §2 |
| C17 ✅ | Default openness | **decided 2026-11-26 (user), may be revisited:** layout (hide, move, relabel) open for generated fields **when the form's configuration allows it**, unless locked; behavior (require, buttons, events, extension fields) closed unless declared | everything closed; everything open | 03 §2 |
| C18 ✅ | Layers and locks | **decided 2026-11-26 (user):** layers stack (developer → tenant → store … → user), any layer can lock items for the layers below, plus a personal layer. Two additions: a layer can target an **audience inside the scope** (an Authorization role or group, e.g. a department: hide a field for Sales, show it for Finance); and **what a user may personalize is configurable** per scope and audience (e.g. only data grid columns), not fixed | one tenant layer; layers without locks | 03 §5 |
| C19 | The shield (field access) | new **Authorization field access** (see / change), enforced in query results and command stage 2, masking from Privacy; design mode is only its editor | hide in the UI only; field access inside Studio | 03 §6 |
| C20 | How overlays reach controls | **2026-11-26: for forms this exists** on branch `Agent/forms-part2` (⚑FM62: `IFormChangeSource`, `FormLayer.Composer`, merge that never loosens); left: the same for `YDataGrid` columns and page slots. | `YForm` and `YDataGrid` take neutral override lists from the owner (UI types); Studio converts its overlay to them | controls read Studio types; overlays as generated XAML | 03 §7 |
| C21 | Design-mode surface | generic `YDesignSurface` in YFex.UI (adorners, inspector, palette, `YDrag`), no Studio types | a Studio-only designer control | 03 §3 |
| C22 | Copy of a page | the copy starts from the page's shape, keeps a `Rest` node (new developer fields still appear) and its base revision; hand-written areas become closed blocks | a full snapshot that drifts; copying XAML | 05 §2 |
| C23 | Route to a copy | NavigatR gains a data-driven per-scope target ("composed screen X for ACME") beside the built code overlay | a CustomLogic page per copy | 05 §3 |
| C24 | Eject | emit a `[CustomLogic<TScope>(...)]` page + view model + Forms model from a copy, for a developer to finish; build when a real case asks; until then keep the IR stable with fixed keys | runtime compile (Tier 2) as the default path; no eject | 05 §4 |
| C25 | Phase order | CP0 → CR1 reactions → CD1/CD2 design mode → P1/P2 new screens → CK1 copy → CE1 eject; extract `YFex.Revisions` in CP0 | 02 order (reactions last) | §9 |
| C26 | Revisions to devices and sites | `YFex.Revisions.Sync` over the Installations road (signed bundles to sites) | a Studio-only sync; Offline signed rule data | 03 §8 |
| C27 | Where reactions run | central by default; a site broker runs the reactions marked "site" while central is unreachable | central only; every device | 04 §8 |
| C28 | Licensing | design mode and reactions as license modules (Licensing availability hook) | no gate | 03 §3, 04 §9 |
| C29 | Desktop tasks as steps | an Automation task exposed as a capability may be a reaction step (marked `Irreversible`, needs approval) | no access to Automation | 04 §3 |
| C30 | Run limits | per-scope quota of reaction runs and of messages through the Resilience quota (⚑N9) | no limit; a private counter | 04 §6 |
| C31 | Personal data in reactions | payload fields classified Personal may go only to internal users or the data subject on transactional channels; marketing needs consent (⚑N7); traces masked | no check | 04 §6 |
| C32 ✅ | `CapabilityFlags.Hidden` | **decided 2026-11-26 (user):** removed; "not in the search bar" is `Search` missing from the `CapabilityUse` list (⚑C34), and per-scope hiding (`CapabilityScope` override) removes `Search` for that scope. Hard replace, 5 places: Capabilities registry, `CapabilityScope` doc, one Capabilities test, Search `CapabilityCatalogProvider`, one Search test | keep `Hidden` beside the list with a precedence rule | 01 §1 |
| C33 ✅ | Library name | **decided 2026-11-26 (user): YFex.Studio** (was the working name YFex.Composer) | YFex.Composer; YFex.LowCode; YFex.Customization (too close to CustomLogic) | here |
| C34 ✅ | How a developer says who may use a capability | **decided 2026-11-26 (user):** `.AvailableTo(CapabilityUse.X)`, a **closed flags enum** `CapabilityUse` owned by YFex.Capabilities with `Ai` (AI agents call it directly) and `Search` (the search bar); **no `All` value**, so a consumer added later starts closed for explicit lists. Shortcut **`AvailableToAll()`**: every `CapabilityUse` value, **including values added later** (documented on the method); write the list to keep a fixed set. Replaces the planned `.Composable()`, which said neither *who* nor *for what* | `.Composable()`; `.ExposeTo(Exposure.X)`; one method per consumer; a type named `Use` (too generic, clashes with `UseX()` methods); an extensible value object (no app adds consumers today) | 01 §1 |
| C35 ✅ | Value for "users may pick it when they build" | **decided 2026-11-26 (user): `CapabilityUse.UserBuilt`**. It names the use, not the library, so Capabilities (lower layer) never names Studio. An AI drafting a Studio flow that a person enacts goes by `UserBuilt`, not `Ai` | `Use.Studio` (a lower library naming a higher one); `Use.Builders` | 01 §1 |
| C36 | Compiled tier from Studio pages | **revised 2026-11-26 with the user:** a Studio page keeps running interpreted; for performance (large grids, many formulas, weak handhelds) or typed members other generators need, the server keeps a **compile draft** (description + generated C# inputs for the existing generators) that a developer, or a future generator, turns into tenant CustomLogic, tested and shipped as a per-tenant update (⚑C39); the compiled page serves only the revision it was built from, the description stays the source of truth. How the draft becomes code is left for later (user) | fully unattended generation as the default; generated code as the source of truth; no compiled tier | 05 §6 |
| C37 | Conflicting audience layers | when a user is in two audiences (departments) with different settings, the stricter wins (hidden, read-only, required add up), the same merge rule as ⚑FM62 | the most specific audience wins; the last enacted wins | 03 §5 |
| C38 | Personal rights | what a user may personalize = the ⚑FM46 `FormMemory` items (sections, detail placement, grid columns, tab) plus field order and hiding, granted per scope and audience as permissions | fixed personal rights | 03 §5 |
| C39 ✅ | How per-tenant code reaches devices | **decided 2026-11-26 (user):** build **flavors per tenant** (`1.4.2+acme.3`: base + the tenant's CustomLogic project, chosen by an MSBuild property, base tests run on every flavor, vendor-signed, targeted per scope by the Installer release bundle) for desktop, POS and handhelds; **one combined build on central**; combined build as the fallback where flavors are impractical (public phone stores) | one combined build everywhere (every customer carries the others' code); loading assemblies at run time (impossible on AOT) | 05 §7 |
| C40 | Uses of a capability that declares none | **proposal (built 2026-11-26 as the interim default):** `Ai` and `Search`, today's behavior; `UserBuilt` stays opt-in (⚑C13), so nothing reaches users' builds unless the developer opens it. Built in YFex.Capabilities (`CapabilityUse`, `.AvailableTo` / `.AvailableToAll()` on the view-model profile and, since 2026-11-26, as CQRS builder verbs `b.Command<T>().AvailableTo(…)` stored in Cqrs `MessageFeatures`) and YFex.Search | `None` (every capability must declare its uses; breaks every existing capability until declared); every use including `UserBuilt` (contradicts the opt-in of ⚑C13) | 01 §1 |
| C41 | Where the per-event payload read table lives (non-generic read path of reactions) | **proposal (built 2026-11-26 as option A):** **the Capabilities.Cqrs bridge, through Cqrs `MessageFeatures`.** In the aggregate configuration, `b.Event<T>().ReadablePayload()` (a Capabilities.Cqrs verb, `T : IEvent, IReadableRecord<T>`) stores a typed `EventPayload<T>` as the event's feature; its `object` overloads cast once and call the generated `T.TryRead` / `T.Read` (one virtual call, no reflection, no delegate), and `EventPayloads` (DI, built from the messaging registry) finds it by event type or capability key. Costs: one verb per trigger event, next to the `.AvailableTo(UserBuilt)` that opens it, so it is the ⚑C13 opt-in and is checked at compile time; only hosts with the CQRS bridge see the table; no YFex.Validation change. YFex.Cqrs gained (2026-11-26, with the user's go-ahead) `b.Event<T>()` for single events (before, `Events` listed only `GroupAs<TUnion>()` groups) and `Feature(...)` on the query builder and the new event builder | (B) **Validation generator:** the module initializer also calls a `RecordReaders.Register<T>()` for every `[Readable]` type, into a type-keyed table next to `ObjectValidation` and `JsonSchemas`. Automatic and visible to every host (Messaging, MCP servers), but it needs a YFex.Validation edit (generator + runtime), adds an entry for every reader type, and turns the opt-in into a run-time check; the bridge API would stay and could switch to it later. (C) **Cqrs generator:** reader thunks per event on `CompiledMessagingRegistry`. Only CQRS, repeats what Validation already knows, and YFex.Cqrs is being edited (invoker table) | 01 §2, Capabilities README |
| C42 ✅ | Where record access is declared | **decided 2026-11-26 (user): B; generator side built (uncommitted); Cqrs verbs `.Readable()` / `.Buildable()` on the command and query builders and `.Readable()` on the event builder built 2026-11-26 (uncommitted).** Also in the aggregate configuration, next to `.Validation(...)`: `b.Command<T>().Buildable()`, `b.Event<T>().Readable()` (renamed from RecordAccess 2026-11-26, user). The verbs must be named `Readable` / `Buildable` (the cheap syntax filter, like `.Validation`), so `.ReadablePayload()` cannot carry it; it keeps its `IReadableRecord<T>` constraint. The Validation generator reads the verb at compile time through a marker attribute YFex.Validation owns (no string matching) and emits the same `IBuildableRecord<T>` / `IReadableRecord<T>` into the partial type; records nested in a declared message get access too. Same limit as compiled fluent rules: configuration and type in one project, otherwise an error pointing to the attribute. The attribute stays for types without a configuration (query rows). Cqrs verbs by the event-shapes work, generator side in YFex.Validation | (A) attribute only (as built); (C) implied by `.AvailableTo(UserBuilt \| Ai)` (the generator would read Capabilities verbs); (D) attribute plus a diagnostic when an exposed message lacks it (would break every message with the default `Ai \| Search` uses, ⚑C40) | 02 §7.2 |
| C43 | Rules on record access | YFex.Rules builds the planned `ISituation.Build<TReader>` (02 §16) on `IMemberSource` / `IBuildableRecord<T>` instead of a second reader; optionally the generated `ISituation.Read` / `TryGetFieldId` delegate to `IReadableRecord<T>` (same meaning: read a field by id) | a separate situation reader | 02 §7.2 |
| C44 | Expressions over records | YFex.Expressions gets `RecordFieldReader<T> : IFieldReader` over `IReadableRecord<T>`, so Reaction conditions run as bound programs by field id instead of `TryRead(path)` per evaluation; needs a reader-side item count and item read on `IReadableRecord<T>` | parsing paths on every evaluation | 02 §7.2 |
| C45 ✅ | Outside consumers in `CapabilityUse` | **decided 2026-11-26 (user):** add `Mcp` (outside AI clients call it as a tool through an MCP server the app hosts; separate from `Ai`, our own agent inside the user's session with our confirmation and approval steps) and `PublicApi` (other programs, partners and integrations, through the app's public HTTP API; not the app's own clients). `CapabilityUse` widened to `ushort`. Built in YFex.Capabilities; no YFex MCP server or inbound API host reads them yet, and `AvailableToAll()` now also opens them, so sensitive capabilities list their uses | `Api` (would also cover the app's own thin clients); one `Ai` value for our agent and outside agents; adding the values only when a host exists | 01 §1 |
| C46 ✅ | Webhooks in `CapabilityUse` | **decided 2026-11-26 (user):** `CapabilityUse.Webhook`, outside subscribers receive the event. **Events only** and only with `.ReadablePayload()` (the webhook sends the payload, like ⚑C13 for `UserBuilt`): rejected where declared on a command, query or view-model member, and by the registry build for a hand-built descriptor that is not an event; `AvailableToAll()` leaves it out on non-events | `Hook` (clashes with the generated `CanExecute` hooks and other hooks); webhooks on commands and queries | 01 §1 |
| C47 | Who delivers webhooks | **proposal (2026-11-26):** YFex.Notifications, which already lists webhooks as a channel (outbox, retries, per-tenant provider); the subscriber list per scope and payload signing (YFex.Cryptography) to be designed there; Capabilities only says which events may be sent | a webhook sender in Capabilities or Studio; one in YFex.Web (outbound clients, no outbox) | 01 §1 |
| C48 | Where invoke by capability key lives | **proposal (built 2026-11-26):** the gate of every use is in core (`CapabilityGate.Check(key, use, caller)`, scope-aware, same answer as the agent's `CanInvoke`), the dispatch in the bridge (`CqrsCapabilityInvoker.InvokeAsync(key, use, caller, ref args, confirmed)` → `CapabilityInvocation` = `MessageOutcome` or the stopping `ActionGate`), so core stays free of Cqrs and Validation. The agent's `Invoke` is that call with `CapabilityUse.Ai`; `ICapabilityAgentApi` keeps `CanInvoke` only | a neutral `ICapabilityInvoker` in core with its own outcome and value-source types (duplicates `MessageOutcome` / `IMemberSource`); core referencing Cqrs | 01 §1 |
| C49 | What the gate stops before dispatch | **proposal (built 2026-11-26):** a use the capability lacks → unknown key; `Denied` → stopped; Irreversible → stopped unless the caller passes `confirmed: true` (the Studio passes it when the author confirmed at enact time, our agent after asking the person); a declared approval → **dispatched**, stage 2 checks the approval tokens the message carries and answers `NeedsApproval`. Open: an `IApprovalBound` command cannot be `[Buildable]` today (record access cannot build `Approvals`, YFV0041), so approval commands cannot run by key until Validation skips that member or the invoker attaches tokens | stop on `NeedsApproval` too (approvals could never be sent through this path); dispatch everything and leave confirmation to each caller | 01 §1, 04 §5 |
| C50 | User steps that cannot be built | **proposal (built 2026-11-26):** with the invoker table registered, a command or query opened to `UserBuilt` without an invoker (`[Buildable]` / `.Buildable()`) fails the capability build; invocable ones carry `CapabilityFlags.Invocable` | list it in the users' table but fail when a flow runs it; drop it silently from the users' table | 01 §1 |
| C51 | Run-as at run time (CR1) | **blocked on YFex.Authorization:** request recorded there as ⚑RA12 (run-as resolver, 09 Phase 7, shared with Jobs D2b); Studio's run-as step waits, no Studio stop-gap | a Studio snapshot of the author's permissions | 06 §4 |
| C52 | The notify step (CR1) | **blocked on YFex.Notifications:** request recorded there as ⚑N31 (`SendNotification` command + `notifications.send` capability, proposed right after N2) | Reactions call `INotificationSender` directly | 06 §4 |
| C53 | `TypeShape` → `ExprSchema` (CR1) | **blocked on YFex.Expressions:** request recorded in [expressions/README](../expressions/README.md) | build it inside YFex.Studio | 06 §4 |
| C54 | Where the reaction router hooks events (CR1) | a typed handler per event registered by `o.Event<T>()` on the bus apps publish through (inspect Cqrs vs Messaging) | a Cqrs pipeline behavior over every event | 06 §4 |
| C55 | CR1 split | CR1a: event → condition → command steps, e-mail/testing channel; CR1b: schedules, Orchestration, quotas, privacy, WhatsApp, recent-events test | one phase | 06 §1, §4 |

## 9. Phases (proposed order, ⚑C25)

| Phase | Deliverable | Depends on | Done when |
|---|---|---|---|
| **CP0** catalog and access | C3 (`[Readable]` / `[Buildable]`, invoker table), `.Composable()` incl. events, Studio catalog adapter (⚑LB33), `YFex.Revisions` extraction (C4/C5) | — | a command is built from values and dispatched by key, AOT-clean; revisions enacted per scope. **Repo skeleton exists (2026-11-26):** `YFex.Studio/studio.slnx` with empty `YFex.Studio.Public` (core only) and `YFex.Studio.Reactions`, `YFex.Studio.Tests` (reference check); Release build 0 warnings, 2 tests pass; not committed. **Cqrs invoker table built (2026-11-26, uncommitted):** `YFexMessageInvokers.All` per assembly, `MessageInvokerTable`, `MessageOutcome` (02 §7.2 status); **Capability key → invoker built (2026-11-26, uncommitted):** `CapabilityGate` (core, every use, per scope), users' table `CompiledCapabilityRegistry.UserBuilt` / `UserBuiltFor` with `CapabilityGate.ListUserBuilt(caller)`, `CqrsCapabilityInvoker` (bridge, the agent's `Invoke` under `Ai`), `CapabilityFlags.Invocable` (⚑C48–C50 pending); next: a JSON `IMemberSource` for AI tool arguments (YFex.Validation), the Studio catalog adapter (⚑LB33) |
| **CR1** reactions (**blocked**: Authorization ⚑RA12, Notifications ⚑N31, Expressions request, Revisions ⚑C25; plan [06](06-cr1-reactions-plan.md)) | event and schedule triggers, conditions, `notifications.send` / command steps, run-as, loop guard, quotas, test run | CP0, Notifications N2 | "WhatsApp me when ACME orders > 5k" enacted by a user, runs once per order, audited |
| **CD1** design mode, layout | extension points, design surface, hide/move/relabel/require, extension fields, personal layer, locks | CP0, Forms + DataGrid seams | a tenant hides and requires fields on a developer page; a user personalizes; another tenant is unaffected |
| **CD2** design mode, behavior | buttons in slots and page events with flows (client runner from 02 P1) | CD1, 02 P1 runner | "Send by WhatsApp" in a row menu runs through CQRS |
| **P1/P2** new screens | as in [02 §15](02-new-screens.md) | CP0 | 02 criteria |
| **CK1** copy | copy a page to a composed screen, `Rest` node, base tracking, route overlay | P2, CD1 | ACME opens its own order screen; a new developer field still appears |
| **CE1** eject | C# emitter to a CustomLogic page | CK1, a real case | an ejected copy builds and matches the copy (02 §13 differential test) |
| **Field access** | Authorization field access (C19) | Authorization | the shield hides "Cost" from cashiers in queries, grids and the API |
