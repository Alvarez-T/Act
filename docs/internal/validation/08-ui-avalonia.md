# 08 — UI integration (YFex.Avalonia, YFex.UI repo)

> Load for: **Phase 5**.
> Repo: `C:\Dev\YFex\YFex.UI` (read its `CLAUDE.md`: Avalonia 12, .NET 11, **no external deps beyond
> the framework SDK** — YFex.Validation/YFex.Rules are own libraries and are allowed).
> Relevant existing code:
> - `src/Avalonia/YFex.Avalonia/Controls/Inputs/YTextInput.axaml` — template already has
>   `<DataValidationErrors>` (≈ line 32) and a `^:error` style (≈ line 161)
> - `src/Avalonia/YFex.Avalonia/Controls/Inputs/*` — `YValueInput<T>` (Minimum/Maximum/Step/Clamp,
>   CommitMode), `YNumericInput<T>`, `YMoneyInput`, `YQuantityInput`, `YLengthInput`, `YMassInput`
> - `src/Core/YFex.UI.Markup/DefaultBindExtensions.cs` — `.Bind(object? expr, [CallerArgumentExpression] string expr)`
>   → `Platform.ApplyBinding(control, property, expr, mode)` in `Internal/Platform.cs`

Goal: a control bound to `vm.Name` gets, **with no manual code**: MaxLength/Min/Max/Step/options,
a required marker, errors with severity colors, "validating" state, and touch reporting.
Manual control stays possible everywhere.

---

## 1. Knowing the field path

| Authoring style | How the path is set |
|---|---|
| Fluent markup `YTextInput().Bind(vm.Name)` | `Platform.ApplyBinding` already receives `"vm.Name"`; strip the root identifier and `?.` → `"Name"` (`"vm.Address.City"` → `"Address.City"`) and set `YValidation.Field` |
| XAML explicit | `<y:YTextInput Text="{Binding Name}" y:YValidation.Field="Name"/>` |
| XAML markup extension | `<y:YTextInput Text="{y:Field Name}"/>` — creates the TwoWay binding **and** sets `YValidation.Field` via `IProvideValueTarget` |
| Opt-out | `y:YValidation.Enabled="False"` |

(Optional later: infer from an existing binding if Avalonia 12 exposes the binding path publicly.)

## 2. `YValidation` attached behavior

```csharp
namespace YFex.Avalonia.Validation;

/// Attached properties + behavior. On attach / DataContext change:
///  1. find the IValidationSource: the control's DataContext, else the nearest ancestor with
///     YValidation.Source set (form scope), else none (behavior idle);
///  2. read TryGetConstraints(Field) → apply to the control (IConstraintTarget, §3);
///  3. subscribe (weakly — no leaks when the VM outlives the view) to ConstraintsChanged /
///     ViolationsChanged for its path → re-apply constraints / update pseudo-classes;
///  4. on LostFocus / commit (YValueInput CommitMode) → source.Touch(Field).
/// Pseudo-classes set on the control: :required, :validating, :invalid, :warning, :info.
public static class YValidation
{
    public static readonly AttachedProperty<string?> FieldProperty;         // "Name"
    public static readonly AttachedProperty<bool> EnabledProperty;          // default true when Field set
    public static readonly AttachedProperty<IValidationSource?> SourceProperty; // form-scope override
    public static readonly AttachedProperty<FieldStatus> StatusProperty;    // read-only mirror for styling/templating
}
```

## 3. Control contracts

```csharp
/// Implemented by YFex controls that can enforce constraints. Projection never REPLACES explicit
/// local values: it writes to separate "constraint" properties and the control uses the most
/// restrictive of (explicit, constraint).
public interface IConstraintTarget
{
    void ApplyConstraints(in FieldConstraints constraints);
}
```

| Control | What it applies |
|---|---|
| `YTextInput` | `MaxLength` (constraint max), `:required` marker on the description/header (asterisk), optional char counter in `HelperText` ("12/80") when `ShowCounter` is on, `Format` → mask hint (e.g. `br.document`) |
| `YValueInput<T>` (+ numeric/money/quantity/length/mass) | new `ConstraintMinimum`/`ConstraintMaximum`/`ConstraintStep` (internal styled props); `Clamp` and steppers use `max(Minimum, ConstraintMinimum)` / `min(Maximum, ConstraintMaximum)`. Conversion from `RawValue`: new `protected virtual bool TryConvertBound(in RawValue v, out T value)` — numerics via generic math `T.CreateChecked`, `YMoneyInput`/`YQuantityInput`/`YLengthInput`/`YMassInput` override |
| Combo/pickers (`ComboBox`, `YTabStrip`-like selectors, future `YOptionPicker`) | `IOptionsTarget.ApplyOptions(in OptionSet)` → items outside the set are disabled (or hidden with `HideDisallowed=true`) |
| `CheckBox`/`ToggleSwitch` | `:required` only |

