# Localization and typed identities (no magic strings)

> Status: **APPROVED IN PART 2026-11-20.** ⚑LZ1–LZ7 approved (LZ2 and LZ10 as revised in §3.2–§3.3: every language lives in
> files), plus sectioned language files and cultures pt-BR + en. 2026-11-21: ⚑LZ12, LZ13, LZ14, LZ26 approved; LZ11 withdrawn;
> LZ15 folded into ⚑LZ28; Rules simplifications S1–S5 recorded as ⚑LZ28–LZ33; later the same day ⚑LZ9, LZ16, LZ28–LZ34 approved; the rest pending. LZ-P1, LZ-P2 and LZ-P3 **built 2026-11-20, uncommitted** (repo `YFex.Localization`; log: §7).
> Prefix `LZ` was unused.
> Origin: review of the Rules "why" plan ([validation/14](../validation/14-explanations-for-ui.md)) against
> `_ignore/SaleScenario`. The user found the plan too dependent on strings (`[Situation("sale")]`, `"stock.out"`,
> `AmountFields => ["Total"]`, a hand-written `IMessageSource` switch) and asked for typed identities, a modern
> multi-language text system, and a rule against magic strings (added to
> [all-projects § String identities](../../agent/rules/all-projects.md)).
> Code examples before / after: [01-sale-scenario-before-after.md](01-sale-scenario-before-after.md).

## 1. Problem

Two different kinds of string are mixed today, and both are typed by hand at every use:

| Kind | Examples in the sale sample | What goes wrong |
|---|---|---|
| **Identity**: names a thing; stored, signed or sent | `"sale"`, `"promo.l3p2"`, `"sale.installments"`, `"stock.out"`, `"Lines[].Quantity"`, `"CustomerId"`, `"Payment"` in `[When]`, `"sales.close"`, `["Total"]` | a typo or rename compiles and fails at run time (no message, wrong field, rule not found) |
| **Text**: shown to a person, per culture | `"Leve 3 pague 2"`, `"{field}: só há {0} em estoque"`, `"Parcelas"`, `[Permission(Name = "Fechar venda")]`, `IMessageBox` `Ok = "OK"` | keys repeated between the rule and the catalog; argument count and types unchecked; no missing-translation check; three localization systems (atlas F12) |

What already exists and is the model to generalize:

- **Authorization `[PermissionCatalog]`** (`YFex.Authorization.Public/Attributes.cs:18`): the id is a literal **once**, in the
  declaring attribute; the generator emits the typed property (`SalePermissions.Close`), `Ids` constants for attribute
  arguments, and an analyzer checks references. The id is never derived from the property name, because it is stored.
- **Generated field handles:** `CloseSale.Fields.CustomerId` (`FieldPath`) and `SaleSituation.Fields.Installments`
  (`SituationField<,>`) are already emitted. The sample's `FieldPath.Of("CustomerId")` is avoidable today.
- **Module message catalogs:** `YFex.Validation.Generator/MessagesGenerator.cs` turns `messages.<culture>.json` into an
  `IMessageSource` with a static switch (no `ResourceManager`). It emits **no typed keys**, so callers still write the key.
- **Core contracts:** `YFex/src/YFex/Localization` — `IMessageSource`, `IMessageCatalog`, `MessageCatalog` (layered,
  culture-major fallback, `RawValue` args without boxing). `MessageCatalog` contains the rendering engine.

## 2. Evaluation: how to produce multi-language text

| Approach | Lookup cost | AOT / trim | Typed keys | Argument checks | Tenant / dynamic layers | Plurals | Translator tooling |
|---|---|---|---|---|---|---|---|
| `.resx` + `ResourceManager` (+ generated Designer class) | string hash per lookup, per-culture satellite assemblies | works, satellite deployment per culture | properties, but they return a raw format string | none: `string.Format(object[])`, boxes value types | not built in (custom `ResourceManager`) | none | good (VS editor, XLIFF tools) |
| `IStringLocalizer` (Microsoft.Extensions.Localization) | same as resx + DI indirection | as resx | none (`localizer["key"]`) | none, `params object[]` | not built in | none | as resx |
| gettext `.po` | dictionary | third-party runtime | none | none | no | yes | very good |
| ICU / Unicode MessageFormat | parse or cache per message | third-party in .NET | none | partly (named args) | no | **yes** (plural, select) | good |
| **Generated catalog (YFex today)** | static switch on the key | **clean** | **none yet** | none yet (`RawValue`, no boxing) | **built in** (layers) | none | none yet |
| **Generated catalog + typed keys (proposed)** | **array index** for module texts; layers only when present | clean | **yes** | **yes, compile time** | built in | ICU-style subset (⚑LZ8) | export / import (⚑LZ23) |

