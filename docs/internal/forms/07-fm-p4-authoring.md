# FM-P4 Authoring: built 2026-11-19

> Status: **built 2026-11-19 (uncommitted)**; plan asked by the user the same day; ⚑FM55, FM57, FM58, FM59 answered by
> the user, ⚑FM54 and FM56 built as proposed. What was built: §7; checks: §8; findings: §9. Builds on FM-P3 part 1 ([06](06-fm-p3-validation-and-layout.md)).
> The user added three requests to this phase: a line break after a field, opening collapsed sections that hold errors,
> and choosing when validation shows from the model (on change, with a delay). Decisions ⚑FM54–⚑FM59 below.

Today a form is either fully generated (`<YForm Source=…/>`) or fully hand-placed (`YForm` without a source, any
controls with `YForm.Size`). FM-P4 lets one screen mix the two and keep the generated editors, labels, validation and
conditions. **Done when** the three ways of writing a form (generated, XAML with `YField`/`YFormRest`, C# markup) give
the same layout (same rows, same tracks), checked by a test.

## 1. What the source has today (read 2026-11-19)

| Piece | Today |
|---|---|
| `YForm.Rebuild` | with a `Source`, **clears the hand-placed children** and adds the shape's editors; without one, lays out what was placed |
| `YForm.Builder` | creates one editor per field (`IFieldShape.CreateEditor` → `AvaloniaEditorFactory`), a section per group / child model, `FieldEntry`/`SectionSlot` in form order (used by `FocusFirstInvalid`) |
| `YFormEditors` | one slot per value type (`Register<T>`, `RegisterValue<T>`), last call wins: a package and an app registering the same type overwrite each other by load order |
| `[Field(NewRow = true)]`, `YForm.NewRow` | start a new row **at** the field; the row before is closed and filled (`FormColumns.FillRow` grows its Medium+ fields) |
| `YTextInput.Field="{Binding Name}"` | `[AssignBinding]`: binds and reads the compiled path (`FieldPathOf`), so a typo is a build error — the pattern `YField` reuses |
| `FocusFirstInvalid()` | opens the sections **on the way to the first** invalid field only, and builds a `BuildWhenShown` section holding it |
| Inputs | push their value on every keystroke (`YParsedInput` sets `Value` when the draft parses) |
| YFex.State timing | `[ValidationOptions(Trigger = ValidateOn.Change/Commit/Submit, Show = ShowErrors.Immediately/AfterTouched/AfterSubmit)]` on the class, `[ValidateOn]` per property, `[Debounce(ms)]` on a property delays only its **async** rules. Default: rules run on every change, errors **show after the field lost focus once** (`AfterTouched`) — that is why the user sees validation only on blur |
| Fluent markup | `M.YForm().Bind(vm.Customer)`, `YFormPanel(…)`, `YFormSection(…).Fields(…)`, `.FieldSize()`, `.NewRow()`; no typed `Form(model, f => …)` builder |

## 2. Authoring pieces

### 2.1 `YField`, `YFormSection Field=…`, `YFormRest`

```xml
<y:YForm DataContext="{Binding Customer}" x:DataType="m:CustomerEditModel" MaxColumns="3">
    <y:YFormSection Header="{y:Text Customer.Contact}">
        <y:YField Field="{Binding Name}" Size="Large" />     <!-- the shape's editor, label, validation, conditions -->
        <y:YField Field="{Binding Document}" EndsRow="True" />
        <y:YField Field="{Binding Email}" />
    </y:YFormSection>
    <y:YFormSection Field="{Binding Address}" />            <!-- the child model's own section, automatic -->
    <y:YFormRest />                                          <!-- every field and section not placed above, in model order -->
</y:YForm>
```

- With a `Source` **and** authored children, the authored children are the layout: `YForm` keeps them and replaces each
  `YField` by the editor the shape makes (same factory, same `FieldEntry`, so `FocusFirstInvalid`, conditions and
  validation work as in a generated form). Plain controls in between stay as they are.
- `YField.Field` is an `[AssignBinding]` like `YTextInput.Field`: only its compiled path is read (never bound), so the
  form keeps linking through the generated accessors (no reflection). Paths are the model's own member names; a dotted
  path (`Address.City`) places one field of a child model.
- `YField` properties (`Size`, `NewRow`, `EndsRow`) override the shape's for this view (same level as `FieldOverride`).
- `YFormRest` expands to the generated items of everything not placed, in shape order, groups as sections. Without a
  `YFormRest`, unplaced fields are **not shown** and a debug message lists them (the design's eject warning, 01 §9.3).
- Placing a field twice, or a path the shape does not have (only possible with a reflection binding), throws when the
  form builds, naming the model and its fields.
- **Compiled-binding scope (checked in A1):** `{Binding Name}` must compile against the model type, not the page's
  view model. Measured in the gallery: with `x:DataType` on an element, its **own** bindings resolve against that type
  (`Source="{Binding Customer}"`, `Tag=…` fail with AVLN2000), **except `DataContext`**, which Avalonia resolves against
  the parent scope. So a mixed form is written `DataContext="{Binding Customer}" x:DataType="m:CustomerEditModel"`, and
  `YForm` uses its `DataContext` as the model when `Source` is not set and its children name fields (`YField`,
  `YFormRest`, `FieldOverride`, `FieldSlot`, `YFormSection Field`). `Source` stays for generated forms.

### 2.2 `FieldOverride` and `FieldSlot`

```xml
<y:YForm Source="{Binding Customer}">
    <y:FieldOverride For="Document" Size="Large" EndsRow="True" />
    <y:FieldSlot For="Address">
        <local:AddressWithMap />          <!-- replaces the field or section; still on the form grid (YForm.Size applies) -->
    </y:FieldSlot>
</y:YForm>
```

- Both are invisible markers: they hold settings, never take a place in the flow. They work in a fully generated form
  (no other children) and in a mixed one.
- `FieldOverride`: `Size`, `NewRow`, `EndsRow`, `IsVisible` (hide in this view), later `Preset` (⚑FM14).
- `FieldSlot`: its content takes the field's place (or the section's, for a group key or child model); the slot's
  content gets the field's `AutomationId` when it has none. A slot does not bind anything: its content binds itself.

### 2.3 C# markup

```csharp
M.Form(vm.Customer, f => f
        .Section("Customer.Contact", s => s
            .Field(c => c.Name).Large()
            .Field(c => c.Document).EndsRow()
            .Field(c => c.Email))
        .Section(c => c.Address)
        .Override(c => c.Phone, o => o.Size(FieldSize.Small))
        .Slot(c => c.Notes, new NotesEditor())
        .Rest())
    .MaxColumns(3);
```

`Form<TModel>` builds the same authored children as the XAML (`YField`, `YFormSection`, `YFormRest`, markers), so both
go through one code path. `Field(c => c.Name)` reads the member name from `[CallerArgumentExpression]` (as
`.Bind(vm.Name)` does), and the lambda's type makes a wrong name a build error.

### 2.4 Editors per app, and which override wins

```csharp
// package (module initializer of YFex.Entity.Avalonia): the type's own input — unchanged API
YFormEditors.Register<CpfCnpj>(() => new YCpfCnpjInput());
// app (startup): this app's choice for the type, wins over the package's whatever the load order
YFormEditors.Override<CpfCnpj>(() => new DocumentInput());
```

Narrowest first: **slot in this view → override in this view (`FieldOverride`, `YField` settings) → `[Field]` on the
model → the app's editor for the type (`Override`) → the package's editor for the type (`Register`) → the built-in
default.** Two slots per type in the generic static (app, package), so the order of registration no longer matters.

## 3. The user's three additions

### 3.1 Line break after a field (⚑FM57)

`NewRow` exists (break **before** a field). The request is the other side: the row ends **after** this field even when
the next one fits.

```csharp
[Field(Size = FieldSize.Medium, EndsRow = true)] [Observable] public partial string? Name { get; set; }
```
```xml
<YTextInput Description="Name" y:YForm.EndsRow="True" />      <!-- manual forms: new attached property -->
```

Question: what happens to the space left in that row? Today a row closed early is **filled** (its Medium and wider
fields grow to the right edge), which would make a Medium field that ends a row as wide as a Full one — the break would
look like a size change. Recommendation: **a row closed by an explicit break (`EndsRow` here, `NewRow` on the next field)
keeps its fields' sizes and leaves the rest empty**; only rows that end because the next field did not fit are filled.
Con: changes how `NewRow` looks today (rows before a `NewRow` stop growing), so existing screens using `NewRow` change.

### 3.2 Open collapsed sections that hold errors (⚑FM58)

Today only the sections on the way to the **first** invalid field open (`FocusFirstInvalid`). Proposal:

- `FocusFirstInvalid()` opens **every** collapsed section that holds an error (building `BuildWhenShown` ones), then
  focuses the first.
- New `YForm.ExpandInvalidSections` (default **true**): when the model's errors become visible (a failed save raises
  `ViolationsChanged("")`, a server result lands), the form opens collapsed sections that hold a visible error, without
  the view calling anything. A section the user closes again stays closed until errors change again.