```csharp
/// Discrete-choice controls. Empty OptionSet = no restriction.
public interface IOptionsTarget { void ApplyOptions(in OptionSet allowed); }

/// Generated per decision table (08 §7) — lets one non-generic editor edit any typed table without reflection.
public interface ITableEditorAdapter
{
    TableSchema Schema { get; }
    int RowCount { get; }
    RawValue GetCell(int row, int column);
    bool TrySetCell(int row, int column, in RawValue value, ref ViolationWriter w);   // validates the row
    void InsertRow(int index); void RemoveRow(int index); void MoveRow(int from, int to);
    bool CheckTable(ref ViolationWriter w);                                          // overlaps/gaps/guardrails, paths "Rows[i].Column"
    ReadOnlyMemory<byte> SerializeRows();                                            // payload for PublishTable
}
```

Dynamic constraints (a new YFex.Rules Decision) arrive through the same path (`ConstraintsChanged`),
so e.g. `YIntegerInput().Bind(vm.Installments)` gets `Maximum = 6` from the Decision automatically.

## 4. Fluent markup additions (YFex.UI.Markup)

```csharp
YTextInput().Bind(vm.Name)                               // auto: path "Name"
YIntegerInput().Bind(vm.Installments)                    // auto: static + dynamic constraints
YTextInput().Bind(vm.Notes).Validation(false)            // opt-out
YTextBlock().Bind(vm.NameError)                          // manual: generated first-message property (optional, §9)
YValidationSummary().Source(vm)                          // form-level + (optionally) all visible errors
YRuleBreakdown().Explain(vm.Policy)                      // §6
```

`Platform.ApplyBinding` change: after creating the binding, if the target implements
`IConstraintTarget` (or is in the known list), set `YValidation.Field` from the expression.

## 5. Error presentation

- INDEI objects are `ErrorItem` (07 §2) → add a `DataTemplate` for `ErrorItem` in the theme:
  icon + message, color by `Severity` (Error = danger token, Warning = warning token, Info = neutral).
- Pseudo-classes drive styling: `^:invalid` (replaces reliance on `:error` for severity-aware looks),
  `^:warning`, `^:validating` (small spinner in the suffix area of `YTextInput`).
- Messages are already formatted by the catalog (`ErrorItem.Message`); controls never format.
- `YValidationSummary`: list of form-level violations (empty path) + optional "all visible" mode;
  clicking an entry focuses the control whose `YValidation.Field` matches the path.

## 6. `YRuleBreakdown`

Renders an `Explanation` (06 §5) — never interprets rules.

```csharp
public sealed class YRuleBreakdown : TemplatedControl
{
    public static readonly StyledProperty<Explanation?> ExplanationProperty;
    public static readonly StyledProperty<bool> ShowNotAppliedProperty;   // "Não aplicados" section
    public static readonly StyledProperty<bool> ShowRuleRefsProperty;     // "loyalty.gold · v3 · Tenant" (admins/support)
    public event EventHandler<RuleRefEventArgs>? RuleInvoked;             // click a line → open "rule as applied" (GetRuleDefinition)
}
```

`.Explain(vm.Policy)` markup binds `Policy` and converts with `decision.Explain(catalog, culture)`
using the `IMessageCatalog` registered in `YFexAvaloniaServices`. Works for any rule set
(discounts, freight, commission, fiscal).

## 7. `YDecisionTableEditor` (runtime table editing, D11)

- Non-generic control; per-table **generated adapter** (`InstallmentTable.EditorAdapter : ITableEditorAdapter`)
  gives columns (`TableSchema`), get/set cell as `RawValue`, row validation (Row is `[Validatable]` +
  guardrails) and table checks — no reflection.
- Cell editors by column kind: `Match<enum>` → multi-select with "Qualquer"; `Band<Money>` → from/to
  money inputs; numbers → `YIntegerInput`/`YDecimalInput` with the Row member's constraints.
- Live validation per cell (paths `Rows[i].Column`), row reorder (matters for `HitPolicy.First`),
  sealed rows read-only, "compare with published version" diff, effective date + scope + note,
  **Publish** button bound to the `PublishTable` command (needs the permission; hidden otherwise).
- Parameter editor: generated form per rule's `RuleParameter`s with guard bounds.

## 8. Approval flow (RequireApproval)

1. VM: `Policy.Verdict.IsNeedsApproval` → shows "Solicitar aprovação" instead of "Fechar venda".
2. Clicking opens an `IDialog` (YFex.UI.Abstractions) asking a manager to authenticate for the
   permission in the `ApprovalRequest`.
3. The server issues an `OverrideToken` bound to (decision fingerprint, permission, approver) (09 §6).
4. VM stores the token, re-sends the command with it; breakdown shows "Aprovado por Maria (gerente)".

## 9. Manual binding (optional generated members)

For screens that want explicit bindings, the Validation generator can emit per validated VM property
(opt-in `[ValidationOptions(EmitErrorProperties = true)]`):
`public string? NameError { get; }` (first visible message) and `public FieldStatus NameStatus { get; }`,
raised on `ValidationChanged`.

## 10. Other frameworks

Author in Avalonia first; bring it to the other platforms with the YFex.UI `yfex-ui-port` skill:
- Uno: same attached-property design.
- Blazor (`YFex.State.Blazor`): components take a `Field` parameter and a cascading `IValidationSource`.

## 11. Tests
Headless tests in `YFex.Avalonia.Tests`: constraint projection (static + dynamic change), explicit
local values not overridden, Touch on blur, pseudo-classes, `ErrorItem` template, breakdown rendering,
table editor cell validation and publish enabling.
