# 01 — Current state, known bugs, migration map

> Load for: **Phase 0** (bug fixes) and **Phase 2/3/6** (migrations).

## 1. Inventory (as of 2026-09-29)

`YFex.Validation` does **not** exist. Its extraction was deferred in
`YFex.Domain/MIGRATION-NOTES.md` (§0, "Decision Phase E.5") and listed as a dependency in
`YFex/docs/internal/composer-design.md` §16 ("Composer rules produce neutral validation results").
*(That was composer-design rev 1; the gap is marked closed in rev 2 now that YFex.Validation exists.)*

### 1.1 Validation seed in YFex.State (zero consumers in the whole `C:\Dev\YFex` tree)

| File | What it is |
|---|---|
| `YFex.State/src/YFex.State/Validation/IValidator.cs` | `IValidator<T> { static abstract ValidationResult Validate(T value); }` and `IAsyncValidator<T> { static abstract ValueTask<ValidationResult> ValidateAsync(T, CancellationToken); }` |
| `YFex.State/src/YFex.State/Validation/ValidationResult.cs` | `readonly struct` with `Message`, `PropertyName`, `Severity`; success = `Message is null` |
| `YFex.State/src/YFex.State/Validation/ValidationSeverity.cs` | `Info, Warning, Error` |
| `YFex.State/src/YFex.State/Validation/ValidationBag.cs` | `Dictionary<string, ValidationResult>` — **one** result per property, `ValidationChanged` event, `GetErrors` for INDEI |
| `YFex.State/src/YFex.State/Attributes.cs` | `[ValidateWith(Type)]`, `[ValidateAsync(Type)]` (AllowMultiple) |
| `YFex.State/src/YFex.State/StateObject.cs:82-92` | lazy `Validation` bag + `ValidationIfCreated` |
| `YFex.State/src/YFex.State.Mvvm/MvvmStateObject.cs:67-101` | `INotifyDataErrorInfo` bridge (`HasErrors`, `GetErrors`, `ErrorsChanged`) |
| `YFex.State/src/YFex.State.Generator/ObservableParser.cs:124-126, 403-414` | resolves validator types |
| `YFex.State/src/YFex.State.Generator/ObservableEmitter.cs:162-171, 233-235, 263-298` | emits `Validate_X` / `Validate_X_Async` helpers and the setter call |
| `YFex.State/src/YFex.State.Generator/DiagnosticDescriptors.cs:67-73` | `YFEX0301` "Validator must implement static abstract interface" |
| `YFex.State/tests/YFex.State.Tests/Validation/*` | tests for bag/result/severity/contract — **no generator emission tests** |

### 1.2 Other validation result types

| Type | File | Problems |
|---|---|---|
| `YFex.Cqrs.ValidationResult` + `ValidationError` (class record) | `YFex.Cqrs/src/YFex.Cqrs/ValidationResult.cs` | allocates a list of records; duplicate of State's; name clash |
| `ICommandValidator<T>`, `IQueryValidator<T>` (instance, async) | `YFex.Cqrs/src/YFex.Cqrs/Configuration/Interfaces.cs` | resolved from DI by `Type` |
| `CommandConfigurationBuilder.Validate(...)`, `Validate<T>()`, `UseFluentValidator<T>()` | `YFex.Cqrs/src/YFex.Cqrs/Configuration/CommandConfigurationBuilder.cs:35-61` | **FluentValidation hard dependency in core Cqrs**; LINQ `Select`; `(TCommand)c` casts |
| `CommandPolicy.Validate : Func<object, CancellationToken, ValueTask<ValidationResult>>` | `YFex.Cqrs/src/YFex.Cqrs/Runtime/CommandPolicy.cs:20` (+ `QueryPolicy`) | object-typed → boxes struct commands |
| `ValidatorDescriptor` (`Inline`, `TypeRef`, `FluentTypeRef`) | `YFex.Cqrs/src/YFex.Cqrs/Registration/Descriptors.cs:11-29` | — |
| `LocalDispatcher.FormatErrors` | `YFex.Messaging/src/YFex.Messaging.Rpc/LocalDispatcher.cs:66-70, 132, 200, 348-360` | **flattens all violations into one `"; "`-joined string** → UI cannot map errors to fields |
| `Error` / `ErrorDetail` | `YFex.Cqrs/src/YFex.Cqrs/Error.cs` | `default(Error).Details` is `null` → `Details.FirstOrDefault()` throws NRE; LINQ |
| Fiscal `ResultadoValidacao` / `ViolacaoRegra` / `Severity {Info, Alarm, Blocking}` | `YFex.Fiscal/src/YFex.NFe.Public/Validation/ResultadoValidacao.cs` | separate on purpose (see MIGRATION-NOTES) — to be adapted, not deleted |
| Fiscal rule engine `IRegraNFe`, `RegraNFeBase`, `NFeRuleContext`, `FaixaNt`, `NtVersao`, `IConjuntoRegras` | `YFex.Fiscal/src/YFex.NFe/Rules/` | **the pattern YFex.Rules generalizes** (D5) |
| `XsdValidation` + `XsdValidator` facets | `src/XML/YFex.Xml/Facets/XsdValidation.cs` | single message, low-level; keep, add conversion |

