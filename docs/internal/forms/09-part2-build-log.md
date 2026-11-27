# Forms part 2: build log (started 2026-11-25)

> Status: **built 2026-11-25** (steps 1–7), on branch `Agent/forms-part2` in worktrees under `C:\Dev\YFex\.claude\worktrees\forms\`
> (not merged, not pushed). Builds the decisions the user settled on 2026-11-25 (README §4, 02 §5): ⚑FM61, FM60, FM43,
> FM62 (+ FM18, FM14, FM46, FM22, FM63), FM40, FM42, FM49, FM17, in that order, one commit per step.

## 0. Setup

| Repository | Worktree branch `Agent/forms-part2` from | Why |
|---|---|---|
| YFex.UI | `Agent/dg-p3b` (c1beed0) | the data grid branch already changed `YForm`, `YFormSection`, the Forms generator and attributes |
| YFex (core), YFex.State, YFex.NavigatR, YFex.Persistence, YFex.Localization | `main` | — (YFex.Privacy needed no change: its worktree was removed again, a junction instead) |
| YFex.Mvvm | **3333984**, not main | Mvvm main (91c8dfa, 0363ef7) uses `ICodePrompt`/`IFilePicker`, which exist only in YFex.UI main (§26), not on `Agent/dg-p3b`; the other worktrees (shell, ui-quality) use the same commit |

Every other `YFex*` sibling is a junction. Build output: `--artifacts-path C:/Temp/yfex-forms`.

**Base gap, resolved by a merge (§9):** `Agent/dg-p3b` branched from main before the §26 components. The starting session
then allowed merging YFex.UI main into this branch (only that direction): see §9.

## 1. ⚑FM61 field names in messages ✅

| Repo | Commit | Change |
|---|---|---|
| YFex core | dbbdefa | `YFex.Localization.IFieldMessage` (`FieldPath`, `FieldKey`, `WithField(label)`); `FieldKeys.Literal(label)`: a field key that is its own name |
| YFex.Localization | fbb9b18 | `MessageCatalog.FieldName` returns a literal key's label, so `{field}` renders it |
| YFex.State | e21ef9e | `ErrorItem : IFieldMessage` (`WithField` formats with the bag's catalog and the current UI culture) |
| YFex.UI | 05b22ec | `YValidation.Label` (attached); `ValidationTracker.Label` + `Text(error, label)`; generated editors and slot controls get the form's label; `YTextInput` reads it; summary lines use the same sentence; `ValidationSummaryItem.Text` = the message alone (no "Label: " prefix) |

Notes:
- "core `ValidationMessages`" of the brief is in YFex.State (`ValidationMessages` beside `ErrorItem`); core got the contract
  the UI reads without referencing State. The message engine needed one line in YFex.Localization (an extra worktree).
- Messages shown outside a form (a plain `YTextInput` with no `YValidation.Label`) keep the app catalog's field name.
- Gallery: the Forms "Validation" example has its own labels (`Nome do cliente`, `Tipo de pessoa`…); the probe shows
  "Nome do cliente é obrigatório" under the field and the same line in the summary, nothing prefixed.
- Tests: `FormsSummaryTests.A_forms_own_labels_name_the_field_in_its_messages_and_summary` (new) and the summary test
  updated; Localization 49/49, State 435/435, Forms tests 91/91.

## 2. ⚑FM60 read-only looks on every input + `YDetails` ✅ (YFex.UI 008a62d)

| Piece | What |
|---|---|
| `IsReadOnly` | new on `YToggleButton` (so `YSwitch`, `YCheckBox`, `YRadioButton`), `YChoiceGroup` (passes it to every choice: `YRadioGroup`, `YCheckBoxGroup`), `YComboBox`, `YSlider`, `YRangeSlider`, `YTagInput`, `YSuggestBox`, `YCode`; `YRating` had one. Read-only refuses clicks, keys, wheel, typing and automation changes; focus and copy still work; never the disabled look |
| `ReadOnlyState` (internal) | one place for the pseudo-classes (`:readonly`, `:readonly-plain`, `:readonly-sunken`, `:readonly-compact`, from the inherited `YInput.ReadOnlyLook`), tunnel input blocking for controls that are not toggles, and `Set(control, bool)` that forms use for any editor (text boxes and every Y input; other controls are disabled as before) |
| `ReadOnlyLooks.axaml` | **one Styles file** with every read-only look, included after the control themes in `Themes/Index.axaml` (Styles win over ControlTheme styles). Plain: no outline or fill, a neutral outline on indicators; Sunken: the read-only fill; checked marks, filled tracks and stars turn neutral (`TextSecondary`); chevron hidden; tags lose their remove buttons and the typing box; code cells draw the look; Compact puts combo and tag labels on the left |
| Forms | generated toggles, combos and registered inputs, `YForm.IsReadOnly`, `ReadOnlyWhen`/`Can{X}Edit` and `EditableIn` now make editors read-only instead of disabled |
| `YDetails : YForm` | `IsReadOnly = true`, `ShowSummaries = false`, `ReadOnlyLook = Sunken` at style priority (an attribute or style wins); edit forms keep `InputLook = Outlined` |

- Gallery: Inputs › "Read only · every input" (Plain, Sunken, Inputs columns); Forms › "Details: the read-only screen"
  (`GalleryCustomerDetails`, look picker). Probe: light, dark, 1280×720; clicks on a read-only switch, combo and radio leave
  their values; no lint findings from the new examples.
- Tests: `ReadOnlyLooksTests` (7, real input); Forms/input/stress filters 436/436.
- **Not done here:** `YColorInput` and `YFileDrop` (§26, not on this branch's base; see §0).
- **Merge with `Agent/ui-quality-fixes`:** only `Themes/Index.axaml` is touched by both (different hunks). The read-only
  styles are in a new file, so the 13 input theme files of the focus-ring change are untouched here.
- Finding FM60-1: in Compact, a date field's value trims at the Small width (label left takes the room); the form's Compact
  layout (⚑DG66) may need wider sizes for read-only compact fields.

## 3. ⚑FM43 `YTabView : TabControl` ✅ (YFex.UI 45de7e7)

The data grid branch had a `YTabView : TemplatedControl` (a `YTabStrip` over pages that all stayed in the visual tree). It
is now Avalonia's `TabControl`:

| Piece | What |
|---|---|
| `YTabView : TabControl` | selection, arrow keys, automation from Avalonia; `Appearance` and `Size` are `YTabStrip`'s properties (inherited by the tabs); the tabs sit on the strip's sliding `YSelectionIndicator` (Underline default, Filled, Flat, Outlined, Pill, Chip, Mark); the row scrolls when the tabs do not fit and keeps the selected tab in view; `Pages`, `SelectedPage`, `Reveal(control)` kept |
| `YTabPage : TabItem` | drawn as a `YTabStripItem` (same look and tokens; hover forwarded); `Icon`, `Badge`, `IsShown` (a hidden page is never the one shown); content: `Content`, else `PageTemplate` built on first selection and kept, else `Route` → a `YPageHost` on that route (NavigatR creates the view model then, the host suspends it while hidden); `IsBuilt` |
| Form tab groups (`GroupsAs.Tabs`) | same pages, now TabItems; only the selected page is in the visual tree, every section stays a logical child of its page (editors, values, validation kept); focus-first-error reveals the page and focuses after layout as before |
| Detail tabs (`Detail.Tabs`, History) | built on first selection (`PageTemplate` over the loaded model) instead of up front; `DetailTab.Route` new (a route as a detail tab; was "designed later") |

- Findings: FM43-1 a `TabControl` lists the selected page's content twice in `GetLogicalDescendants` (under the page and
  under its content presenter): `YPageHost.NestedHosts` now takes `Distinct()`; tests that count logical descendants do too.
  FM43-2 the joined looks (Tab, Browser, Overline, Card), close, pin, drag reorder and overflow menu stay `YTabStrip`-only.
  FM43-3 `YTabPage` has its own `PART_FocusRing`; after `Agent/ui-quality-fixes` merges it should use `FocusRing.Target`
  (⚑UI120) like the other controls.
- Gallery: Tabs › "Tab view: pages built on first open" (Underline with a PageTemplate page, Pill). Probe `detail-tabs.probe`
  passes again (the History tab scrolls into view in the narrow detail); light, dark, 1280×720 without new findings.
- Tests: `TabViewTests` (3: lazy template and route pages, keys and hidden pages, detail tab built on first selection),
  `FormTabsTests` updated; tab/detail/forms/stress filters 114/114 and 240/240.

## 4. ⚑FM62 configuration layers (+ ⚑FM18, FM14, FM46, FM22, FM63) ✅

| Repo | Commit | Change |
|---|---|---|
| YFex core | 88254ef | `YFex.Privacy.DisplayContext` (Record, List, Export, Log) and `SensitiveDisplay` (Default, Masked, Clear, Hidden) + `SensitiveDisplays` (defaults: lists/exports masked, one record masked with reveal, logs always masked, secrets always hidden; `Stricter`). Small and additive, so FM63 was built rather than stopped |
| YFex.Persistence | c531ba8 | `ISealedKeyValueStore : IKeyValueStore` marker; `EncryptedKeyValueStore` implements it (callers ask for sealed storage without referencing Cryptography) |
| YFex.Mvvm | 53043a5 | `KeyValueFormStore : IFormStore` over `IKeyValueStore` (UTF-8 text, per-user scope; `IsSealed` from the marker) |
| YFex.UI | 01aaaa8 | below |

YFex.UI:
- `YFex.UI.Forms` (framework-neutral): `IFormConfiguration<TModel>` (+ `IModuleFormConfiguration`, `IClientFormConfiguration`,
  `IServerFormConfiguration`), `IFormDefaults` (per value type), `FormBuilder<TModel>.Field(m => m.X)` (the key comes from
  `[CallerArgumentExpression]`: the compiler's text of the lambda, parsed to `X` / `Address.City`; never run, no expression
  trees, AOT-safe), `FieldRuleBuilder` (Hidden, ReadOnly, Required, Label, Order, Preset, VisibleWith, EditableWith,
  Sensitive(context, display), RevealWith, Draft), `Form().Remember(FormMemory…)`, `FieldPreset` (semantic names: Standard,
  Emphasis, Code, Muted), `FieldChange` + `IFormChangeSource` (data layers), `FieldChanges` JSON (source-generated) +
  `JsonFormChangeSource` (tenant settings `forms.{Model}`), `FormConfigurations` (registry, merge, `HasPermission`, `Store`,
  `Changed`), `FormRules` (resolved: IsVisible, IsEditable, IsRequired, Display, CanReveal, KeepsInDraft). `IFieldShape.ValueType`
  added. References `YFex.Authorization.Public` (a leaf over core) for `Permission`; no Authorization change (⚑FM18).
- Merge rule: layers in order AppDefaults → Model → Module → Client → Server → Tenant → User → Composer; hidden, read-only,
  required, permissions and masking only add up (`FieldRule.Merge`, `SensitiveDisplays.Stricter`); labels, order, preset take
  the later value; a data layer can only say `true`. No permission checker = fails closed.
- `YForm` (`YForm.Rules.cs`, partial): `Configurations` (inherited) / `DefaultConfigurations`; fields hidden or without
  `VisibleWith` left out; locked or without `EditableWith` read-only (conditions never unlock them); labels, order, preset class
  `preset-{Name}` (styles in `FieldPresets.axaml`); required-by-layer marked "*" and checked by `ValidateRequired()` /
  `FocusFirstInvalid()` with a summary line; `DisplayContext`; sensitive values (`IClassifiedData`) masked on read-only
  records (and hidden/masked in other contexts) with an eye button when `CanReveal`, raising `SensitiveRevealed` (audit hook);
  `Rules`; remembered open sections through `FormConfigurations.Store` and `ResetLayoutAsync()` ("Restaurar layout padrão").
- `IFormStore` + `MemoryFormStore` in `YFex.UI.Public`; `IconKey.Eye`; texts `ui.show-value`, `ui.form.reset-layout`.
- Gallery: Forms › "Configuration layers" (permission switches, tenant settings, check required, reset layout, details with a
  masked e-mail and the audit line). Probe: tenant relabels "CPF/CNPJ", hides Branch, requires Billing code; reveal reported;
  light, dark, no new findings.
- Tests: `FormConfigurationTests` (11): lambda keys, layer merge never loosens, tenant JSON, sensitive defaults, permissions /
  presets / order / required in a real form, masked value revealed with a real click and reported, remembered sections and reset.

Findings and decisions to confirm:
- FM62-1 **Required from a later layer is checked by the form, not by the model's validation** (no message under the field; a
  summary line and focus). YFex.State/Validation have no per-instance rule hook a UI layer may add to. ⚑ proposal: a small
  `IValidationSource` extension (extra required paths) in YFex.Validation + State, then the form adds instead of checking.
- FM62-2 Configuration classes are registered by hand (`Add(new …)`); a generator emitting the registration (as CQRS does) is a
  later convenience.
- FM62-3 A child model's own configuration is not read when it is shown inside a parent form: configure by path from the
  parent (`m => m.Address.City`).
- FM62-4 `preset-Code` sets the monospace font on the whole input (label too); a value-only part would need a template hook.
- FM63-1 Only `IClassifiedData` value types are detected as sensitive; a raw `[Sensitive]` string property is not (the Forms
  generator does not record it). Lists (the data grid) do not apply `DisplayContext.List` yet: `FormRules.Display` is ready.
- FM63-2 An editable field in an edit form shows its value in clear (⚑FM19 masks details and read-only fields only).

## 5. ⚑FM40 CRUD bases + ⚑FM42 `YCrudPage` ✅ (YFex.Mvvm ca54009, YFex.UI 35e7e55)

| Piece | What |
|---|---|
| `ListViewModel<TRow>` (Mvvm) | `Rows`, `Selected`, `IsBusy`, `RefreshCommand`, `LoadRowsAsync`; loads on navigation, keeps the selection on refresh |
| `EditorViewModel<TModel>` (Mvvm) | `Model`, `IsNew`, `IsSaving`, `NewAsync`, `SaveAsync` over `CreateAsync`/`SaveModelAsync` |
| `CrudViewModel<TRow, TModel>` (Mvvm) | an `ICrudPage` and the detail's `IDetailSource`: `LoadModelAsync` (mapping by hand, ⚑FM45), `CreateAsync`, `SaveModelAsync(model, isNew)`, `DeleteRowAsync` + `CanDelete`, `CanEdit`, `FindRow` (selects a created record), `Describe`; delete asks first (message box) and toasts; save toasts and refreshes. A new record is edited under a private placeholder item ("Novo" in the detail title) |
| Stubs | `EditorViewModel<TModel>` and `ListViewModel` stubs removed (unused); `SelectorViewModel` and `MasterDetailViewModel` kept |
| `ICrudPage` (UI.Public) | Title, Rows, SelectedRow, IsBusy, CanAdd, NewItem(), RefreshCommand, DeleteCommand + `IDetailSource` + `INotifyPropertyChanged` |
| `YCrudPage` (YFex.Avalonia) | `YPageHeader` (title, refresh), the grid (`AutoColumns` from the row's `[Form]` shape) with toolbar (search → `SearchText`, Add), detail through the grid's `YDetailPresenter` (`DetailPresentation`, `DetailSource` for another view model's tile, ⚑DG4–DG5), empty state with an Add action. Slots: `PageCommands` (+ `ReplacePageCommands`), `GridToolbar` (+ `ReplaceGridToolbar`), `Grid`, `DetailTemplate`, `Empty`. Rows and selection synced in code (no reflection bindings) |

- Owner fix: `FieldShape<TModel,TValue>.GetValue(object)` returns null for an object of another type (it threw when the grid read
  a column of the new-record placeholder; custom rows could hit it too).
- Texts `ui.crud.*`, `ui.form.save/discard/draft-restored` (en, pt).
- Gallery: Forms › "CRUD page" (`GalleryCustomersCrud`, a `[Form]` row model). Probe: add → type a name → Save adds the row and
  selects it, light and dark, no findings. Note: an embedded `PageViewModel` must be made with its page (it takes the page's
  navigator); a lazily created one throws "No Navigator".
- Tests: `CrudPageTests` (2, real input: default parts, select, add and save; slots add and replace) against an `ICrudPage`
  fake; the test found a rebuild bug (slot controls kept their old parent), fixed.
- Not built: "Restaurar layout padrão" in the CRUD page menu (the page has no menu yet; `YForm.ResetLayoutAsync` exists),
  remembering detail placement and grid columns (⚑FM46 flags exist; the grid's saved views are the data grid plan's), a StressKit case.

## 6. ⚑FM49 a model as a whole page ✅ (YFex.NavigatR 0203a0b, YFex.Mvvm 209a1d3, YFex.UI 728f3dc)

| Piece | What |
|---|---|
| `[FormPage(Title, Icon, Layout, ViewModel)]` (YFex.UI.Forms) | marks a `[Form]` model as a page; `FormPageLayout` Sections, Tabs, Wizard (YFex.UI) |
| NavigatR generator | a second pipeline on `YFex.UI.Forms.FormPageAttribute` (by metadata name): emits `{Model}PageRoute : FormPageRoute<Model>` (display name, icon, layout) and adds `Register<{Model}PageRoute, FormPageViewModel<Model>>()` (or the attribute's `ViewModel`) to `RegisterGeneratedRoutes()`; NAV010 when the project does not reference YFex.Mvvm |
| `IRoute.Icon` (NavigatR.Public) | an icon name (YFex.UI `IconKey` name) beside `DisplayName`, default null, for navigation lists and the command palette (⚑UI215) |
| Mvvm | `FormPageRoute<TModel>` (abstract record), `IFormPageHandler<TModel>` (Load, Validate, Save; from DI, optional), `FormPageViewModel` (an `IFormPage`: Title, Icon, Layout, Model, IsModified, IsBusy, commands) and `FormPageViewModel<TModel>` (route → title/icon/layout, load or `new TModel()`, change tracking, Save with toast, Discard = reload, leave warning with unsaved changes; hooks `OnLoadedModelAsync`, `OnSavedAsync`, `OnModelChanged`) |
| `IFormPage` (UI.Public) | the contract `YFormPage` shows; `ViewMap` now also looks up a view model's interfaces, so `YFexAvaloniaServices.Register` maps `IFormPage` → `YFormPage` once |
| `YFormPage` | `YPageHeader` (title, Discard, Save), the model's `YForm` in a scroll (Sections) or with `GroupsAs.Tabs`; Ctrl+S; Save first runs `ValidateRequired()` and focuses the first problem |

- Gallery: Forms › "A model as a whole page" opens `GallerySettingsModel` (tabs Company, Invoices) through
  `GallerySettingsModelPageRoute`. Probe: page built by NavigatR, typing enables Discard, Ctrl+S saves (toast) and clears it;
  light, dark, 1280×720 without findings.
- Tests: `FormPageTests` (3: generated route data, view by contract, YFormPage save by click and Ctrl+S). NavigatR suite: 203
  passed + the 3 known `StringRouteTests` failures; `GeneratedRoutesTests` need the default `bin` layout (run without
  `--artifacts-path`: 38/38).
- **Not built:** the Wizard layout (`YWizard` is on main, not on this branch; it shows as sections), `[FormCommand]` methods on
  the command bar (design text of ⚑FM49, not in this brief), the section index (jump list) of the Sections layout.
- ⚑ placement to confirm (FM49-1): NavigatR's generator knows two names from upper layers (`YFex.UI.Forms.FormPageAttribute`,
  `YFex.Mvvm.FormPageRoute`/`FormPageViewModel`) to keep one `RegisterGeneratedRoutes()`. The alternative is a Forms-generator
  `RegisterFormPages()` the app calls next to it.

## 7. ⚑FM17 drafts, sealed ✅ (YFex.UI e4960bb; YFex.Persistence c531ba8 and YFex.Mvvm 53043a5 from step 4)

| Piece | What |
|---|---|
| Field text (YFex.UI.Forms) | `IFieldShape.ToText(model)` / `FromText(model, text)`: invariant text per field kind (text, bool, `INumber`, `IParsable` value types, enums, `DateOnly` "O"); child models by path |
| `FormDrafts` | `Capture(shape, model, rules)` → JSON `{"Name":"Ana","Address.City":"Campinas"}`; `Restore(...)` → fields set. Read-only fields and `Draft(DraftPolicy.Exclude)` fields left out; sensitive values kept (the store seals them); damaged JSON ignored |
| `YForm.DraftKey` | writes the draft 600 ms after the last change, restores it when the form shows a model (`DraftRestored` event), `SaveDraftAsync`, `DiscardDraftAsync`. **Only a sealed store** (`IFormStore.IsSealed`, i.e. `KeyValueFormStore` over `ISealedKeyValueStore` = Persistence's `EncryptedKeyValueStore`) gets drafts; any other store gets none |
| `YFormPage` | a draft per page model (`forms/{Model}/draft`); "Suas alterações não salvas foram restauradas" in the header after a restore; Save (IsModified → false) and Discard drop it |

- Gallery: a demo in-memory store marked sealed (`YForm.DefaultConfigurations`), with a comment that apps pass
  `KeyValueFormStore` over `EncryptedKeyValueStore`.
- Tests: `FormDraftTests` (2): every field kind and a child round-trip, the excluded CVV stays out, the e-mail stays in; a form
  writes nothing to an unsealed store, writes and restores with a sealed one, discard removes it.
- Probe: typing on the form page enables Discard and keeps the value; leaving and reopening was not driven by the probe (the
  back gesture did not fire from inside a text box), so restore across navigation is covered by the test only.

## 8. Final checks and hand-over

| Check | Result |
|---|---|
| `YFex.Avalonia.Tests` full suite | **967/968** (last run; the data grid branch was 939/939, +29 new tests). The one failure differs per run and passes alone: run 1 `DataGridViewTests.Matches_fold_case_and_accents_and_merge_overlaps` (a pure text function: another test changes the culture) and `GallerySmallWindowTests` (**real, fixed** in 10f1f5e: `YTabView` scrolled the page); run 2 `StressKitTests(NavBar)` (timing; 3/3 alone). Treat the first and last as order/timing flakes to look at separately |
| FormsSpike | JIT **ALL PASS**; Native AOT (`dotnet publish -c Release -r win-x64`) **ALL PASS**. AOT warnings: known ChartSeriesBinder IL2075, DataGrid IL2104/IL3053, plus `YDockHost` IL2072 and `YWindow` IL2026/IL3050 that come from the base branch. The ViewMap IL2075 this step introduced is fixed (b456f66; trim analyzer clean on YFex.UI.Public) |
| YFex.State | 435/435 · YFex.Localization 49/49 · YFex.NavigatR 203 + the 3 known `StringRouteTests` failures (`GeneratedRoutesTests` 38/38 in the default bin layout) · YFex.Mvvm, YFex.Persistence(.Encryption), core: build only (no test projects touched) |
| Probe | every new example in light, dark and 1280×720 (sections above); `detail-tabs.probe` passes |

**Branches (all `Agent/forms-part2`, not merged, not pushed; worktrees in `C:\Dev\YFex\.claude\worktrees\forms\`):**

| Repo | Base | Commits |
|---|---|---|
| YFex.UI | `Agent/dg-p3b` c1beed0 | 05b22ec FM61 · 008a62d FM60 · 45de7e7 FM43 · 01aaaa8 FM62 · 35e7e55 FM42 · 728f3dc FM49 · e4960bb FM17 · 10f1f5e tab-row fix · b456f66 ViewMap trim fix |
| YFex (core) | main 1422ea1 | dbbdefa FM61 · 88254ef FM63 |
| YFex.Localization | main 05821df | fbb9b18 FM61 |
| YFex.State | main 0782aa3 | e21ef9e FM61 |
| YFex.Persistence | main 5338393 | c531ba8 FM17 marker |
| YFex.Mvvm | **3333984** (not main) | 53043a5 store · ca54009 FM40 · 209a1d3 FM49 |
| YFex.NavigatR | main 50e0fca | 0203a0b FM49 routes + `IRoute.Icon` |

**Merge notes:**
- YFex.UI depends on `Agent/dg-p3b` (merge that first). Overlap with `Agent/ui-quality-fixes`: only `Themes/Index.axaml`
  (different hunks); new controls here (`YTabPage`) still use a `PART_FocusRing` border that ⚑UI120 replaces.
- YFex.Mvvm: merge main's `ViewModel.CodePrompt`/`FilePicker` commits with this branch (no shared lines expected).
- The §26 parts wait for YFex.UI main on this branch: read-only looks of `YColorInput`/`YFileDrop` (⚑FM60) and the Wizard
  layout of `YFormPage` (⚑FM49). The question was sent to the starting session; no answer reached this session.

**Answers 2026-11-26 (user), plan in [10](10-part3-follow-ups.md):** FM43 confirmed, plus an option to keep pages in the
visual tree (§1); FM63 changed to opt-in (§2); `[FormCommand]` and FM62-2 became ⚑FM70, ⚑FM71 (both accepted 2026-11-27: page commands on the page view model; generated registration). FM49-1 confirmed 2026-11-27 (user):
keep NavigatR's generator registering `[FormPage]` routes (one `RegisterGeneratedRoutes()`). FM62-1 reopened and decided 2026-11-27: `[Requirable]` on the command, checked by the generated validator on client and server (10 §6, ⚑FM72–FM74).

**Decisions to confirm (proposals, built as written):** FM62-1 layered Required checked by the form (a Validation hook would
let the model own it); FM49-1 NavigatR's generator names `YFex.UI.Forms.FormPageAttribute` and YFex.Mvvm types; FM43 only the
selected tab is in the visual tree (content stays a logical child); FM63 masking only for `IClassifiedData` values and only
read-only records in `Record` context; `YForm.DefaultConfigurations` in the gallery uses an in-memory store marked sealed.

## 9. Merge of main and the §26 parts (after the approval)

| Repo | Merge commit | What came in |
|---|---|---|
| YFex.UI | **6a5023f** `Merge main … into Agent/forms-part2` | main 846b5bb: §26 components (YCodePrompt, YColorPicker/YColorInput, YFileDrop, YStepper/YWizard) and the already-merged `Agent/dg-p3b`, `Agent/ui-quality-fixes`, `Agent/shell-docking`. Conflicts only in `IconKey.cs` and `Icons.axaml` (both sides appended icons; main's first, then `Eye`) |
| YFex.Mvvm | **7a912c5** `Merge main (ViewModel.CodePrompt, FilePicker)…` | needed by the merged gallery (`ViewModel.CodePrompt`/`FilePicker`); Mvvm main 0363ef7 |

- After the merge: build clean, **full suite 1083/1083**.
- 853cb39 **FM60 §26:** `YFileDrop.IsReadOnly` (no drop, no Browse, rows without Retry/Remove; Plain and Sunken hide the drop area
  and show the list, rows drawn plain or sunken; `ReadOnlyState.Set` covers it); `YColorInput` is a text input, so
  `YTextInput`'s read-only looks already draw it (it hides its picker button). Both in the gallery's read-only grid; test
  `The_section_26_inputs_read_only_file_drop_and_color_input`.
- 4932e6a **FM49 Wizard layout:** `YFormPage` with `FormPageLayout.Wizard` builds a `YWizard` with a step per group (the form's
  groups as tab pages with the strip hidden, `YTabView.no-strip`; the one form moves into the current step). Next checks the step
  (fields a configuration requires, visible errors on the page) and focuses the first problem; Finish runs Save; Cancel runs
  Discard; the header has no Save/Discard in this layout. Gallery: "Open a store (wizard)" (`GalleryOnboardingModel`, trade name
  required by `GalleryOnboardingForm`); probe in light, dark and 1280×720; test `The_wizard_layout_steps_through_the_groups_and_finish_saves`.
- `YTabPage` no longer draws its own `PART_FocusRing`: the shared `YFocusRing` (⚑UI120) shows around the tab (FM43-3 closed).
- Final gates after the merge and additions: **full suite 1084/1085**; the one failure
  (`NavBarRevealTests.Hover_opens_the_pane…`, a hover timing test) ran while the AOT publish loaded the machine and passes alone
  (7/7). FormsSpike JIT and **Native AOT ALL PASS**; AOT warnings are only the pre-existing ones (ChartSeriesBinder IL2075, DataGrid
  IL2104/IL3053, `YDockHost` IL2072, `YWindow` IL2026/IL3050).
- Branch tips now: YFex.UI `Agent/forms-part2` = e0928fb; YFex.Mvvm = 7a912c5.
  The rest as in §8. Still not merged into main, not pushed.
