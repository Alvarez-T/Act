# FM-P2 Shapes: built 2026-11-19

> Status: **built and committed 2026-11-19** (YFex.UI `5b9c0ec`). Decisions used: ⚑FM3, FM8, FM21 (agreed), FM11, FM12 (decided 2026-11-19), FM5 and
> FM47 (no `[InputHints]`; each value type's own input, registered per type). Deferred by the user: ⚑FM53, ⚑FM7 / `YForm.Label`.

## 1. What was built

| Area | Where | What |
|---|---|---|
| Attributes | `YFex.UI.Forms/FormAttributes.cs` | `[Field]` gains `Group`, `VisibleIn`, `EditableIn` (`FormModes`), `VisibleWhen`, `ReadOnlyWhen`; new `[FieldGroup(key)]` (`Columns`, `Collapsible`, `StartsCollapsed`, `BuildWhenShown`); the conventions are documented on `[Form]` |
| Shapes | `FieldShape.cs`, `FormShape.cs`, `FormEnums.cs` | `FormModes`; fields carry `Group`, modes, `VisibleWhen`/`EditableWhen` delegates and `DependsOn`; `ChildField<TModel, TChild>` (a section with the child's shape); `FieldGroupShape` with `Is{Group}Visible`; `IEditorFactory.Child` |
| Generator | `YFex.UI.Forms.Generator` | child models (a property whose type has `[Form]`), groups, modes, conditions from `Is{Field}Visible` / `Can{Field}Edit` / `VisibleWhen` / `ReadOnlyWhen` (joined with `&&`), **what each condition reads** (identifiers of model properties in its body; unknown → re-checked on any change); condition members are not fields (a settable bool named by a condition stays a field) |
| Analyzers (⚑FM11) | same | YFFORM004 `Is{X}Visible`/`Can{X}Edit` with no field or section X; YFFORM005 `VisibleWhen`/`ReadOnlyWhen` naming a missing or non-bool member; YFFORM006 `[Field]`/`[FieldGroup]` outside a `[Form]` model; YFFORM007 `[FieldGroup]` key matching no section; YFFORM008 a child of the model's own type |
| Avalonia | `Controls/Forms` | `YForm.Mode`, `YForm.Labels` (inherited) + static `DefaultLabels` (catalog keys `{Model}.{Field}`, `{Model}.{Group}`; debug message per missing key); groups → one `YFormSection` placed at the first field; child models → sections that follow a replaced child; `ConditionWatch` re-applies only the dependent fields; `YFormSection.IsBuilt` + build when first shown (⚑FM12 option); `YFormEditors` registry (`Register<T>`, `RegisterValue<T>`; core types built in: e-mail, phone, money, mass, length, quantity, temperature) |
| Gallery | Forms page | examples in `GalleryCodeExample` with their code (`CodeSamples/Forms.*.md`): generated, modes and conditions, sections, catalog labels, placed by hand (sizes, sections), fluent markup; plus widths and stress |

## 2. Checks

- Generator tests `YFex.UI.SourceGenerator.Tests/FormGeneratorTests` (8): emitted code (groups, modes, child, conditions and what they read; condition members are not fields) and YFFORM004–008.
- Behaviour tests `YFex.Avalonia.Tests/FormsShapesTests` (9): modes, conventions follow their reads, `ReadOnlyWhen`, group placement and columns, child section and a replaced child, build when shown, registered inputs, catalog labels with fallback, detach stops listening.
- Suite: Avalonia 403/404 (the known `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent`), generator tests 18/18.
- FormsSpike: Native AOT publish, ALL PASS; only the existing `ChartSeriesBinder` and DataGrid trim warnings. The spike model does not use groups or conditions yet (see P2-3).
- Look: GalleryShots *Forms*, `artifacts/shots/fm-p2` (light, dark, tall).

## 3. Findings and what is left

| # | Finding | Next |
|---|---|---|
| P2-1 | Dependency reading is syntactic (identifiers that name model properties); a condition calling a helper method that reads other properties is re-checked only on those it names directly | acceptable; `[Field(DependsOn)]` only if a real case needs it |
| P2-2 | Two child models that contain each other (A → B → A) are not diagnosed (only a child of the model's own type, YFFORM008) | diagnose cycles if one appears |
| P2-3 | The AOT spike checks the FM-P0 features only | add groups, a child model and a condition to the spike model |
| P2-4 | `Labels` missing-key detection compares with `MessageCatalog.Humanize` (a key whose real label equals the humanized name is reported once as missing) | a `TryFieldName` on `IMessageCatalog` would make it exact (core change, ⚑ when needed) |
| P2-5 | `FieldPreset` (⚑FM14), `ConfigureForm` block, profiles (⚑FM34) not built | later phases |