## 2. Phase 0 — bugs to fix in YFex.State (before any migration)

All three bugs are latent because nothing uses `[ValidateWith]` yet. The **first** real usage breaks.

### Bug 1 — wrong arguments in the generated sync helper (compile error)

`ObservableEmitter.cs` emits:

```csharp
if (!__vr.IsSuccess) { __bag.Set(__vr.Message!, __vr.Severity); return; }
```

but the signature is `ValidationBag.Set(string propertyName, string message, ValidationSeverity severity = Error)`
→ `CS1503` (cannot convert `ValidationSeverity` to `string`). Same line in the async helper.

**Fix:** emit `__bag.Set("<PropertyName>", __vr.Message!, __vr.Severity);`

### Bug 2 — async-only property calls a helper that is never emitted

Setter emission (line ~234) calls `Validate_<Prop>(value)` whenever **either** sync or async
validators exist, but `Validate_<Prop>` is only emitted when **sync** validators exist; the async
helper is named `Validate_<Prop>_Async` and is **never called**.

**Fix:**
- sync only → call `Validate_X(value)`
- async only → call `Validate_X_Async(value)`
- both → `Validate_X(value)` returns `bool` (true = passed); call the async helper only when it passed.

### Bug 3 — `async void`, no cancellation, stale results

`private async void Validate_X_Async(T value)` with `CancellationToken.None`:
exceptions crash the process (async void), nothing is cancelled when the value changes again, and
a slow older result can overwrite a newer one.

**Fix (emitted shape):**

```csharp
private int __vver_Name;                                   // version: incremented on every new value
private global::System.Threading.CancellationTokenSource? __vcts_Name;

private void Validate_Name_Async(string value)
{
    __vcts_Name?.Cancel();                                 // cancel the previous in-flight check
    var cts = __vcts_Name = new global::System.Threading.CancellationTokenSource();
    int version = ++__vver_Name;
    _ = Validate_Name_AsyncCore(value, version, cts.Token); // Task-returning; never async void
}

private async global::System.Threading.Tasks.Task Validate_Name_AsyncCore(string value, int version, global::System.Threading.CancellationToken ct)
{
    try
    {
        var vr = await global::My.EmailNotTaken.ValidateAsync(value, ct).ConfigureAwait(false);
        if (version != __vver_Name) return;                // a newer value arrived → discard stale result
        if (!vr.IsSuccess) Validation.Set("Name", vr.Message!, vr.Severity);
        else Validation.Clear("Name");
    }
    catch (global::System.OperationCanceledException) { /* superseded */ }
    catch (global::System.Exception ex) { /* route to the same unhandled-error path [StateCommand] uses (see StateObject / [ErrorBucket]) */ }
}
```

> Marshal the bag update to the UI thread the same way `[ReactsTo(RunOnMainThread = true)]` does.

### Phase 0 tests (add generator emission tests — none exist today)

1. StateObject with `[ValidateWith(typeof(Sync))]` compiles; invalid value → `HasErrors`, `GetErrors("Prop")` returns the message.
2. Async-only property compiles and the async validator runs.
3. Sync + async: async runs only when sync passed.
4. Race: set value A (slow validator), then B (fast) → final state reflects **B**.
5. Validator throws → no crash; error routed.