- Sections hidden by a condition (`Is{Group}Visible` false) stay hidden: their fields should not be validated (model
  side).
- The error dot with a count on a closed section header stays with ⚑FM50 (deferred).

### 3.3 A delayed change, and showing validation while typing (⚑FM59 ✅ both)

Checked first (the user asked whether it already existed): rules already run on every change, errors show after blur by
default (`AfterTouched`), `Show = Immediately` shows them on every keystroke, and `[Debounce]` on a property delayed only
its async rules. Nothing delayed when errors show. The user chose both of these (timing belongs to YFex.State,
validation 07 D16):

```csharp
// (a) a general delay of what a change sets off (not only validation)
[Observable, Debounce(800)] public partial string? Search { get; set; }       // its checks run after 800 ms quiet
[Debounce]                                 // on the class: 500 ms for every [Observable]; [Debounce(0)] opts out
public partial class CustomerEditModel : MvvmStateObject { … }

// (b) rules live, only the messages wait
[ValidationOptions(Show = ShowErrors.AfterPause)]          // PauseMs default 700
public partial class SupplierEditModel : MvvmStateObject { … }
```

- (a) The value and its own `PropertyChanged` change at once; the property's checks (sync and async rules, invariants,
  When-dependents) run after the pause. Leaving the field (Touch) and `ValidateAll()` run a pending check at once.
  `[Computed]` and `[ReactsTo]` would follow the same pause, but the State generator does not generate them yet (§9 P4-1).
