# YFex Forms — domain fields to UI

> Status: **proposal** (2026-11-18); **FM-P0 spike and FM-P1 layout BUILT 2026-11-18** (committed 2026-11-19; **FM-P2 built and committed 2026-11-19**; [03](03-fm-p0-spike.md), [04](04-fm-p1-layout.md)). Decisions **⚑FM1–⚑FM53**; the user answered ⚑FM10, FM17 (drafts), FM20, FM23, FM25, FM26, FM27, FM35 and FM41 in the second review (2026-11-18), the rest are pending (⚑FM51–⚑FM52 added for YFex.Inference). Part 1 = forms for model fields;
> part 2 = the CRUD screen pattern (outline only). Implementation repo: `YFex.UI` (framework-neutral core + Avalonia first).

| File | Holds |
|---|---|
| [01-design-and-examples](01-design-and-examples.md) | what exists, the four layers, metadata options compared, model and view code, sizes, presets, conditions, the old `Form` branch review, ideas, pitfalls |
| [02-crud-pattern](02-crud-pattern.md) | YFex.Farma findings, CRUD view model and page, tabs on demand, a model as a whole page, part-2 questions |
| [03-fm-p0-spike](03-fm-p0-spike.md) | FM-P0 spike: what was built, 30 checks on JIT and AOT, 60-field timings, findings S1–S7 |
| [04-fm-p1-layout](04-fm-p1-layout.md) | FM-P1: form flow, sections, the pickers built for it (YDatePicker, YCalendar, YComboBox), gallery, tests, findings |
| [05-fm-p2-shapes](05-fm-p2-shapes.md) | FM-P2: groups, child models, modes, conditions + analyzers, editor registry, catalog labels, gallery examples with code; findings P2-1–P2-5 |
| [06-fm-p3-validation-and-layout](06-fm-p3-validation-and-layout.md) | FM-P3 part 1 (validation wiring, required mark, focus first invalid) and the reworked layout (uniform grid, rows fill, sections on the grid, phone widths); findings P3-1–P3-5 |
| [07-fm-p4-authoring](07-fm-p4-authoring.md) | FM-P4 (built 2026-11-19): `YField`, `YFormRest`, overrides, slots, markup, app editors; user additions: line break after a field, open sections with errors, show validation after a typing pause (⚑FM54–FM59) |

Related plans: [Search + Capabilities](../search-capabilities/README.md) (separate session, owns lookups and command capabilities), [YFex.Domain value types](../../../../YFex.Domain/docs/value-types-plan.md) (⚑VT1–VT6, duplicate types).

## 1. In one paragraph

A UI model (`CustomerEditModel`, a YFex.State `StateObject` that `[ValidatesAs<…CreateCommand>]`) gets `[Form]`. A source generator emits a static, framework-neutral **form shape**: fields in declaration order with typed get/set delegates, the editor kind chosen at compile time, size, group, modes and visibility rules. Defaults come from the **value type's own input** (`YCpfCnpjInput`, `YMoneyInput`…: mask, keyboard, width; registered once as the type's editor, ⚑FM5/⚑FM47), then from the declaration (nullability, `Id` types, enums, child models, lists), and only then from one optional `[Field(…)]` attribute. `YForm` (Avalonia) renders the shape on a 24-unit flow with at most 1–3 columns and six size presets, sections for child models, and existing `Y` inputs as editors. Validation, Rules decisions, masks and permissions are read from their owners (`IValidationSource`, `[Sensitive]`, Authorization), never re-implemented. Screens can be fully automatic, hand-placed with editors still chosen for them, or fully manual — all three in the same layout panel.

```xml
<y:YForm Source="{Binding Customer}" MaxColumns="3" />
```

A record has **three models, each with its own boundary** (user, 2026-11-18): a **row model** for the list, an **edit model** for the form, and a **details model** for the read-only screen (which carries more than the form: purchase history, appointments, convênio). Each has its own shape; nothing is shared between them by default.