Conclusion: `.resx` is not the better choice for YFex. It gives typed *names* but untyped *arguments*, no layers
(dynamic → tenant → app → module → built-in, which Rules and Notifications need), boxing on every format, and satellite
assemblies per culture. The existing generated catalog already wins on AOT, layers and allocation; it lacks typed keys,
argument checks, missing-translation diagnostics, plurals and a translator workflow. This plan adds those (⚑LZ2, LZ4,
LZ7, LZ8, LZ23). `.resx` stays reachable only through an optional `IStringLocalizer` bridge for ASP.NET hosts (⚑LZ24).

## 3. Design

### 3.1 Identity value objects, and the compile-time guarantee (⚑LZ1)

A value object alone does not catch errors at compile time when it converts implicitly from `string`:
`RuleCode c = "stok.out";` still compiles. The guarantee comes from **generated symbols + an analyzer**:

- Every identity is declared once (attribute literal or catalog member). The generator emits the symbol.
- Every API that takes an identity takes the value object (`RuleCode`, `MessageKey`, `FieldPath`, `Permission`,
  `SituationField<,>`, new `TableRef` / `RuleSetRef`), never `string`.
- Analyzer **YFX0100**: a string literal, interpolation or concatenation converted to an identity type outside its
  declaration is an error (tests and the declaring attribute are exempt). Data that really arrives as text (database,
  wire, tenant edits) goes through `TryCreate` / `Parse` at the boundary — no implicit conversion involved.

Recommended: keep the implicit `string` conversions (no break across repos) and enforce through YFX0100. Alternative:
remove them (purest, breaking in Validation, Rules, Cqrs, Messaging, State, UI).

### 3.2 Typed message keys (⚑LZ2, LZ3, LZ4)

Core gets the key types (they are named by Validation, Rules, Cqrs, Notifications, UI):

```csharp
namespace YFex.Localization;

/// Key of a text without arguments. Carries its module and index so the module lookup is an array read.
public readonly struct MessageKey : IEquatable<MessageKey>, ISpanFormattable { public string Value { get; } … }
/// Key whose template takes typed arguments: SaleText.StockOut is MessageKey<decimal>.
public readonly struct MessageKey<T0> { public MessageKey Key { get; } … }
public readonly struct MessageKey<T0, T1> { … }
public readonly struct MessageKey<T0, T1, T2> { … }
```

Declared in C# with the key only; **every language, the main one included, lives in a language file** (approved
2026-11-20, revising the first proposal that put neutral-culture text on the declaration):

```csharp
[Messages]
public static partial class SaleText
{
    [Message("sale.boleto-needs-customer")] public static partial MessageKey BoletoNeedsCustomer { get; }
    [Message("stock.out")]                  public static partial MessageKey<decimal> StockOut { get; }
}
// generated: SaleText.Keys.StockOut = "stock.out" (const, for attribute arguments only)
```

```json
// texts.pt-BR.json / texts.en.json — sections, each checked against its declarations (§3.3)
{ "messages": { "sale.boleto-needs-customer": "Boleto exige cliente identificado", "stock.out": "{field}: só há {0} em estoque" } }
```

Checked cultures come from MSBuild `YFexCultures` (default `pt-BR;en`).

Generator diagnostics: placeholder count ≠ generic arity; a culture file with an unknown key, a missing key (per
culture listed in MSBuild `YFexCultures`), or different placeholders; unused key. The current JSON-only path keeps
working for modules that have no C# declarations.

Key literal: explicit once in the declaration (recommended, same as permission ids: violation codes are stored in audit
and sent on the wire, so a rename must not change them). Alternative: derived from prefix + member name, made safe by
the shipped-identities file (⚑LZ27).

Call sites take the typed key, so the argument is checked by the compiler:

```csharp
w.Add(CloseSale.Fields.CustomerId, SaleText.BoletoNeedsCustomer);
v.Add(CloseSale.Fields.Lines.At(i).Quantity, SaleText.StockOut, available);   // available must be decimal
catalog.Format(SaleText.StockOut, 3m, culture, fieldKey: …);
```

`MessageKey` converts implicitly to `RuleCode` (same string), so violations keep their stored code.

### 3.3 Texts declared where the thing is declared (⚑LZ9, LZ10, LZ20)