- (b) A field's new errors show once it stays unchanged for the pause (`PauseMs`, or the property's `[Debounce(ms)]`),
  when it is left, or after a save; errors already showing stay while they still apply (matched by rule code) and go at
  once when fixed. `HasErrors` (Save gates) stays live.
- Forms need nothing: inputs already listen to `ViolationsChanged`.
- Rejected: a delay in the control (every platform would repeat it, and the model could not choose it); a new attribute.

## 4. Decisions

| ⚑ | Topic | Proposal | Alternatives |
|---|---|---|---|
| FM54 | Mixed forms | a form with `Source` and authored children keeps them as its layout (placeholders replaced); without `YFormRest` unplaced fields are not shown + debug list | always append the rest; a separate `YFormLayout` control |
| FM55 ✅ | Naming the field in `FieldOverride`/`FieldSlot` | **`Field="{Binding Document}"` like `YField` (user 2026-11-19)**: a typo is a build error; needs the model `x:DataType` in scope | `For="Document"` string checked at run time |
| FM56 | App editors per type | `YFormEditors.Override<T>` (static, app level) above `Register<T>` (package level) | `services.AddYFexForms(f => f.Editor<T>(…))` (DI suggests scoping the static registry does not have); last registration wins (today) |
| FM57 ✅ | Line break | **`[Field(EndsRow)]`, `YForm.EndsRow`, `YField`/`FieldOverride` `EndsRow`, markup `.EndsRow()`; rows closed by an explicit break (`EndsRow`, `NewRow`) keep their sizes and leave the rest empty (user 2026-11-19)** | explicit breaks fill the row like today |
| FM58 ✅ | Sections with errors | **`FocusFirstInvalid` opens all; `YForm.ExpandInvalidSections` (default true) opens them when errors become visible (user 2026-11-19)** | only on `FocusFirstInvalid`; only the first section |
| FM59 ✅ | Delay of a change / showing validation while typing | **Both (user 2026-11-19), with defaults:** (a) **`[Debounce]` widened** (general, not only validation): on an `[Observable]` property (or on the class, for all its observables; no argument = 500 ms, `Debounce(0)` = none) it delays what the change sets off — the property's validation (sync and async), its `[Computed]` dependents (and `[ReactsTo]` once that is generated); the value and its own `PropertyChanged` change at once; blur (Touch) and `ValidateAll` run a pending check now. (b) **`ShowErrors.AfterPause`** (`[ValidationOptions(PauseMs)]`, default 700): rules and `HasErrors` stay live, messages show after the pause | new attribute (`[ShowErrorsAfter]`, `[Settle]`); control-side delay; `Show = Immediately` only |

Code (FM59, agreed):

```csharp
// (a) general delay of a change's effects
[Observable, Debounce(800)] public partial string? Search { get; set; }       // [Computed] / validation after 800 ms quiet

[ValidationOptions(Show = ShowErrors.Immediately)]
[Debounce]                                   // class default, 500 ms, every [Observable] of the class
public partial class CustomerEditModel : MvvmStateObject { … }

// (b) rules live, only messages wait
[ValidationOptions(Show = ShowErrors.AfterPause)]          // PauseMs default 700
public partial class SupplierEditModel : MvvmStateObject { … }
```

