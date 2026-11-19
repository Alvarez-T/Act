# 03 — FM-P0 spike results (2026-11-18)

> Part of the [forms design](README.md). Phase FM-P0: generator for one model with the five editor kinds, a bare `YForm`,
> Native AOT publish, build time of a 60-field form. Built in YFex.UI, **uncommitted**.

## 1. What was built

| Path (YFex.UI) | Holds |
|---|---|
| `src/Core/YFex.UI.Forms` | framework-neutral: `[Form]`, `[Field(Order, Size, Hidden, NewRow)]`, `FieldSize`, `EditorKind`, `FormShape<TModel>`, field classes per editor family (`TextField`, `ToggleField`, `NumberField<,T>`, `ParsedField<,T>`, `ChoiceField<,T>`, `DateField`), `IEditorFactory<TControl>` visitor, `FormShapes` registry. `IsAotCompatible`, BCL only |
| `src/Core/YFex.UI.Forms.Generator` | netstandard2.0 incremental generator (`ForAttributeWithMetadataName`): `[Form]` class → static `Form` shape with typed `static m => m.X` accessors, module-initializer registration; YFFORM001 (bad model), YFFORM002 (type without editor, warning), YFFORM003 (`Form` name taken) |
| `src/Avalonia/YFex.Avalonia/Controls/Forms` | `YForm` (Panel: `Source`, `MaxColumns` 1–3, spacing tokens; spike column flow), `AvaloniaEditorFactory`, `FieldLink` (two-way link through the accessors, detached when the form leaves the tree) |
| `src/Avalonia/YFex.Avalonia.FormsSpike` | headless console: 30 checks, timings, `CompiledBinding.Create` probe; `PublishAot` |

Editors used: `YTextInput`, `YSwitch`, `YNumericInput<T>` and `YParsedInput<T>` (through internal sealed generic subclasses, both bases are abstract), `ComboBox` under a `YHeaderControl` for enums, `YParsedInput<DateOnly>` for dates.

## 2. Results

**Correctness: 30/30 checks pass on JIT and on Native AOT.** Covered:
- declaration order plus `[Field(Order)]`;
- hidden fields, and types without an editor left out with a warning;
- init-only → read-only;
- `Full` size;
- labels;
- path `Model.Property`;
- registration;
- links in both directions for each editor family;
- invalid drafts kept in the input;
- non-nullable numbers keep their value on empty input;
- three columns at 1280 px;
- handlers dropped on detach.

The State generator and the Forms generator work side by side on `[Observable] partial` properties (pitfall 4: confirmed).

**AOT publish: no IL warning from Forms, YForm or `CompiledBinding.Create`.** The three warnings left are not Forms:
- `ChartSeriesBinder.ResolveValuePath` (IL2075, reflection on a value path);
- `Avalonia.Controls.DataGrid` (third party, IL2104/IL3053).

**Timings: 60 fields (20 text, 20 numbers, 10 e-mail/phone, 5 enums, 5 dates), 1280 px, 3 columns, Skia headless, ms. Each case runs in its own process; warm = median of 40 runs, with a GC before each.**

| Runtime | Case | Build | Layout cold | Layout warm | Render cold | Render warm |
|---|---|---|---|---|---|---|
| JIT | `YForm` from the shape | ~1 | 145–205 | 64–101 | 27–29 | 16–28 |
| JIT | the same 60 inputs by hand (Grid, no bindings) | 6 | 154–160 | 93–97 | 32–34 | 21–24 |
| AOT | `YForm` from the shape | 0.4 | 53–64 | 61–82 | 22–26 | 15–16 |
| AOT | by hand | 0.3 | 55–61 | 61–70 | 21–26 | 14–16 |

Reading:
- **The forms layer costs nothing measurable.** Building the editors from the shape takes under 1 ms. The rest is the same with or without forms, within run-to-run noise: it is the inputs' own template and layout work, roughly 1 ms per input on AOT and 2.5–3.5 ms on a cold JIT.
- **Pitfall 8 is real for JIT apps.** A cold 60-input screen takes about 175–230 ms to its first frame, close to the R9 budget. Collapsed sections and tabs built on first open (FM-P1/FM-P2) stay necessary. A faster input template would help every screen, not only forms; that belongs to the input controls (StressKit timings).

## 3. Decisions the spike settles or informs

| ⚑ | Spike result |
|---|---|
| FM1 placement | works as proposed: core `YFex.UI.Forms` + generator in YFex.UI, controls in YFex.Avalonia; YFex.Avalonia stays free of State/Mvvm |
| FM2 mechanism | generator → static shape: works, no reflection, AOT-clean |
| FM8 editor choice | visitor dispatch works: `YParsedInput<EmailAddress>` and `YNumericInput<decimal>` are created with T known at compile time; no `MakeGenericType` |
| FM9 binding | generated accessors + `FieldLink` work on JIT and AOT. **`CompiledBinding.Create<TIn,TOut>(expr)` also works on AOT with no warning** (both directions). Recommendation stays with generated accessors (no expression trees, no `DataContext` needed, one link type per field family); `CompiledBinding.Create` is a valid tool for hand-written code-behind |

## 4. Findings for later phases

| # | Finding | Where it goes |
|---|---|---|
| S1 | **Native AOT apps using `YFexTheme` crashed at startup** ("No precompiled XAML found"): the theme loads `_index.axaml` and density dictionaries by URI, and trimming removed Avalonia's compiled loader. **Fixed in the owner**: `YFexTheme.Include` keeps `CompiledAvaloniaXaml.!XamlLoader` with `[DynamicDependency]` | YFex.UI, done (uncommitted) |
| S2 | Generator diagnostics ignore `#pragma warning disable`; only `NoWarn`/`.editorconfig` silence YFFORM002 | FM-P2: move the per-property checks to a `DiagnosticAnalyzer` so pragmas work |
| S3 | No Y date input and no Y choice (combo) control; the spike used a parsed `DateOnly` text input and a plain `ComboBox` | **done in FM-P1**: `YDatePicker`, `YCalendar`, `YComboBox` ([04](04-fm-p1-layout.md)) |
| S4 | `YParsedInput<T>` and `YNumericInput<T>` are abstract, so Forms adds internal sealed generic subclasses | decide in FM-P1: make the bases concrete in their owner, or keep the internal subclasses |
| S5 | Model authoring traps with YFex.State: a property named `IsActive` hides `StateObject.IsActive`; partial properties cannot have initializers; a non-nullable `string` set in the constructor gives CS8618 on the generated backing field | document in the Forms authoring guide; CS8618 is a State generator issue to raise there |
| S6 | Every `[Form]` shape is created at module load (delegates for all fields) | FM-P2: register a factory and build the shape on first use if startup time shows it |
| S7 | Pre-existing AOT warning in YFex.Avalonia: `ChartSeriesBinder.ResolveValuePath` uses `GetProperty(string)` | YFex.UI charts, outside Forms |

## 5. How to run

```bash
dotnet run -c Release --project src/Avalonia/YFex.Avalonia.FormsSpike
dotnet run -c Release --project src/Avalonia/YFex.Avalonia.FormsSpike -- --case form
dotnet publish -c Release -r win-x64 src/Avalonia/YFex.Avalonia.FormsSpike
```