Display texts of rules, tables, rule sets, stages, groups, situation fields and permissions live in the sections of the
language files, addressed by the declaration's identity; the generator turns them into the catalog keys of ⚑RX7, so nobody
types a key, and reports entries whose declaration does not exist and declarations without a title:

```json
{
  "messages":    { "stock.out": "{field}: só há {0} em estoque" },
  "situations":  { "sale": { "Installments": "Parcelas", "Tenant": "Empresa" } },
  "ruleSets":    { "sale": { "title": "Venda", "stages": { "items": "Itens" }, "groups": { "Installments": "Parcelamento" } } },
  "rules":       { "promo.l3p2": { "title": "Leve 3 pague 2", "description": "Na compra de 3, a terceira sai grátis." } },
  "tables":      { "sale.installments": { "title": "Tabela de parcelamento" } },
  "permissions": { "sales.close": { "title": "Fechar venda" } },
  "fields":      { "SaleVm.ManualDiscount": "Desconto manual" }
}
```

| Section entry | Catalog key |
|---|---|
| `messages.{key}` | `{key}` |
| `situations.{situation}.{field}` | `{situation}.{field}` (what `Explanation` asks today) |
| `ruleSets.{set}.title` / `.description` | `{set}` / `{set}.description` |
| `ruleSets.{set}.stages.{code}` / `.groups.{name}` | `{set}.stage.{code}` / `{set}.group.{name}` |
| `rules.{code}` / `tables.{code}` `.title` / `.description` | `{code}` / `{code}.description` |
| `permissions.{id}` `.title` / `.description` | `perm.{id}` / `perm.{id}.description` |
| `fields.{Type}.{Path}` | `{Type}.{Path}` (view models, contracts) |

Attribute arguments that must name a message use the generated `Keys` constants.

Field names: one title on the situation or contract member. View models inherit it through the existing
`[SituationSource]` / `[ValidatesAs]` name mapping, so `"sale.Installments" or "Installments"` duplicates disappear.

### 3.4 Lookup performance (⚑LZ7)

- A generated `MessageKey` holds `(string Value, ModuleTexts Module, ushort Index)`. Module lookup =
  `Module.Templates[cultureIndex][Index]`; no hashing, no switch.
- Override layers (dynamic, tenant, app) are `FrozenDictionary<string,string>` per culture and are consulted only when
  the catalog has any (one length check on the hot path).
- Rendering stays the current span renderer (stackalloc first, pooled buffer after). Typed overloads convert
  `T0` to `RawValue` through `RawValue.From` overloads / `IRawValue<T>`, so no boxing.
- RX-P0-style benchmark: format a 1-argument key, built-in layer vs module vs tenant override.

### 3.5 Plurals and choices (⚑LZ8)

`"{0, plural, one {# parcela} other {# parcelas}}"` and `"{0, select, Pix {…} other {…}}"` — the ICU syntax subset, no
nested formats. .NET exposes no CLDR plural rules, so the library ships plural rules for the cultures YFex uses (pt, en,
es first) as generated tables. The generator validates the syntax at build time.

### 3.6 Rules-specific replacements (⚑LZ11–LZ16, LZ28–LZ33)