Finding while checking (2026-11-19): `[Debounce]`, `[Throttle]`, `[ReactsTo]`, `[Cooldown]`, `[Poll]`, `[Busy]`, `[Queue]`,
`[Gate]`, `[RequiresAll]`, `[ErrorBucket]` on **methods** are declared in `YFex.State/Attributes.cs` but no generator
implements them (the State generator reads `[StateCommand]` and `[Observable]`/`[Computed]` only; property `[Debounce]` is
read by the Validation generator for async rules). Not part of FM-P4; recorded for YFex.State.

## 5. Steps

Each step builds; tests, gallery and AOT smoke at the end (A8).

| Step | Work |
|---|---|
| A1 | Check the compiled-binding scope (§2.1); `YField`, `YFormRest`, `YFormSection.Field`; `YForm` keeps authored children with a `Source` |
| A2 | `FieldOverride`, `FieldSlot` (generated and mixed forms) |
| A3 | `YFormEditors.Override<T>` + precedence |
| A4 | Markup `M.Form(model, f => …)` building the same children |
| A5 | `EndsRow` (attribute, generator, shape, attached property, panel) and the explicit-break rule (⚑FM57) |
| A6 | Open sections with errors (⚑FM58) |
| A7 | YFex.State `AfterPause` + Validation generator (⚑FM59) |
| A8 | Tests: the three styles give the same placements; overrides and precedence; slots; rest; EndsRow; sections open on errors; AfterPause (State tests, fake time). Gallery Forms examples with code (*Mixed*, *Overrides and slots*, *Markup*, *While typing*); AOT spike model gains groups, a child model and a condition (P2-3, P3-5) |

## 6. Ecosystem fit (this phase)

| Library | Outcome | ⚑ |
|---|---|---|
| YFex.State | **lacks a feature**: show-after-pause timing (§3.3) | FM59 |
| YFex.Validation | **lacks a feature** (generator emits the edit call) | FM59 |
| YFex.Localization (core) | used: section headers in XAML via `{y:Text}` | — |
| YFex.Automation | used: slots get the field's automation id | — |
| Others (Mvvm, NavigatR, Composer) | unchanged; Composer's future layout descriptors would produce the same authored children (⚑FM29) | — |

## 7. What was built

| Area | Where | What |
|---|---|---|
| Pieces | `YFex.Avalonia/Controls/Forms/FormPieces.cs` | `YField` (`Field` = `[AssignBinding]`, path only; `Size`, `NewRow`, `EndsRow`), `YFormRest`, `FieldOverride` (`Field`, `Size`, `NewRow`, `EndsRow`, `Hidden`), `FieldSlot` (a `Decorator`, `Field`); invisible placeholders, no template |
| Form | `YForm.cs` | the view's children are kept as the layout while a model is shown; model = `Source`, or the `DataContext` when the children name fields; placeholders replaced by the shape's editors (same factory, entries, conditions, validation); the view's own sections keep their items and get them back on rebuild; dotted paths (`Address.City`) through the new `IChildFieldShape`; rebuild when a child object on a placed path is replaced; a name the shape lacks, or a field placed twice, throws and lists the model's fields; unplaced fields without `YFormRest` → debug message; the view's children leave the form before the build (a slot can land in a generated section) |
| Precedence | `YForm.Builder.Editor` | slot → `YField` settings → `FieldOverride` → `[Field]` → app editor → package editor → built-in |
| Section | `YFormSection.Field` / `FieldPath` | the child model's generated section in that place; locally set `Header`, `IsCollapsible`, `IsExpanded`, `MaxColumns`, `YForm.Size` win |
| App editors | `YFormEditors.Override<T>` / `OverrideValue<T>` | an app slot above the package slot (`Register<T>`), per type in the generic static; `null` removes it |
| Line break | `[Field(EndsRow)]`, `IFieldShape.EndsRow`, generator, `YForm.EndsRow`, markup `.EndsRow()` | `YFormPanel`: a row closed by an explicit break (`EndsRow`, `NewRow`) is not filled (⚑FM57); full rows and the last row still are |
| Sections with errors | `YForm.ExpandInvalidSections` (default true) | on `ViolationsChanged` (coalesced, background priority) collapsed sections holding a **visible** error open (built when needed); a section the user closes stays closed until its errors clear and come back; `FocusFirstInvalid` opens every section with an error, then focuses the first |
| Markup | `Markup/FormLayout.cs`, `Helpers.Form(model, f => …)` | `Field(c => c.X)`, `Tiny()`…`Full()`, `NewRow()`, `EndsRow()`, `Section(header, …)`, `Section(c => c.Child)`, `Override`, `Slot`, `Rest`, `Add`; paths from `[CallerArgumentExpression]`; the same children as the XAML |
| YFex.State | `Attributes.cs`, `ValidationBag` | `[Debounce]` (default 500 ms, also on a class), `ShowErrors.AfterPause`, `[ValidationOptions(PauseMs)]` (700), `ValidationBag.PauseMs`, `Edited(path, pauseMs)`, per entry `Paused` / `PauseVersion` / `Settled`; `ResetInteraction` clears them |
| YFex.Validation generator | `Parser.Vm`, `Emitter.Vm` | class and parameterless `[Debounce]`; `PauseMs`; debounced setter hooks (`__vDebounced_X` with a version counter, `__vRunDebounced_X`); Touch and `ValidateAll` run pending checks; `bag.Edited` under AfterPause; the async runner's own delay removed (the whole chain waits) |
| Gallery | Forms page | *Mixed* (XAML: a section with `YField`s and a dotted path, `EndsRow`, `FieldOverride`, `FieldSlot`, `YFormRest`), *Mixed, in C# markup*, *Validation while typing*; code samples `Forms.Mixed`, `Forms.MixedMarkup`, `Forms.Typing` |
| Spike | `YFex.Avalonia.FormsSpike` section 5 | groups, child model, condition, `EndsRow`, markup with a dotted path / override / slot / rest, `YField` with a compiled binding and the model from the `DataContext` (closes P2-3, P3-5) |

