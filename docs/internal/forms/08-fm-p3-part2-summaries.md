# FM-P3 part 2: validation on every editor, summaries, error counts (built 2026-11-19)

> Status: **built 2026-11-19, uncommitted.** Asked by the user 2026-11-19 ("Yes, start it"). That answer approved the
> scope: ⚑FM50 (summaries) and ⚑FM33 (validation/08's control half lives in YFex.UI). `YDetails` moved to part 2
> (⚑FM60). Builds on part 1 ([06](06-fm-p3-validation-and-layout.md)) and FM-P4 ([07](07-fm-p4-authoring.md)).

## 1. What was built

| Piece | Where (YFex.UI, `src/Avalonia/YFex.Avalonia`) | What |
|---|---|---|
| `ValidationTracker` (internal) | `Attached/ValidationTracker.cs` | follows one field of a source for one control. It reads status, the first visible message (from `INotifyDataErrorInfo`, formatted by the source), the worst visible severity and constraints. It refreshes on `ViolationsChanged` / `ErrorsChanged` / `ConstraintsChanged` on the UI thread, and does Touch. `YTextInput` and `YValidation` share it (one way to read validation) |
| `YValidation` attached behavior (⚑FM33, validation/08 §2) | `Attached/YValidation.cs` | `Field` (path), `Source` (inherited: a form or panel sets it once; unset uses `DataContext`) and `IsEnabled`.<br>Outputs: `Status`, `Message`, `HasError`, `HasWarning`, `IsRequired`; pseudo-classes `:invalid`, `:warning`, `:validating`, `:required`.<br>Accessibility: the message is the help text and the peer reports required.<br>Touch when keyboard focus leaves the control, an item is picked (`SelectionChanged`) or a toggle flips. Works on **any** control |
| `YTextInput` on the tracker | `Controls/Inputs/YTextInput.cs` | its own subscription code is replaced by the tracker. It also reads `YValidation.Field` / `Source` and publishes the same outputs and pseudo-classes, so a form can ask any editor the same way |
| `YComboBox` (P3-3) | `Controls/Inputs/YComboBox.*` | `HelperText`, `DisplayHelperText` (visible message, else helper text) and `DisplayDescription` ("Kind *" when required). The helper line is docked under the field; the danger or warning border comes from `:invalid` / `:warning` |
| `YSwitch` (P3-3) | `Controls/Buttons/YSwitch.*` | message line under the switch and a danger or warning track border |
| Generated editors | `Controls/Forms/AvaloniaEditorFactory.cs` | toggle, choice and registered non-text inputs now validate through `YValidation` (text inputs as before) |
| `FieldSlot` content | `YForm` builder | the view's own control gets `YValidation.Field` and `Source` unless it names its field itself. It is a field entry, so `FocusFirstInvalid` and the summaries reach it (focus goes to the first focusable control inside) |
| `YValidationSummary` (⚑FM50) | `Controls/Forms/YValidationSummary.cs`, theme in `YForm.axaml` | header plus one line per problem: `ValidationSummaryItem(Field, Message, Severity, Activate)` reads "Field: message", and clicking a line runs `Activate` and raises `ItemInvoked`. Hidden while empty. `:error` / `:warning` tint, live region (assertive) so screen readers announce it |
| Summaries in `YForm` | `Controls/Forms/YForm.cs` | `ShowSummaries` (default true; only models with validation) and `FormSummary`.<br>Each section ends with a summary of its own fields plus one line per inner section.<br>The form ends with one for: form-level problems (empty path; server results without a field) first, then its own fields, then "N problems in Section" per section (click opens it and focuses its first error).<br>Refreshed after violation changes and when a section is built (once per burst, background priority). Sections not built yet are counted from the model |
| Error count on sections | `YFormSection.ErrorCount`, `:haserrors`, `PART_ErrorCount` | a danger badge with the count beside the header while the section is closed |
| Summary texts | resources `YFormSummaryOne/Many/InOne/InMany` (`{0}` count, `{1}` section) | English defaults in `YForm.axaml`; apps translate by defining the same keys |

Not built (left on purpose):
- the "why?" link with `YRuleBreakdown` (validation/08 §6): it is useful only once a screen uses Rules decisions, so it moves to part 2 or later;
- constraint projection beyond required and max length (`IConstraintTarget`, min/max/step on value inputs, `IOptionsTarget`);
- automatic `YValidation.Field` from fluent `.Bind()`;
- error dots on tabs (no form tabs yet; ⚑FM49).

## 2. Checks

- `FormsSummaryTests` (7):
  - combo and switch show their field's error;
  - any control through `YValidation`, including Touch on leaving and opting out;
  - a slot's own control is validated and focused;
  - section and form summaries, the error count on a closed section, clicking lines;
  - server results at form level, with a form-level problem first (**P3-4 done**);
  - headers from app resources;
  - `ShowSummaries=False`.
- Stress (`FormsAndPickersStressTests`), with `YValidationSummary` and a validated `YForm` added: no errors.
  - Fixed: a stacked helper line let a huge font measure the combo to infinity (now docked), and a long summary header did not wrap.
  - Accepted warnings: a 1,000-character header grows taller than its box; the first key presses are slow while the code warms up; `FontWeight = -1` throws in Avalonia; the intermittent "not collected" on `YComboBox` was already known (04).
- Suite: Avalonia 432/433 (the known `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent`).
- AOT spike: new section 6 (`SpikeValidated`: a combo with a rule, summaries, error count, clicking a line), **ALL PASS on Native AOT**. It shows only the known warnings: ChartSeriesBinder IL2075 and DataGrid IL2104/IL3053. This also adds validation coverage the spike lacked (**P3-5 done**).
- Gallery: the Forms "Validation" example has a required kind combo and a **Server answer** button (a form-level problem plus one on E-mail, through an app message layer `GalleryMessages`). Shots in `YFex.UI/artifacts/shots/fm-p3b` (after Save and after the server answer, light and dark).

## 3. Findings

| # | Finding | Next |
|---|---|---|
| P3b-1 | **P3-2 still open: messages name the field by its key, not the form's label.** `ErrorItem.Message` (YFex.State) takes the field name from `ValidationMessages.Catalog` by `{Type}.{Path}`, while the form takes labels from `YForm.Labels` / `DefaultLabels` by the same key. One app catalog for both fixes it for app-wide labels; a per-form `Labels` cannot reach the messages without a change across core, State and UI | ⚑FM61 (asked of the user) |
| P3b-2 | A summary line repeats the field ("Name: Name é obrigatório") because the built-in messages start with the field name | decide with ⚑FM61 (e.g. a short form of the message for summaries) |
| P3b-3 | In the gallery, messages follow the machine's UI culture (pt-BR) while the summary headers use the English defaults; an app sets both | app setup, documented on `YForm` |
| P3b-4 | `FormLevel` pairs violations with `GetErrors(null)` messages by position (YFex.State lists form-level items first) | fine for YFex.State models; a dedicated "visible items of a path" on `IValidationSource` would remove the assumption |
| P3b-5 | A summary line names a field by its label only; two fields with the same label in different sections read alike | the line could carry the section; wait for a real screen |
