# FM-P3 (part 1) Validation wiring, and the form layout reworked: built 2026-11-19

> Status: **built and committed 2026-11-19** (YFex.UI `4feeef7`). Asked by the user 2026-11-19: start FM-P3 with the decisions left for later
> (⚑FM50 summaries, ⚑FM19 `YDetails` masks, ⚑FM33 placement stay pending), and improve the visual distribution of fields
> (holes between fields, structure, responsiveness), following web form conventions.

## 1. Layout: what was wrong and what the web does

The user marked holes in the gallery forms (Zip → Street, Number → City, after State, between Tiny/Small fields). Causes in
the FM-P1 flow:

1. **"A field never straddles a column"**: a field that did not fit the rest of a column jumped to the next column, leaving a hole.
2. **Rows never filled**: what a row left at its end stayed empty.
3. **Uneven gutters**: inside a column fields were half a gap apart, across columns a full gap.
4. **Sections indented** behind a start border, so their fields did not line up with the fields around them.
5. **One-column width caps on phones**: at phone width fields stopped at their width token (half the screen) with ragged right edges.

How web form layouts handle it (Bootstrap and Ant Design 12/24-column spans, Material 3 layout grid and window size classes,
Carbon 16-column grid; NN/g on single-column forms): one uniform grid, **the same gutter between every track**, fields span N
tracks and may start on any track; designers make rows add up, and flex layouts grow (and shrink) fields to fill a row;
container width picks 1 / 2 / 3 columns (Material 3 compact < 600, medium < 840, expanded); on phones fields are full width;
sections are a heading on the same grid, separated by a divider, not an indented box.

## 2. The new flow (`YFormPanel`, `FormColumns`)

| Rule | Before | Now |
|---|---|---|
| Grid | 6 units per column, half gap inside a column | **6 tracks per column, one uniform gutter** (`ColumnSpacing`) |
| Start | at the next column when a field does not fit the rest of the column | **any track** (fields pack, no holes) |
| Row end | left empty | **`FormColumns.FillRow`**: the row's empty tracks go to its Medium and wider fields equally; a row of only Tiny/Small fields grows each up to one column (a lone date stays a date's width) |
| Large | wraps when 12 tracks do not fit | **shrinks to one column to finish a row** (`MinUnits`), so `Kind | Code | Name` stay together |
| Width caps | every one-column form | only when one column is **forced** on a wide container (`MaxColumns="1"` in a dialog); on narrow screens fields fill the width |
| Columns | most of `MaxColumns` whose 280 px minimum fits, 40 px hysteresis | unchanged (≈ Material 3 breakpoints) |
| Sections | indented, start border | **on the form's grid**, hairline divider above the heading |

Shots: `artifacts/shots/fm-p3` (1440 light/dark, 820 tablet, 460 phone, Save clicked).

## 3. FM-P3 part 1: validation wiring

| Piece | Where | What |
|---|---|---|
| Wiring | `AvaloniaEditorFactory.Validated` | every text-based generated editor (text, numbers, parsed and registered value inputs, dates) gets `ValidationSource` = the model when it is an `IValidationSource` (a validated YFex.State view model) and `ValidationPath` = the field; a child model validates its own fields. The input already reads constraints (max length, required), visible messages (`INotifyDataErrorInfo`), status and calls `Touch` on blur |
| Required mark | `YTextInput.DisplayDescription`, `:required` | required fields read "Name *" (the template binds `DisplayDescription`); the accessible name stays the label, the peer reports IsRequiredForForm. Applies to every `YTextInput`, generated or not |
| Focus first invalid | `YForm.FocusFirstInvalid()` | after `ValidateAll()` (or server results), focuses the first field in form order with an invalid draft or an Invalid status; opens collapsed sections and builds a section waiting to be shown when it holds the error |
| Server violations | existing `ApplyServer<TContract>(…)` on validated view models | lands on the same fields through `ViolationsChanged`; **not covered by a Forms test yet** |
| Gallery | Forms page "Validation" | validated model + Save (`ValidateAll` then `FocusFirstInvalid`), code sample `Forms.Validation` |

Not built (waiting on decisions): summaries per section/form and error dots on collapsed sections (⚑FM50), `YDetails` with
`[Sensitive]` masks (⚑FM19). Not wired: `YComboBox` and `YSwitch` (no helper line or error state of their own yet).

## 4. Checks

- `FormsAndPickersTests`: layout tests rewritten for the new rules (row fill, small rows, Large shrinking, even gaps, any-track start, narrow screens fill the width).
- `FormsValidationTests` (5): constraints and required mark, messages after `ValidateAll`, focus first invalid, opening and building the section with the error, all valid → false.
- Suite: Avalonia 412/413 (the known `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent`), generator 18/18.

## 5. Findings and what is left

| # | Finding | Next |
|---|---|---|
| P3-1 | Grown fields no longer line up across rows (Code ends at track 2, Kind at 6): the price of rows without holes | acceptable; designers keep alignment by choosing sizes that add up |
| P3-2 | Validation messages use the field key ("Name é obrigatório"), not the catalog label | ⚑FM61 ([08](08-fm-p3-part2-summaries.md) P3b-1) |
| P3-3 | `YComboBox`/`YSwitch` have no validation display | **done in part 2** ([08](08-fm-p3-part2-summaries.md)) |
| P3-4 | Server violations through `ApplyServer` not tested at form level; collection paths (`Contacts[2].Phone`) wait for FM-P6 | **form level done in part 2** (bag `ApplyServer`; the `[ValidatesAs]` path mapping is Validation's own test); collections FM-P6 |
| P3-5 | AOT spike still covers FM-P0 features only (P2-3) | **done**: sections 5 (FM-P4) and 6 (validation, part 2) |