## 8. Checks

- `FormsAuthoringTests` (11): the three styles give the same bounds for every field (generated; `YField`s from compiled
  bindings with the model as `DataContext`; markup with `Rest`); `EndsRow` on the model; explicit breaks not filled, the
  last row filled; overrides (size, hidden, `YField` over `FieldOverride`); slot place, size and id; a slot inside a group
  section; no rest + dotted path; an unknown name throws; the app editor beats a later package registration; sections
  open when errors become visible; `ExpandInvalidSections=false` + `FocusFirstInvalid` opens every one.
- Avalonia suite 423/424 (the known `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent`).
- YFex.State 371/371 (new `ValidationTimingTests`, 8: debounced validation, Touch and `ValidateAll` run pending checks,
  class default and opt-out, AfterPause live rules / pause / errors already showing / submit / per-property pause; the old
  async debounce test now waits for the pause before the check starts). YFex.Validation 62/62.
- FormsSpike: ALL PASS on JIT and Native AOT (section 5 included); AOT warnings only the known `ChartSeriesBinder`
  (IL2075) and DataGrid (IL2104, IL3053).
- Shots `YFex.UI/artifacts/shots/fm-p4` (light and dark, 1440 px): the mixed XAML and markup forms render the same.

## 9. Findings and what is left

| # | Finding | Next |
|---|---|---|
| P4-1 | `[Computed]` and `[ReactsTo]` are not generated (the State generator's `ComputedProperties` is always empty); neither are method `[Debounce]`, `[Throttle]`, `[Cooldown]`, `[Poll]`, `[Busy]`, `[Queue]`, `[Gate]`, `[RequiresAll]`, `[ErrorBucket]`: declared attributes only | a YFex.State decision: build or remove them; property `[Debounce]` then covers `[Computed]` / `[ReactsTo]` too |
| P4-2 | `DebounceState.NextToken` resets its source with `TryReset()` when it was not cancelled, which returns a fresh token **without cancelling** the delay already waiting, so it may not debounce (`StateComponent` in Blazor uses it). Not verified by a test; the new code uses a version counter | verify in YFex.State, fix in the owner |
| P4-3 | `YFormSection` placing a **group** by key (not a child model) is not supported; groups come only through `YFormRest` or a generated form | `YFormSection Group="Contact"` if a screen needs it |
| P4-4 | `FieldOverride.Preset` waits for ⚑FM14 | — |
| P4-5 | A mixed form in XAML must use `DataContext` + `x:DataType` (Avalonia resolves only `DataContext` against the parent scope) | documented on `YForm` and in the gallery sample |
| P4-6 | FM-P3 findings P3-2 (catalog labels in messages), P3-3 (`YComboBox`/`YSwitch` without validation display) and P3-4 (server violations at form level) are still open | P3-3, P3-4 done in FM-P3 part 2 ([08](08-fm-p3-part2-summaries.md)); P3-2 is ⚑FM61 |