| Today | Proposed | ⚑ |
|---|---|---|
| `Stage.First("items")` | `[Stage] public static partial Stage Items { get; }`, `[Stage(After = nameof(Items), Title = "Subtotal")]` | LZ11 |
| `[When("Payment")]`, `[Then(LimitMax, "Installments")]` | `[When]` infers the field from the row parameter name; `[Then(LimitMax, nameof(SaleSituation.Installments))]` when names differ | LZ12 |
| `FieldPath.Of("Lines[].Quantity", i)` | `CloseSale.Fields.Lines.At(i).Quantity` (generated nested path structs) | LZ13 |
| `v.Invariant(static (in CloseSale c, ref ViolationWriter w) => { if … w.Add(…) })` | `v.Field(c => c.CustomerId).Required().When(c => c.Payment == PaymentMethod.Boleto).Message(SaleText.BoletoNeedsCustomer)`; general case `v.Rule(c => …, on: c => c.CustomerId, SaleText.X)`; `Invariant` stays for several violations, written `(in c, ref w) =>` (C# 14 modifiers without types) | LZ14 |
| `public static string[] AmountFields => ["Total"]` (⚑RX14) | `[Amount]` on the view-model property that shows the decision's amount; `Explanation.ForAmount()`; `ForField(SaleSituation.Fields.Installments)` instead of `ForField("Installments")` | LZ15 |
| `Set.TryGetTable("sale.installments", …)`, `Trace.For("sale.installments")`, `PublishTable("sale", "sale.installments", …)` | `SaleRules.Tables.Installments` (`TableRef<InstallmentTable.Row>`), `InstallmentTable.Code`, `PublishTable.For(SaleRules.Tables.Installments, …)` | LZ16 |

### 3.7 Placement (⚑LZ5, LZ6)

Recommended: new repository **`YFex.Localization`**.

| Piece | Where |
|---|---|
| `MessageKey`, `MessageKey<…>`, `IMessageSource`, `IMessageCatalog`, `[Messages]`, `[Message]` | YFex core `Localization` (named by many libraries) |
| `MessageCatalog` renderer, override layers, plural rules, culture fallback | `YFex.Localization` (engine; core holds types only) |
| generator + analyzers (catalog, YFX0100 literal identities, missing translations) | `YFex.Localization.Generator` (moves `MessagesGenerator` out of `YFex.Validation.Generator`) |
| translator export / import, pseudo-locale | `YFex.Localization.Tools` (CLI) |
| `IStringLocalizer` bridge | `YFex.Localization.Extensions` (optional) |
| tenant / dynamic stores | adapters at their owners (⚑LZ22) |

Layer order becomes core → Localization → Validation → … Libraries that only read texts keep referencing core
contracts; composition roots reference the engine. Alternative: keep the ~250-line renderer in core and add only the
generator package (smaller move, but behaviour stays in core against the core rule).

## 4. Decisions (⚑, all pending)

| ⚑ | Topic | Proposal (recommended first) | Rejected alternatives | § |
|---|---|---|---|---|
| LZ1 ✅ | Compile-time safety of identities | value objects in every API + generated symbols + analyzer YFX0100 (literal → identity is an error outside declarations and tests); keep implicit `string` conversions | remove implicit conversions (breaking in ~8 repos); value objects only (still run-time failures) | 3.1 |
| LZ2 ✅ | Where texts are declared | C# declares keys only; every language in sectioned files `texts.<culture>.json` (approved 2026-11-20) | JSON-first with generated keys (no argument types); `.resx` (§2) | 3.2 |
| LZ3 ✅ | Key of persisted identities | explicit literal once in the declaration (like permission ids) | derived from member name (rename changes stored codes, unless ⚑LZ27) | 3.2 |
| LZ4 ✅ | Typed arguments | `MessageKey<T0…T2>` from the declaration; generator checks placeholders vs arity in every culture | untyped `MessageKey` + run-time check | 3.2 |
| LZ5 ✅ | New repository | `YFex.Localization` (engine, generator, analyzers, tools, optional bridge) | everything in core; generator stays in `YFex.Validation.Generator` (non-Validation libraries can't use it) | 3.7 |
| LZ6 ✅ | `MessageCatalog` renderer | moves from core to `YFex.Localization` | stays in core (core rule) | 3.7 |
| LZ7 ✅ | Lookup | index into generated per-culture arrays; override layers only when present | static string switch (today); dictionary per lookup | 3.4 |
| LZ8 | Plurals / select | ICU subset, generated CLDR plural tables for used cultures | none (texts like "1 parcelas"); full ICU (third-party) | 3.5 |
| LZ9 ✅ | Field display names | one title on the situation / contract member; VMs inherit via `[SituationSource]` / `[ValidatesAs]` mapping | a title per VM, per contract and per situation (today's duplicates) | 3.3 |
| LZ10 ✅ | Rule, table, rule set, stage, group texts | sections of the language files, addressed by identity → catalog keys of ⚑RX7 (approved with P3 scope 2026-11-20) | hand-written keys in a catalog (today; RX7 as written) | 3.3 |
| LZ11 ⊘ | Stage declaration (**withdrawn 2026-11-21**: stage codes are stored, so `Stage.First("items")` is already the single declaration; any change belongs to the Rules simplification, ⚑LZ28–LZ33) | `[Stage]` partial properties, code from member name (not stored separately from the rule set version) — check in LZ-P0 whether stage codes are persisted; if they are, explicit code once | `Stage.First(nameof(Items))`; literals (today) | 3.6 |
| LZ12 ✅ | Table columns (approved 2026-11-21; attribute names follow ⚑LZ29) | `[When]` / `[Then]` infer the field from the parameter name; `nameof` otherwise; diagnostic when no field matches | literals (today) | 3.6 |
| LZ13 ✅ | Nested field paths | generated `Fields.Lines.At(i).Quantity` for collection members of record element types | `FieldPath.Of("Lines[].Quantity", i)` (today) | 3.6 |
| LZ14 ✅ | Cross-field checks | `.Message(key)` on field chains; `v.Rule(predicate, on:, key)`; `Invariant` for multi-violation cases | only `Invariant` (today's verbosity) | 3.6 |
| LZ15 → LZ28 | Amount fields (supersedes ⚑RX14; **folded into ⚑LZ28 2026-11-21**: outputs get `ForOutput`, inputs `ForField`) | `[Amount]` on the VM property + `ForAmount()`; `ForField(SituationField)` typed | `AmountFields => ["Total"]` string list | 3.6 |
| LZ16 ✅ | Rule set and table handles | generated `TableRef` / `RuleSetRef`; Rules.Admin `PublishTable.For(handle, …)` | string codes in admin calls | 3.6 |
| LZ17 | Analyzer severity | error in infrastructure and domain, warning in samples, off in tests | warning everywhere (ignored in practice) | 3.1 |
| LZ18 | Capabilities localizer (atlas F12) | `ICapabilityLocalizer` becomes an adapter over `IMessageCatalog`; `ResourceKey` → `MessageKey` | keep its own `IStringLocalizer` path | 5 |
| LZ19 | Authentication.Delivery templates (F12) | `ChallengeTemplates` become catalog entries (moves with Notifications) | keep its pt-BR/en/es table | 5 |
| LZ20 | Permission names | `[Permission(Name, Description)]` texts go into the catalog under `perm.{id}`; rename `Name` → `Title` for one vocabulary | literal display names (not translatable, today) | 3.3 |
| LZ21 | UI built-in texts | `IMessageBox` `Ok`/`Cancel`, YForm labels, NavBar texts become YFex.UI catalog keys (en, pt) | English literals (today) | 5 |
| LZ22 | Tenant / dynamic text stores | tenant overrides through YFex.Tenancy scoped overrides; server store in YFex.Data; device cache in YFex.Persistence; dynamic rule texts stay with the rule version (Rules.Dynamic, signed by Offline) | a store inside YFex.Localization | 5 |
| LZ23 | Translator exchange | CLI export / import of XLIFF 2 and flat JSON; pseudo-locale (`qps-ploc`) for UI overflow tests | hand-edited JSON only | 3.7 |
| LZ24 | `IStringLocalizer` bridge | optional `YFex.Localization.Extensions` (ASP.NET hosts, third-party components) | none; or make it the main API | 3.7 |
| LZ25 | Personal data in arguments | arguments of `[Sensitive]` types are masked when a text is formatted for logs or AI; shown in the UI | always shown | 5 |
| LZ26 ✅ | Ambient catalog | DI-provided `IMessageCatalog` + culture per call; remove `ErrorItem`'s static catalog setter | static global catalog (today, `YFex.State/Validation/ErrorItem.cs:55`) | 5 |
| LZ27 | Shipped identities file | generator writes `Identities.Shipped.txt`; removing or renaming a shipped id is an error unless declared as moved (like PublicApiAnalyzers) — enables derived ids later | none (rely on explicit literals, ⚑LZ3) | 3.2 |
| LZ28 ✅ | Rule-set outputs (S1; replaces `DerivedField` and ⚑LZ15) | `[Output]` static methods in the rule set, like `LineBase`: `[Output(After = nameof(Payment))] public static Money InstallmentValue(in SaleSituation s, in RunningTotals<Money> t) => …`; types from the signature (mismatch = compile error), generated ids and handles `SaleRules.Outputs.X`, `decision.Value(…)`, `Explanation.ForOutput(…)`, `ForObject()` | keep `DerivedField.After(stage, "Name", lambda)` (run-time type check, shared mutable object, "belongs to another rule set"); `RuleOutput<T>` fields | 3.6 |
| LZ29 ✅ | Output name (S1a) | derived from the method name — checked 2026-11-21: output names are not stored (Rules.Dynamic specs bind only situation fields, `RuleStoreSchema` keeps cells in the payload; names match only at build time, `[ConstraintsFrom]` / table columns) | always explicit; derived + shipped-identities file (⚑LZ27) | 3.6 |
| LZ30 ✅ | One directive vocabulary (S2; with ⚑LZ12) | table columns use attributes named like the `DirectiveWriter` methods: `[LimitMax(…)]`, `[LimitMin(…)]`, `[Grant(…)]`, `[Decrease(AdjustTarget.Line)]`, `[Deny(Keys.X)]`; `DirectiveKind` stays internal for the engine and Rules.Dynamic | `[Then(DirectiveKind.LimitMax, "Installments")]` (today) | 3.6 |
| LZ31 ✅ | Typed messages in rules (S3; Rules side of ⚑LZ14) | `d.Deny(path, SaleText.X)`, `d.Warn(path, SaleText.StockLow, left)`, `d.RequireApproval(permission, path, SaleText.NeedsManager, max)` with `MessageKey<T>` argument types; `RawValue` overloads leave the authoring surface, stay for Rules.Dynamic | `RuleCode` + `RawValue` only (today) | 3.6 |
| LZ32 ✅ | Typed field handles in Rules (S4) | `RuleParameter(…, editableAt: [SaleSituation.Fields.Tenant])`; attribute arguments keep `nameof` (constants required) | `nameof` string arrays (today) | 3.6 |
| LZ33 ✅ | Rule method shapes (S5) | leave as is: marker `IRule<TS>` + generator check YFR0010 | `static abstract` / `static virtual` members (two interfaces for the with/without `RuleContext` shapes) | 3.6 |
| LZ34 ✅ | Rules API documentation (user requirement 2026-11-21) | every public attribute, property, method and type of YFex.Rules gets a short `<summary>` and, where usage isn't obvious, an `<example><code>` (all-projects.md); the Rules generator writes `///` docs on every member it emits — `Fields.X`, `Outputs.X`, `Tables.X`, stage and group handles — saying what it is, where it comes from (`[Output]` method, `Stage.First("items")`, table code) and its title from the pt-BR file when there is one; partial members keep the doc on the user's declaring part | docs only on hand-written types | 3.6 |

## 5. Ecosystem fit

| Library | Outcome | Role / reason | ⚑ |
|---|---|---|---|
| YFex core | **used, better placement** | holds key types and contracts; renderer moves out | LZ1, LZ5, LZ6 |
| YFex.Validation | **used, lacks feature** | typed `Add` overloads, `.Message(key)`, `Rule(...)`, nested paths; its `MessagesGenerator` moves out | LZ4, LZ13, LZ14 |
| YFex.Rules | **used, lacks feature** | texts on declarations, `[Stage]`, `[When]` inference, `TableRef`, `ForField(SituationField)`, `ForAmount()`; `RuleMessages` becomes declared keys | LZ10–LZ16 |
| YFex.Expressions | not a fit | expression text is formula source, not display text | — |
| YFex.State | **fits, lacks feature** | `[Amount]` VM attribute, `[SituationSource]` field-title inheritance, `ErrorItem` catalog via DI | LZ9, LZ15, LZ26 |
| YFex.Persistence | fits | device cache of tenant texts | LZ22 |
| YFex.Data | fits | server store of tenant texts | LZ22 |
| YFex.Cqrs | used | formats ProblemDetails at the edge with the typed catalog; error codes become `MessageKey`s | LZ1 |
| YFex.Messaging (Rules.Admin) | **fits, lacks feature** | `PublishTable.For(handle, …)`, `GetRuleDefinition` by `TableRef` | LZ16 |
| YFex.NavigatR, YFex.Mvvm | fits (check in LZ-P0) | page titles and route names are display / identity strings | LZ1, LZ21 |
| YFex.UI | **fits, lacks feature** | built-in texts as catalog keys; `YForm.Labels` already uses `IMessageCatalog` | LZ21 |
| YFex.Authorization | **used, lacks feature** | the catalog pattern to copy; permission names into the catalog | LZ20 |
| YFex.Authentication | **better placement** | Delivery challenge templates → catalog entries (F12) | LZ19 |
| YFex.Capabilities | **better placement** | own localizer over `IStringLocalizer` → adapter over `IMessageCatalog` (F12) | LZ18 |
| YFex.Notifications | used | already renders `{key}.subject/body` through `MessageCatalog`; gets typed template keys | LZ4 |
| YFex.Tenancy | fits | tenant text overrides as scoped overrides | LZ22 |
| YFex.Offline | used | dynamic rule texts travel with signed rule data | LZ22 |
| YFex.Privacy | fits | mask `[Sensitive]` arguments outside the UI | LZ25 |
| YFex.Audit | used (unchanged) | stores codes + args, never text | — |
| YFex.Logging | not a fit | logs keep code + args, no localization | — |
| YFex.Composer (design) | fits | user-authored texts are a dynamic layer | LZ22 |
| YFex.Search | fits | per-culture keywords come from the catalog (with Capabilities) | LZ18 |
| YFex.Automation | fits | AI agents read texts in the invariant culture + keys | LZ25 |
| YFex.Licensing | fits | feature names / unavailable reasons as declared keys | LZ10 |
| YFex.Jobs | fits (check) | job display names in admin screens | LZ10 |
| YFex.Time, Cryptography, Networking, Installations, Resilience, Orchestration, EventSourcing, Actors, Files, Web, System, Profiling, Security, Devices, Backup, Hosting, Cluster, Provisioning, Installer, CustomLogic, Xml | not a fit | no user-facing texts beyond error codes that already go through Cqrs / Validation; identities there follow the rule but need no library change | — |

### Requests to other libraries

| Library | Change | Breaking | ⚑ | Phase |
|---|---|---|---|---|
| YFex core | `MessageKey` types, `[Messages]`/`[Message]`; renderer moves out | yes (namespace of `MessageCatalog` construction) | LZ1, LZ6 | LZ-P1 |
| YFex.Validation | typed `Add`, `.Message`, `Rule`, nested paths; generator move | additive + generator move | LZ4, LZ13, LZ14 | LZ-P4 |
| YFex.Rules | declaration texts, `[Stage]`, `[When]` inference, refs, typed `ForField`, `ForAmount` | yes (stages, `ForField`) | LZ10–LZ16 | LZ-P3, LZ-P4 |
| YFex.State | `[Amount]`, title inheritance, DI catalog | additive / removal of static setter | LZ9, LZ15, LZ26 | LZ-P4 |
| YFex.Messaging | `PublishTable.For` | additive | LZ16 | LZ-P4 |
| YFex.Authorization | permission texts to catalog, `Name` → `Title` | yes (rename) | LZ20 | LZ-P3 |
| YFex.Capabilities, YFex.Authentication | adapters / templates (F12) | internal | LZ18, LZ19 | LZ-P6 |
| YFex.UI | built-in texts | additive | LZ21 | LZ-P6 |
| validation/14 (RX plan) | RX7 revised by LZ10, RX14 superseded by LZ15 | plan change | LZ10, LZ15 | before RX-P1 |

## 6. Phases

| Phase | Content | Done when |
|---|---|---|
| **LZ-P0** Inventory | YFX0100 in report-only mode over every repo: literal identities and literal UI texts per repo; check whether stage codes and field names are persisted | a count per repo and per identity kind in this folder; LZ11 settled |
| **LZ-P1** Core types ✅ 2026-11-20 | `MessageKey`, `MessageKey<…>`, attributes, typed `IMessageCatalog` overloads, `MessageKey → RuleCode` | builds; existing string APIs untouched |
| **LZ-P2** Repo + generator ✅ 2026-11-20 | `YFex.Localization` (renderer moved), generator: C# declarations + culture JSON, index lookup, diagnostics; JSON-only path kept | the sale sample's `SaleMessages` switch is gone; benchmark not slower than today |
| **LZ-P3** Declaration texts ✅ Rules part 2026-11-20 (Authorization consumption waits on ⚑LZ20) | Rules / Authorization `Title`, `Description`; field titles + inheritance | `Explain` shows the same pt-BR texts with no hand-written key |
| **LZ-P4** Typed call sites | Validation, Rules, State, Messaging APIs of §3.6 | the sample compiles with zero identity literals outside declarations |
| **LZ-P5** Plurals + translators | ICU subset, plural tables, export/import CLI, pseudo-locale | "1 parcela / 3 parcelas"; round-trip XLIFF |
| **LZ-P6** Consolidation | Capabilities, Delivery, UI texts, `IStringLocalizer` bridge (F12) | one catalog system in the ecosystem; F12 closed |
| **LZ-P7** Enforcement | YFX0100 at ⚑LZ17 severities; shipped-identities file if ⚑LZ27 | Release build of every repo clean |

Tests close each phase (00-start-here §3); AOT smoke for the new repo.

## 7. Log

- **2026-11-20** — approved: ⚑LZ1–LZ7, LZ2/LZ10 revised (keys in C#, every language in sectioned files), `Keys` constants,
  cultures `pt-BR;en`, engine moves out of core with every consumer updated. Scope of the session: LZ-P1 + LZ-P2 + LZ-P3.
- **2026-11-20, built (uncommitted)**, LZ-P1 + LZ-P2 + LZ-P3:
  - **Core** (`YFex/src/YFex/Localization`): `MessageKey` (value object; generated keys carry module + index; equality by
    text), `MessageKey<T0>`, `<T0,T1>`, `<T0,T1,T2>`, `IMessageModule`, `[Messages]`, `[Message]`, key overloads on
    `IMessageCatalog` (default implementations use the key text), typed `Format` extensions (`MessageCatalogFormatting`).
    `MessageCatalog` removed from core; its tests moved. Validation: `RuleCode` converts from `MessageKey`.
  - **Repo `YFex.Localization`** (`localization.slnx`, git initialised, no commit): engine `MessageCatalog` (index lookup for
    a key's own module, layers above still override by key); generator (sections, legacy flat files, `[Messages]` emission
    with `Keys`, YFL0001–YFL0013, docs/language-files.md); 47 tests; AOT smoke (exit 0, zero IL warnings); benchmark.
  - **Benchmark** (61-key module, 1 argument, TryFormat into a span, ShortRun): key text 18.6 ns, generated key by index
    17.4 ns (0.93), with two override layers above 18.5 ns; 0 bytes allocated in all three.
  - **Moved from YFex.Validation.Generator:** `MessagesGenerator` and `MissingMessageCheck` (YFV0040 → YFL0002, YFV0041 →
    YFL0001); their tests moved from YFex.State's `YFex.Validation.Generator.Tests`.
  - **Consumers updated** (reference `YFex.Localization`): Validation.Testing, Validation smoke, Validation tests (+ generator
    analyzer), State, Notifications, YFex.Avalonia, Rules tests, Rules.Dynamic tests, `_ignore/SaleScenario`.
  - **P3:** sections `situations`, `ruleSets` (title, description, stages, groups), `rules`, `tables`, `permissions`, `fields`
    map to the ⚑RX7 keys and are checked against the project's declarations (YFL0006 unknown entry, YFL0011 missing title).
    Rules tests translate the doc-12 sale (`Messages/texts.pt-BR.json`): `Explain` shows the file's titles.
  - **Verified:** Release builds of Localization (0 warnings), Validation (0), State, Rules, Notifications (projects one by
    one; the solution restore fails on an existing NU1510 in Authentication.Delivery), YFex.Avalonia + tests + gallery;
    tests: Localization 47, core 316, Validation 62, State 381 + Validation.Generator 50, Rules 127 (all repos), Notifications
    11, Avalonia Forms 61. Warnings seen in State, Rules, Notifications and UI builds already existed.
  - **Found, not caused by this work:** the YFex.Validation AOT smoke crashes at start (`InvalidCastException: a Decimal value
    is not a money` in the generated `Order.Rules.Total` initializer, `[Range(0, 100000)] Money`); reproduced on the committed
    HEAD of core + Validation in a scratch worktree. JIT run passes.
  - **Not done, waiting on decisions:** Authorization reading permission titles from the catalog and `Name` → `Title`
    (⚑LZ20); typed call sites in Validation/Rules (LZ-P4, ⚑LZ13–LZ16); plurals (⚑LZ8); stage declaration order (⚑LZ11, still
    `Stage.First("…")`); YFX0100 literal analyzer (LZ-P7).
- **2026-11-21** — decisions for LZ-P4: ⚑LZ12, LZ13, LZ14, LZ26 approved. ⚑LZ11 withdrawn (stage codes are stored;
  the Rules simplification decides any change). ⚑LZ15 folded into ⚑LZ28. The Rules simplifications from the Rules review
  (S1–S5) recorded as ⚑LZ28–LZ33, pending. Still pending for P4: ⚑LZ9 (examples given: one title on the situation member,
  view models inherit through `[SituationSource]` / `[ValidatesAs]`, a `fields` entry overrides per type, YFL0011 when a
  sourceless VM field has none), ⚑LZ16, ⚑LZ28–LZ33. LZ-P4 is one phase covering both plans, built in this order:
  (1) LZ13 + LZ14 + LZ31 + LZ26, additive; (2) LZ32 + LZ16, handles; (3) LZ12 + LZ30, table attributes, breaking;
  (4) LZ28 + LZ29, outputs, breaking, largest; (5) LZ9.
- **2026-11-21 (later)** — approved ⚑LZ9, LZ16, LZ28, LZ29 (derived names; output names checked not stored), LZ30–LZ33.
  New ⚑LZ34 approved: Rules XML docs, hand-written and generated, with examples. Every LZ-P4 decision is now taken.