## 2. Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md). Outcomes per [ecosystem-fit §1](../../agent/rules/ecosystem-fit.md).

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core (`src/YFex`) | used | value objects, `EnumOption`, `Paths`, `[Sensitive]`, localization catalogs; no change needed (⚑FM5 ✅: no type-level input hints, each type's own input carries them) | FM5 |
| core `Localization` | used | labels, help, section headers by key convention; atlas F12 still open | FM21 |
| core `Diagnostics` | used | probe for form build time (perf budget, R9) | — |
| YFex.Validation | used; **lacks a feature**; **better placement** | `IValidationSource`, `FieldConstraints`, `[ValidatesAs]`, `TypeShape`/JSON schema. Overlaps: typed field access vs `[RecordAccess]` (C3), and validation/08 (Phase 5) is the control half of this design | FM30, FM33 |
| YFex.Rules | used | dynamic constraints and options from decisions through `IValidationSource`; approval flow from validation/08 §8 | — |
| YFex.Expressions | fits later | formulas for computed/default fields if conditions ever become data (Composer) | FM16 |
| YFex.Composer (design) | **better placement** | design only ([composer-design](../composer-design.md), proposal 2026-09-30, no repo): end users compose screens and logic at run time from a stored description. Forms is the developer-side counterpart: the old `Form` branch designer and runtime layout descriptors belong in Composer, and Composer's planned `DisplayHints` should be the same type as Forms' hints so both render one shape | FM16, FM29 |
| YFex.State | used | `[Observable]`, `[Trackable]` (dirty, change summary), undo, `StateList<T>`, `IActivatable`, `[Persist]` drafts | FM17 |
| YFex.Mvvm | used (part 2) | `EditorViewModel<TModel>` and the other stubs become the CRUD bases; controls never reference it | FM40 |
| YFex.NavigatR | used (part 2) | leave guard for unsaved changes, tab routes created on demand | FM43 |
| YFex.UI (this repo's home) | used | `YForm`, `YField`, `YFormSection`, panel, editors registry; `YFex.UI.Public` for overlays | FM1, FM7 |
| YFex.Cqrs | used | save = dispatch the command from `ToCreateCommand()`; server violations back to fields | FM45 |
| YFex.Messaging | used (part 2) | "saved offline, waiting to send"; record-changed-by-someone-else notices | — |
| YFex.Persistence | used | per-user form preferences (collapsed sections, columns, detail placement), drafts | FM17, FM46 |
| YFex.Data | not a fit | the form never touches SQL; rows map to contracts | — |
| YFex.Backup | not a fit | no UI-model concern | — |
| YFex.Tenancy | fits | per-tenant field overrides (hide, relabel, required) as scoped settings | FM22 |
| YFex.CustomLogic | fits | a tenant-specific editor or form configuration selected by scope | FM22 |
| YFex.Authentication | not a fit | identity only; who may see a field is Authorization | — |
| YFex.Authorization | used | typed permissions + `IAuthorizationContext.HasPermission` feed the field conventions; a member-path policy is optional, compared in 01 §13 | FM18 |
| YFex.Licensing | fits through Authorization | a field or section of an unlicensed module hidden via the availability hook | FM18 |
| YFex.Capabilities | fits; **own plan** | form and CRUD commands as capabilities, AI fill; contracts designed in the separate Search + Capabilities plan | FM23, FM44 |
| YFex.Search | fits; **own plan** | lookup editor for id-typed fields; the "pick one record of type X" provider is designed in the separate Search + Capabilities plan | FM20 |
| YFex.Privacy | used | `[Sensitive]` masks on the details screen and read-only fields; reveal needs permission | FM19 |
| YFex.Audit | fits | record who revealed a sensitive value; change diff on save | FM19, FM26 |
| YFex.Actors | fits | created/updated-by footer with names at read time | FM26 |
| YFex.Logging | used via core probes | build-time and error probes only | — |
| YFex.Time | used | date defaults and "today" from `TimeProvider`, validity-period editor | — |
| YFex.Profiling | fits later | consent-gated field-usage signals (skipped fields, error rates) to improve forms | FM32 |
| YFex.System | not a fit | machine telemetry, no form concern | — |
| YFex.Security | not a fit | inspection tooling; sensitive-field handling is Privacy's | — |
| YFex.Devices | fits | scale weight into a `Mass` field, barcode into a code field | FM24 |
| YFex.Automation | fits | stable `AutomationId = field path` on generated editors | FM25 |
| YFex.Files (design) | fits later | attachment field editor over a file store | FM13 |
| YFex.Offline | not a fit directly | offline status reaches the form through Messaging (part 2) | — |
| YFex.Web, Cryptography, Networking, Installations, Resilience, Orchestration, EventSourcing, Jobs, Cluster, Provisioning, Hosting, Installer, Notifications, Xml | not a fit | no UI-model or field concern; toasts and dialogs come from `YFex.UI.Public` | — |
| YFex.Inference (proposal, [inference](../inference/README.md)) | fits (optional) | paste/AI fill beyond value types (names, addresses), suggested defaults, duplicate warnings; rules-only fill works without it | FM51, FM52 |
| YFex.Domain / YFex.Farma (domain) | consumers | value objects get their own inputs in a `{DomainPackage}.Avalonia` package in their repo (`YFex.Entity.Avalonia`, FM47); duplicates settled in the domain plan (⚑VT1–VT2, atlas F48) | FM31, FM47 |

Outside libraries: PolyType (type-shape generator) and CommunityToolkit-style form generators were studied for the pattern only; adopting either is rejected in ⚑FM2.

## 3. Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Validation | decide whether typed field accessors join `[RecordAccess]` (C3) or stay in the Forms generator; finish validation/08 constraint projection (`IConstraintTarget`) for every input | no | FM30, FM33 | FM-P3 |
| YFex.Authorization | none now; member-path policies only if ⚑FM18 picks "with" | no | FM18 | — |
| YFex.Search + YFex.Capabilities | lookup provider contract; commands as capabilities — owned by the separate plan | no | FM20, FM23, FM44 | FM-P5 |
| YFex.Composer (design) | share `DisplayHints` with Forms' hints; take the old `Form` branch designer and descriptor merge rules as input | design only | FM16, FM29 | — |
| YFex.Mvvm | fill the stub bases (part 2) | yes for the stubs (unused today) | FM40 | part 2 |
| YFex.Domain | resolve duplicate types (⚑VT1–VT2); new package `YFex.Entity.Avalonia` with the inputs for its types (`YCpfInput`, `YCnpjInput`, `YCpfCnpjInput`…), which registers them as the editors of their types | VT1 breaking for Farma `Customer` | FM5, FM31, FM47 | FM-P2 |
| YFex.Inference (proposal) | span finder and judgments with abstain; suggestion shape in `YFex.UI.Public` (⚑UI116) | no (new) | FM51, FM52 | FM-P5 or later |

## 4. Decisions

Recommended option first. Where explained: column "See". ✅ = answered by the user; the rest are pending.

| ⚑ | Topic | Proposal | Rejected alternatives | See |
|---|---|---|---|---|
| FM1 | Placement | `src/Core/YFex.UI.Forms` (framework-neutral attributes, enums, shape types, `IEditorFactory`) + `YFex.UI.Forms.Generator` in the YFex.UI repo; Avalonia controls in `YFex.Avalonia` | new repo `YFex.Forms`; inside YFex.Mvvm (controls may not reference it); inside Validation | 01 §2 |
| FM2 | Metadata mechanism | source generator → static `FormShape<T>` + typed visitor | runtime reflection; scaffolding only; runtime JSON (Composer); depending on PolyType | 01 §3 |
| FM3 ✅ | Annotation surface | **Agreed 2026-11-19 (user, FM-P2).** type hints → conventions → one `[Field]` (+ optional `[FieldGroup]`) → a static `ConfigureForm` block, all **inside the model** | one attribute per aspect; a separate `IFormConfiguration<T>` class (rejected by the user 2026-11-18) | 01 §4.1, §4.3 |
| FM4 | Where annotations live | UI models only, never public contracts | contracts with UI attributes | 01 §4.1 |
| FM5 ✅ | Type-level input hints | **decided 2026-11-19 (user): none.** Every value type gets its own input (⚑FM47), and that input owns mask, keyboard and default field width; the package that owns the input registers it as the editor of its type | core `[InputHints]` + `InputHintsOf<T>` (dropped: duplicated what the type's input knows); static abstract interface on every VO; per-app registration only | 01 §4.8 |
| FM6 | Layout | unit flow (6 units per column: Tiny 2, Small 3, Medium 6, Large 12, Full the row; a field never straddles a column), `MaxColumns` 1–3 (default 2), width tokens applied only in a one-column form (with more columns fields fill their span so columns line up), hysteresis 40 px, row-major order. **Built in FM-P1** | strict column grid; free flex; column-major | 01 §4.4, §7 |
| FM7 ⏸ | Field wrapper | **Later, with `YForm.Label` (04 P2) (user, 2026-11-19).** none for `Y` inputs (they own label + errors); `YHeaderControl` for editors without a label; `YField` is a placeholder replaced by the editor | a `YFormField` wrapper around every editor | 01 §5.2, pitfall 5 |
| FM8 ✅ | Editor choice | **Agreed 2026-11-19 (user, FM-P2).** compile-time visitor dispatch + app overrides per type in generic statics | `DataTemplate` per type; registry by `Type` with `Activator` | 01 §4.7, §5.5 |
| FM9 | Binding generated fields | generated accessors + a small field binding on `INotifyPropertyChanged`; spike Avalonia compiled bindings built in code as an alternative | reflection `{Binding}` with string paths | 01 §4.7 |
| FM10 ✅ | Modes | **no View mode.** Viewing a record is the details model's own screen (01 §9.2). The edit model keeps only `Create` / `Edit`: `[Field(EditableIn = FormModes.Create)]` for fields fixed after creation; whether Create and Edit share one edit model is ⚑FM53 | one model with Create / Edit / View modes (rejected by the user 2026-11-18) | 01 §9.2 |
| FM11 ✅ | Conditions | **Decided 2026-11-19 (user): the conventions are documented where model authors look (`[Form]` XML docs, 01 §4.6) and analyzers keep them from being forgotten: an `Is{X}Visible`/`Can{X}Edit` member with no field `X` (typo), `VisibleWhen`/`EditableWhen` naming a missing or non-bool member.** `Is{Field}Visible` / `Can{Field}Edit` conventions with inferred dependencies; `[Field(VisibleWhen = nameof(…))]`; `ConfigureForm` block in the model | string expressions in attributes; separate configuration class | 01 §4.6 |
| FM12 ✅ | Child models | **Decided 2026-11-19 (user): building a section only when it is first shown is an option (`[FieldGroup(BuildWhenShown = true)]`; `YFormSection.IsBuilt` tells); default = built with the form.** collapsible section with start border, built when first opened; options `Inline`, `Card`, `Dialog` | always inline | 01 §8 |
| FM13 | Collections (later) | simple elements → `YDataGrid` with new-item row; complex → cards; reorder with `YDrag`; attachments with YFex.Files later | grid only | 01 §8 |
| FM14 | Visual presets | closed `FieldPreset` set (`Standard`, `Emphasis`, `Code`, `Multiline`, `Secret`, `Inline`) + form `Density` | free visual attributes | 01 §4.5 |
| FM15 | `Form` branch | keep the unit-flow and hysteresis algorithms and the group behaviour, rewritten on tokens; drop designer, descriptors, JSON persistence, reflection | merge the branch; ignore it | 01 §6 |
| FM16 | End-user designer | belongs to Composer; Forms may later add "personalize my form" (hide/reorder, per user) | port the old designer into Forms | 01 §6 |
| FM17 | Per-user preferences and drafts | **drafts: yes (user 2026-11-18).** YFex.Persistence / State `[Persist]`; still open: `[Sensitive]` fields sealed or left out of the draft | old `ILayoutPersistence` JSON files | 01 §6, §9.6 |
| FM18 | Field permissions | **without** an Authorization change: `Can{Field}Edit` / `Is{Field}Visible` from `HasPermission`, plus a `FieldAccess` helper; the query removes what may not be seen. "With" (member-path policies in Authorization) kept for comparison, revisit when rules repeat across screens or operators must change them | member policies now; form-side permission attributes | 01 §13 |
| FM19 | Sensitive data | masked on the details screen and in read-only fields, reveal button gated by permission and audited | always clear; never revealable | 01 §9 |
| FM20 ✅ | References to other records | **models carry what they need, never a bare id** (user 2026-11-18): a reference property is a small summary type (`SupplierRef { Id, Name, Document }`) the query fills, so showing it needs no extra lookup. Only *changing* it needs a picker, which comes from the Search + Capabilities plan | raw id fields resolved by the form; combo boxes loading all rows | 01 §9.9 |
| FM21 ✅ | Labels | **Agreed 2026-11-19 (user, FM-P2).** catalog keys by convention (`Customer.Name`), humanized name as debug fallback with a warning | labels in attributes | pitfall 13 |
| FM22 | Tenant overrides | small overrides as Tenancy scoped settings; bigger ones through CustomLogic or Composer | no per-tenant change | §2 |
| FM23 ✅ | Paste and AI fill | **yes (user 2026-11-18)**: plain paste fills the value types it finds; a separate **"Colar com IA" (AI paste)** option sends the text through the shape's JSON schema; both show a preview the user applies (engine in ⚑FM51) | none | 01 §9.8 |
| FM24 | Device input | fields accept YFex.Devices input (scale, scanner) by type | none | 01 §9.11 |
| FM25 ✅ | Automation ids | `AutomationId = field path` on generated editors (user: ok, 2026-11-18) | none | 01 §9.10 |
| FM26 ✅ | Change summary, audit, actors | the change summary before save is **optional**, off by default, turned on per form (user 2026-11-18); the save diff to Audit and the created/updated-by footer do not depend on it | always shown | 01 §9.7 |
| FM27 ✅ | Shape reuse | **one model per boundary** (user 2026-11-18): row, edit and details models each get their own shape; each control reads the shape of the model it is given (grid ← row model, form ← edit model, details screen ← details model). No model is shared between list, form and details | one model driving grid, form and details card | 01 §9.1 |
| FM28 | Eject | dev command (or "Copy as XAML" in debug builds) writes the `.axaml` an automatic form corresponds to, as a starting point for a hand-made screen; optional, may be dropped | none | 01 §3 C, §9.3 |
| FM29 | Shared display hints | one hints type for Forms and Composer `DisplayHints` | two types | §2 |
| FM30 | Field accessors vs `[RecordAccess]` | Forms generator emits typed accessors now; revisit merging when C3 is built | wait for C3; build C3 first | §3 |
| FM31 | Duplicate value types | decided in the domain plan before adding hints: ⚑VT1 ✅ remove `Email` (user, 2026-11-19); ⚑VT2 ✅ remove `Telefone`, Brazilian rules as domain extensions on `PhoneNumber` (user, 2026-11-19) | hints on both | pitfall 17 |
| FM32 | Profiling signals | later, consent-gated | none | §2 |
| FM33 | Validation Phase 5 | Forms phases complete validation/08's control half (constraint projection, summary, pseudo-classes) in YFex.UI | a separate Phase 5 project | §3 |
| FM34 | Form profiles | `[Field(Profiles = "quick")]` + `YForm.Profile`, aligned with Validation `ProfileId` | separate quick models | 01 §9.4 |
| FM35 ✅ | Keyboard data entry | Enter-to-next (option), Ctrl+S, focus first invalid on save (user: ok, 2026-11-18) | none | 01 §9.5 |
| FM47 ✅ | Inputs for value types | **rule:** every value type gets its own input, designed from the type (mask, paste, copy, live check, display mask, keyboard, default width); core types → YFex.Avalonia; domain types → **a separate UI package per domain package, in that domain's repo, named `{DomainPackage}.Avalonia`: `YFex.Entity.Avalonia` (YFex.Domain), `YFex.NFe.Avalonia` (YFex.Fiscal)… (user, 2026-11-19)**. Each references YFex.Avalonia and its own `.Public` types; YFex.UI never depends on them | plain text box fallback; domain inputs in YFex.Avalonia (breaks the dependency rule); Brazilian documents in core; `YFex.Avalonia.Domain*` packages in the YFex.UI solution (makes YFex.UI depend on domain repos) | 01 §11 |
| FM48 | Input mask source | derived from a positional `[Sensitive]` mask (`*` and `#` = slots); otherwise the type's own input defines its mask | always a separate input mask | 01 §4.8 |
| FM49 | A model as a whole page | `[FormPage]` + generic `FormPageViewModel<TModel>` / `YFormPage`; layouts Sections, Tabs, Wizard; `[FormCommand]` methods on the command bar | generated page per model; hand-written pages | 02 §4 |
| FM50 | Showing failures | message under each field; a summary at the bottom of each section and of the form (click to focus); "why?" from the Rules `Explanation`; error dots on tabs and collapsed sections | one summary at the top only | 01 §12 |
| FM51 | Paste / AI fill engine (detail of ⚑FM23; [inference](../inference/README.md) ⚑IF8) | per field `IExtractable<T>` first, `find_record` for ids (⚑SK29), then YFex.Inference spans for names and addresses; shown as a **paste preview** (field diff, Apply) | a model fills the whole form; value types only | inference 01 §5 |
| FM52 | Suggestions and duplicates ([inference](../inference/README.md) ⚑IF35, ⚑IF40) | optional suggested defaults (ghost text, confidence chip from YFex.UI ⚑UI116) for fields with an option table (category, account, NCM); duplicate warning banner from deterministic blocking + a local "same record?" judgment; merging is the user's command | no suggestions; auto-merge | inference 01 §5, §9 |
| FM53 ⏸ | Create vs Edit model | **Later (user, 2026-11-19).** one edit model for Create and Edit (`EditableIn = Create` for fields fixed after creation); a separate create model only when creating asks for different fields | always a create model and an edit model (three models become four) | 01 §9.2 |
| FM54–FM59 | FM-P4 authoring and the user's additions | listed in 07 §4 | — | 07 |
| FM40–FM46 | CRUD pattern (part 2) | listed in 02 §5 (⚑FM41 ✅ answered: separate row model) | — | 02 |

## 5. Phases (proposed)

Each step builds; tests and AOT smoke at the end of each phase, per the working method.

| Phase | Content | Done when |
|---|---|---|
| FM-P0 Spike ✅ | generator for one model with five editor kinds (`Text`, `Parsed`, `Number`, `Choice`, `Date`) + bare `YForm`; AOT publish; build time of a 60-field form | **done 2026-11-18**: 30/30 checks on JIT and AOT, no Forms IL warning, forms layer adds no measurable time; ⚑FM1, 2, 8, 9 work as proposed ([03](03-fm-p0-spike.md)) |
| FM-P1 Layout ✅ | `YFormPanel` (port of the unit flow + hysteresis), `YFormSection`, attached `YForm.Size/NewRow` (`Label` not built, see 04), sizes and columns, gallery page "Forms"; plus `YDatePicker`, `YCalendar`, `YComboBox` from the UI plan (user, 2026-11-18) | **done 2026-11-18**: Forms page checked at 280 / 320 / 760 / page width, light and dark; 22 tests + 5 stress runs; spike 30/30 on JIT and AOT ([04](04-fm-p1-layout.md)) |
| FM-P2 Shapes ✅ | **built 2026-11-19, committed `5b9c0ec` ([05](05-fm-p2-shapes.md))**: full generator: conventions, `[Field]`, `[FieldGroup]`, child models (optional build-when-shown, ⚑FM12), modes, editor-per-type registry (packages register their inputs), labels by key, condition analyzers (⚑FM11); gallery Forms examples with their source | a customer form shaped like Farma's renders automatically in the gallery (Farma is reference only, not built) |
| FM-P3 Validation (part 1 ✅ 2026-11-19, [06](06-fm-p3-validation-and-layout.md); summaries ⚑FM50 and `YDetails` ⚑FM19 wait) | constraint projection to every input (validation/08), summary, focus first invalid, read-only `YDetails` with masks | required/limits/errors/rules decisions shown without view code |
| FM-P4 Authoring ✅ (built 2026-11-19, [07](07-fm-p4-authoring.md)) | `YField`, `YFormRest`, `FieldOverride`, `FieldSlot`, fluent markup, app editor overrides; plus `EndsRow`, sections open on errors, YFex.State `ShowErrors.AfterPause` (⚑FM54–FM59) | the three authoring styles produce the same layout |
| FM-P5 Integrations | per decisions: lookups, field permissions, reveal + audit, preferences, drafts | — |
| FM-P6 Collections | grid and card collections, add/remove/reorder | — |
| Part 2 | CRUD bases and page, `YTabView` | [02](02-crud-pattern.md) |
