# 04 — FM-P1: form layout and the pickers (2026-11-18)

> Part of the [forms design](README.md). Built in YFex.UI, **uncommitted**. The user asked to include the date picker
> and the other controls the forms need, taken from the Avalonia components plan (C7, C11, C13).

## 1. What was built

| Path (YFex.UI `src/Avalonia/YFex.Avalonia`) | Holds |
|---|---|
| `Controls/Forms/FormColumns.cs` | the arithmetic, framework-free: columns for a width with 40 px hysteresis (port of the old `FormSizeResolver`), units per size |
| `Controls/Forms/YFormPanel.cs` | the flow panel: 6 units per column, row by row, a field never straddles a column, `MaxColumns` (1–3, **inherited**), `ColumnSpacing` (`SpacingMedium`), `RowSpacing` (`SpacingSmall`), `ColumnMinWidth` (`YFormColumnMinWidth`), read-only `ActiveColumns`; width tokens only in one column |
| `Controls/Forms/YForm.cs` | `YForm : YFormPanel`: attached `YForm.Size` and `YForm.NewRow` (any control); `Source` builds the generated editors (FM-P0) and now gives each one its size (`[Field(Size)]`, else Small for numbers/dates/toggles, Medium otherwise) |
| `Controls/Forms/YFormSection.cs` + `YForm.axaml` | `YFormSection : HeaderedItemsControl`: header (plain text, or a ghost `YButton` with a turning chevron when `IsCollapsible`), hairline start border, its fields in a `YFormPanel`; Full by default; `IsExpanded`, `MaxColumns`; closed = fields not measured (no templates); ExpandCollapse automation peer. Width tokens `YFormFieldWidth{Tiny,Small,Medium,Large}`, `YFormColumnMinWidth` |
| `Controls/Dates/YCalendar.cs`, `YCalendarDay.cs`, `YCalendar.axaml` | plan C11, single selection (see the plan's status line) |
| `Controls/Dates/YDatePicker.cs` | plan C13, single date: typed (culture or digits) or picked from a `YFlyout` + `YCalendar` |
| `Controls/Inputs/YComboBox.cs(.axaml)` | plan C7, single selection, field look of the inputs, token items with a check |
| icons `Calendar`, `Checkmark` | added to `Icons.fluent.txt` + `IconKey`, regenerated |
| `Controls/Forms/AvaloniaEditorFactory.cs` | generated forms use `YDatePicker` for `DateOnly` and `YComboBox` for enums |
| gallery pages *Forms* and *Pickers* | generated form with a column switch, every size, sections, 320 / 760 px forms, 280 px stress; date picker states (value, range, invalid draft, read-only, disabled), two calendars, combo boxes (1,000 items, disabled), stress |

**Owner fix:** `YParsedInput.OnApplyTemplate` replaced a draft set before the template (a `Text` in XAML) with the empty value. It now keeps the draft when there is no value. This applies to every parsed input. Regression test: `Text_set_before_the_template_stays_as_a_draft`.

## 2. Checks

- **Tests** (`FormsAndPickersTests`, 17):
  - columns and hysteresis, units;
  - row-by-row placement and column alignment, no straddling, new row, one-column caps;
  - section full row, collapse through automation, closed section not built;
  - generated form uses the date picker and combo box (sizes, automation id, two-way enum);
  - calendar: culture week start, marks, keyboard across months, range;
  - date picker: culture text, digits, invalid draft, out of range, F4 → pick → text;
  - combo box keyboard and label;
  - the owner fix.
- **Stress** (`FormsAndPickersStressTests`, 5 runs, all themes, 0 errors). Accepted warnings:
  - **calendar:** keeps its natural size (≈ 260 × 285) in containers smaller than that;
  - **date picker:** the first F4 takes ~150 ms because it builds the flyout and calendar on demand, which keeps a form of 60 date fields cheap;
  - **inherited native-font warnings:** undefined `FontWeight`/`FontStretch`/`TextWrapping` values;
  - **leak checks:** an intermittent "not collected" on `YComboBox` and `YFormSection`. It was not reproducible in 10 isolated runs, so it is GC timing, as for `YDialog`/`YDockTabs`.
  - Two properties are skipped, with the reasons in the test: `FontSize = double.MaxValue` on the calendar (the same Avalonia measure overflow as `YDialog`), and `ItemsSource` on a section whose `Items` are filled (Avalonia forbids both).
- **Suite:** 388/389. The failure is the known `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent`.
- **Spike** (FM-P0 app): 30/30 on JIT and Native AOT. There are no new AOT warnings; only the existing `ChartSeriesBinder` and third-party DataGrid warnings remain. With the heavier date and combo editors, a 60-field form is still in the same range as the same inputs built by hand. The timings were noisy on this run (AOT warm layout 93–160 ms for both).
- **Look:** GalleryShots *Forms* and *Pickers*, light and dark, `artifacts/shots/fm-p1`. Popups cannot open headless, so the calendar flyout and the combo drop-down are covered by tests, not images. Hover states were not checked.

## 3. Findings and what is left

| # | Finding | Next |
|---|---|---|
| P1 | Capping Medium/Large at their width token in a multi-column form left ragged right edges; caps now apply only in one column | design §4.4 updated (⚑FM6) |
| P2 | `YForm.Label` (a label for a control without one) is not built: a panel cannot add a label without wrapping the child, which ⚑FM7 rejects | use `YHeaderControl` or the control's `Description`; decide with ⚑FM7 |
| P3 | `YComboBox` keeps the platform popup (gap against plan B8) | decisions.md "Known gaps" |
| P4 | Inputs still use YTextInput's legacy literals (radius 8, border 1.5, padding 10,7); `YComboBox` uses tokens of the same values, so they match | plan C6 |
| P5 | Sections in generated forms (`[FieldGroup]`, child models) | FM-P2 |
| P6 | Fluent markup factories for `YForm`, `YFormSection`, `YDatePicker`, `YCalendar`, `YComboBox` | **done 2026-11-19**: `Helpers` factories (`YForm()`, `YFormPanel(…)`, `YFormSection(header)`, `YDatePicker()`, `YCalendar()`, `YComboBox()`), `Bind` overloads, `YFormExtensions` (`MaxColumns`, `FieldSize`, `NewRow`, `Collapsible`, `Fields`, `Range`, `Items`…); test `Markup_BuildsAFormWithPickersAndSections` |
| P7 | YFex.UI `AGENTS.md` layout table did not list `YFex.UI.Forms`, the generator and the spike | **done 2026-11-19** |