Phase 2 later replaces this machinery; these tests become the regression net for the migration.

## 3. Migration map (Phase 2 / 3 / 6)

| Old | New | Compatibility |
|---|---|---|
| `YFex.State.Validation.IValidator<T>` | `YFex.Validation.IValueRule<T>` ⚑P1 | keep old interface `[Obsolete]` one release; generator accepts both |
| `YFex.State.Validation.IAsyncValidator<T>` | `YFex.Validation.IAsyncValueRule<T>` | same |
| `YFex.State.Validation.ValidationResult` | `YFex.Validation.Violation` (single) / `ValidationResult` (many) | old type `[Obsolete]` |
| `YFex.State.Validation.ValidationSeverity` | `YFex.Validation.Severity` | identical values (Info=0, Warning=1, Error=2) |
| `[ValidateWith(typeof(T))]` / `[ValidateAsync(typeof(T))]` | `[ValidateWith<T>]` (sync or async detected by interface) | old attributes `[Obsolete]`, still honoured by the generator |
| `ValidationBag` (string → one result) | `ValidationBag` (field id → violations with origin) — stays in YFex.State (07) | public API kept where possible (`HasErrors`, `GetErrors`, `ValidationChanged`) |
| `State generator Validate_X` emission | `partial void` hooks implemented by YFex.Validation.Generator ⚑P6 (03 §7) | — |
| `YFex.Cqrs.ValidationResult` / `ValidationError` | `YFex.Validation.ValidationResult` / `Violation` | breaking; update all usages in Cqrs/Messaging/tests |
| `ICommandValidator<T>` / `IQueryValidator<T>` | `IContextRule<T>` (02 §6) | keep the old ones `[Obsolete]`, adapt inside the builder |
| `CommandPolicy.Validate : Func<object,…>` | typed `ValidationSlot<T>` + context-rule array (09) | internal |
| `UseFluentValidator<T>()` in YFex.Cqrs | extension method in `YFex.Cqrs.FluentValidation` | same call site after adding the package |
| `LocalDispatcher.FormatErrors` string | `Error` carrying `ViolationList` (09 §5) | `Error.Message` still filled (first message code) for old callers |
| `Error.Details` null on default | fix: null-safe accessors, no LINQ | — |

### 3.1 Fiscal (Phase 6)

Fiscal intentionally owns Portuguese-named public types. Keep them as **facades**:

```csharp
// YFex.NFe.Public — after migration (sketch)
public readonly struct ResultadoValidacao
{
    private readonly ValidationResult _inner;               // YFex.Validation
    public bool Valido => _inner.IsValid;
    public ImmutableArray<ViolacaoRegra> Violacoes => …;    // projected lazily from _inner.Violations
    public ValidationResult AsValidationResult() => _inner;  // for Cqrs/UI
}
```

| Fiscal | YFex.Validation |
|---|---|
| `ViolacaoRegra.RuleCode` | `Violation.Code` (`"nfe.<code>"`) |
| `ViolacaoRegra.Caminho` | `Violation.Path` (XML path → `FieldPath` template) |
| `ViolacaoRegra.Mensagem` | message catalog entry for the code (module catalog `YFex.NFe`) |
| `Severity.Blocking / Alarm / Info` | `Severity.Error / Warning / Info` ⚑P4 |
| `ViolacaoRegra.RejeicaoCStat`, `ImpactoFinanceiro` | `Violation.Args` (cStat as arg 0) + fiscal extension data kept in the facade |
| `IRegraNFe` / `RegraNFeBase` / `NFeRuleContext` | `IRule<NFeSituation>` with `[Rule("nfe.xxx")]`, `Applies` = `Modelo`/`FaixaNt` filters (04 §11) |

### 3.2 Xml

Add `public static Violation ToViolation(this XsdValidation v, in FieldPath path, RuleCode code)`
in YFex.Xml (YFex.Xml → YFex.Validation reference). `XsdValidation` itself stays.

### 3.3 Out of scope but noticed

`CommandConfigurationBuilder.Invalidates(Expression<Func<TQuery,TCommand,bool>>)` compiles an
expression tree (interpreted under AOT). Not part of this work — leave a note for Cqrs.
