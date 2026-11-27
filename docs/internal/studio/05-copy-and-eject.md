# 05 — Copying a page and ejecting it to CustomLogic

> Part of the [Studio design family](README.md). Status: **proposal** (2026-11-25), levels 2 and 3 of README §2.
> Decisions ⚑C22–⚑C24 (README §8), pending.
> Origin: the user, 2026-11-25: besides changes on top of a developer page, a tenant may need a version of the page
> "copied" and restructured for it, which could later become CustomLogic; not everything allowed, but the possibility
> should already be in the package.

## 1. When to use which

| Need | Use | Why not the next level |
|---|---|---|
| hide, move, require, a few new fields or buttons | overlay (03) | — follows the developer page automatically |
| a different structure: other sections and tabs, fields from two models, a different grid, another order of work | **copy** (§2) | an overlay cannot restructure |
| behavior the catalog cannot express (a custom control, special calculation, device integration) | **eject** (§4) | needs code |

A copy is owned by its scope and falls behind the developer page unless §2's rules keep it in step, so overlays are
always the first choice.

## 2. The copy (⚑C22)

"Copy this page for ACME" turns the page's generated parts into a composed screen (02 IR) bound to the **same**
commands and queries:

| Page part | Becomes |
|---|---|
| a `YForm` from a shape | `Section`/`Widget` nodes per field, with the shape's editors, constraints and conditions |
| a `YDataGrid` from a shape | a `Grid` node with its columns |
| declared slots and their buttons | `ActionSet` flows |
| the page's overlays in force for ACME | applied before copying, so the copy starts as ACME sees the page |
| hand-written areas | a **closed block**: placed, moved or removed as a whole; its inside is not editable |
| fields not placed | a **`Rest` node** (like Forms' `YFormRest`): every shape field the copy does not place, in shape order |

The `Rest` node keeps a copy in step: a field the developer adds later still appears (or the admin hides it there).
The copy also remembers its **base page version**; when the developer page changes, the designer shows what changed
("2 new fields, 1 removed, the Save command gained a parameter") and the copy is checked against the new catalog.

Cons: needs the converter from Forms shapes to the 02 IR, which needs the shared hints type (Forms ⚑FM29). Pages built
mostly by hand copy poorly; the designer says so before copying.

## 3. Opening the copy (⚑C23)

NavigatR already resolves a route to a different view model per scope from **code**
(`RouteRegistry.RegisterFor`, CustomLogic P2, built). A copy needs the same from **data**: for route `OrderRoute` in scope
ACME, open composed screen `acme/order-editor` revision 4. Request to NavigatR: a per-scope route target that a store
supplies at run time, resolved most-specific-first like the code overlay. The original page stays reachable for
comparison by admins.

## 4. Eject to CustomLogic (⚑C24)

When a copy hits the wall, a developer ejects it:

```
composer eject acme/order-editor --to src/Acme.Customizations
  → AcmeOrderEditorModel.cs            Forms model with [Field] attributes from the copy's widgets and checks
  → AcmeOrderEditorViewModel.cs        flows as [StateCommand] methods calling the same commands
  → AcmeOrderEditorPage.cs             C# markup: YForm with YField placements, grid, slots
  → AcmeOrderRoute.cs                  [CustomLogic<AppScope>(AppScope.Tenant, "acme")] page swap (AppScope = the app's own levels)
```

The developer finishes it, builds it into the tenant assembly and ships it; the copy is then retired (or kept as the
fallback for platforms that do not get the assembly). Expressions become C# through the bound expression tree (02 §13);
the 02 §13 differential test compares the copy and the ejected page on sample records.

**Never at run time by default.** Compiling a DLL on a device does not work on Native AOT, iOS or Android AOT, and loading
code a user produced is a security risk (02 §3, option C stays benchmark-gated). Eject is a developer tool.

**What to keep now so eject stays cheap later:** stable node keys in the IR, canonical JSON, every widget and step named
by catalog keys (never by display text), and expressions stored as `Expr`, not text. These are already 02's rules; ⚑C24
only asks that no shortcut breaks them.

## 5. Is it worth it?

| Piece | Cost | Value | Proposal |
|---|---|---|---|
| copy (§2) | converter shape → IR, `Rest` node, base tracking, route target | tenants with a different way of working stay on the product without a fork | build after design mode (CK1) |
| eject (§4) | C# emitter over the IR | the escape hatch when data is not enough, without starting from zero | design now, build on the first real case (CE1) |

## 6. Compiled tier from Studio drafts (future, ⚑C36)

> Recorded 2026-11-26 for later (user): *how* a draft becomes CustomLogic code, by a generator or by hand, is a future
> problem. What is fixed now is the shape of the path.

A page a user designs in the Studio (a copy or a new screen) **keeps working interpreted** from its stored description.
For performance, the server may later turn it into a compiled CustomLogic page for that tenant:

```
user designs page (revision R) ──► runs interpreted everywhere, at once
        │
        └─► server keeps a **compile draft** for (page, tenant, R): the description + generated C# inputs
               │   (queue "compile candidates", visible to developers; nothing runs from it)
               ▼
          developer or a future generator turns it into CustomLogic code (⚑C24 eject output as the start)
               ▼
          tests (base suite + the tenant's + differential: compiled vs interpreted on samples, 02 §13)
               ▼
          signed per-tenant update (§7) ──► on install, the compiled page replaces the interpreted one for revision R
```

Rules to keep in mind when this is designed:

| Rule | Why |
|---|---|
| The **description stays the source of truth**; the compiled page is a faster copy of revision R | the interpreted version is always the fallback, on every platform and while an update is pending |
| A compiled page serves **only the revision it was built from**. If the admin enacts R+1 later, devices run R+1 interpreted until the next compile | an older build must never hide a newer design |
| Only pages a developer marks eligible get a compile draft | build cost grows with tenants × platforms |
| What makes it worth it is **performance** (large grids, many formulas, weak handhelds) and real typed members other generators need (extension fields in Rules situations, SQL columns) | compiling adds no power the description does not have (README §2) |
| The generator, when it exists, emits small C# inputs for the **existing** generators (Forms, State, Validation, CustomLogic) | the output matches hand-written code; no second code model |

## 7. How per-tenant code reaches devices (⚑C39, decided 2026-11-26)

Any per-tenant CustomLogic, written by hand or compiled from a draft, is compiled code. A Native AOT, iOS or Android app
cannot load an assembly later, so the code must be **in the build**. Two ways:

| | **A. One build with every tenant's code** | **B. A build flavor per tenant** |
|---|---|---|
| What ships | one binary per platform containing all tenants' CustomLogic, selected by scope at run time | the base build for most tenants; `app 1.4.2+acme.3` = base + ACME's code, for ACME only |
| Builds per release | 1 × platforms | (1 + tenants with code) × platforms; only changed flavors rebuild |
| Confidentiality and size | every customer carries the others' code | each customer gets only its own |
| Stores | works with public stores | Android: sideload/MDM (handhelds are enrolled by QR already); iOS: Apple's private business distribution, or option A for phones |
| Central servers | one process for all tenants needs all their server-side code: option A on central, or a dedicated pool per big tenant (Provisioning) | — |

**How B works, step by step:**

1. **Source.** The tenant's code lives in its own project (`Acme.Customizations`) with `[CustomLogic<AppScope>(AppScope.Tenant,
   "acme")]` types. The CustomLogic generator already emits one registration per assembly (`AddCustomLogic_<Assembly>()`).
2. **Build.** An MSBuild property (`YFexFlavor=acme`) adds that project reference and a generated `AddFlavorCustomLogic()` the
   composition root always calls (empty in the base build), so app code never names a tenant.
3. **Version.** Base version plus flavor revision as build metadata: `1.4.2+acme.3`. A base release rebuilds every flavor; a
   change in ACME's code only bumps `acme.N`.
4. **Tests.** The base suite runs on each flavor (no regression for ACME), plus ACME's own tests and, for Studio pages, the
   differential tests of §6.
5. **Sign and target.** The vendor signs each flavor (Installer ⚑IN3). Central's release bundle, addressed per scope on the
   Tenancy chain (Installer ⚑IN2), says "product X, version 1.4.2, flavor acme" to ACME's installations only; Installations
   knows each installation's tenant from enrollment, and the download is refused to other tenants.
6. **Run.** Inside the flavor, CustomLogic still selects by scope, so a flavor installed by mistake elsewhere runs the default
   code (defense in depth).
7. **Stuck flavor.** If ACME's flavor fails to build or test, ACME stays on its last good version (protocol floors from
   Installations may force a decision). Studio pages keep the interpreted fallback; hand-written CustomLogic has none.

Decided (⚑C39, user 2026-11-26): **B for desktop, POS and handhelds; A on central** (vendor-run, one process for many tenants), with A as the
fallback for platforms where flavors are impractical (public phone stores).
