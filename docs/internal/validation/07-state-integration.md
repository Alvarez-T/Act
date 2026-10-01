# 07 — YFex.State integration (view models)

> Load for: **Phase 2** (sections 1–8, 10) and **Phase 4/5** (section 9 — Decisions in VMs).
> Touches: `YFex.State` (bag, attributes, timing), `YFex.State.Generator` (hooks), `YFex.State.Mvvm`
> (INDEI), and emission in `YFex.Validation.Generator` (03 §7).
> YFex.State references YFex.Validation (D2). Timing/debounce stays in YFex.State (D16).

---

## 1. What changes in YFex.State

| Area | Change |
|---|---|
| `YFex.State/Validation/*` | `IValidator`, `IAsyncValidator`, `ValidationResult`, `ValidationSeverity` → `[Obsolete]`, forwarding to YFex.Validation (01 §3) |
| `ValidationBag` | reworked: per-path **list** of `Violation` with origin, status, touched flag, async version (§2) |
| `Attributes.cs` | `[ValidateWith(Type)]`, `[ValidateAsync(Type)]` → `[Obsolete]`; **new**: `[ValidationOptions]`, `[ValidateOn]` (§6); `[Debounce]` gains `Property | Field` targets (§6.3) |
| `YFex.State.Generator` | emits `partial void __OnValidate_<Prop>(T value)` hooks + `__OnBatchValidated()`; old `Validate_X` emission deleted (03 §7) |
| `MvvmStateObject` | INDEI returns `ErrorItem` objects honouring the display policy (§8) |
| new | `ApplyServer(...)` overloads (§7), `IValidationSource` implemented via generated code (§5) |

## 2. `ValidationBag` (new shape)

```csharp
namespace YFex.State.Validation;

/// Per-StateObject store of violations. Lazy (created on first use; zero cost for VMs without
/// validation — unchanged from today). Keyed by VM property path ("Name", "Lines[2].Quantity");
/// paths are compile-time constant strings emitted by the generator, so lookups never allocate keys.
public sealed class ValidationBag
{
    public const ushort FieldOwner = 0;          // sync field rules (own + borrowed)
    public const ushort AsyncOwner = 1;          // async field rules
    public const ushort SubmitOwnerBase = 0x8000; // submit-time contract results (ValidateAll), + contract index

    /// Any ERROR-severity violation exists (visible or not). Use for Gates/CanSave.
    public bool HasErrors { get; }
    public int ErrorCount { get; }
    public ShowErrors Show { get; set; }         // display policy (set by generated ConfigureValidation)
    public ProfileId Profile { get; set; }       // active profile for per-keystroke validation (§4.3)

    /// Replace all violations for `path` produced by (source, ownerId) with the writer's content. Other origins on
    /// the same path are kept. This is what makes "re-run the invariant" clear only the invariant's old errors.
    /// No allocation and no event when nothing changed.
    public void Replace(string path, ViolationSource source, ushort ownerId, ref ViolationWriter w);
    public void Replace(string path, ViolationSource source, ushort ownerId, ReadOnlySpan<Violation> violations);
    /// Same, for violations spread over many paths (invariants/rules/server results). Paths must already be VM
    /// paths — map with ValidationResult.MapPaths(ref map) first (no IPathMap parameter: it would box struct maps).
    public void ReplaceAll(ViolationSource source, ushort ownerId, ReadOnlySpan<Violation> violations);
    public void ReplaceAll(ViolationSource source, ushort ownerId, ref ViolationWriter w);
    public void ReplaceAll(ViolationSource source, ushort ownerId, in ValidationResult result);
    /// ValidateAll: maps contract paths, drops field violations the per-field rules already reported.
    public void ReplaceSubmitResult<TMap>(ushort ownerId, ReadOnlySpan<Violation> contractViolations, ref TMap map)
        where TMap : struct, IPathMap, allows ref struct;
    public ServerOutcome ApplyServer(in ValidationResult result);   // Source = Server, identity paths
    public void Clear(string path, ViolationSource? source = null);
    public void ClearStale(string path);                     // Server + submit results; every setter hook calls it
    public void ClearAll();

    public bool HasErrorsFor(string path);                   // any Error on path (any origin)
    public bool HasFieldErrorsFor(string path);              // Error from FIELD rules — gates invariants
    public ViolationList Get(string path);                   // all, regardless of visibility (+ ReadOnlySpan<char> overloads)
    public ViolationList GetVisible(string path);            // after ShowErrors policy (§6.2)
    public IReadOnlyList<ErrorItem> GetVisibleItems(string path);   // stable ErrorItem objects (INDEI)
    public List<ErrorItem> GetAllVisibleItems();             // form level first
    public bool HasVisibleErrors { get; }
    public FieldStatus GetStatus(string path);
    public FieldState GetState(string path);
    public ViolationList FormLevel { get; }                  // violations with empty path
    public ValidationResult ToResult();                      // snapshot of everything

    // Interaction state used by the display policy
    public void Touch(string path);                          // control lost focus / committed
    public bool IsTouched(string path);
    public void MarkSubmitAttempted();                       // Save clicked → AfterSubmit errors become visible
    public bool SubmitAttempted { get; }
    public void ResetInteraction();                          // forget touched/submitted (new record)

    // Async bookkeeping (replaces the per-property fields from the Phase 0 fix)
    public AsyncValidationTicket BeginAsync(string path);    // cancels previous ticket for path; status → Validating
    public bool CompleteAsync(in AsyncValidationTicket t, ReadOnlySpan<Violation> r); // false (ignored) if stale
    public bool IsCurrent(in AsyncValidationTicket t);
    public void CancelAsync(string path);                    // sync rules failed → invalidate the in-flight check

    /// Raised with the path ("" = form level / everything). MvvmStateObject maps it to ErrorsChanged.
    public event Action<string>? ValidationChanged;
}

/// Reference-type wrapper created ONCE when a violation is stored (not per GetErrors call), so INDEI
/// consumers (Avalonia DataValidationErrors) get stable objects without repeated boxing.
/// ToString() returns the formatted message (catalog, current culture) — templates can also bind Severity.
public sealed class ErrorItem
{
    public Violation Violation { get; }
    public Severity Severity => Violation.Severity;
    public string Message { get; }                           // resolved lazily once via IMessageCatalog (10)
    public override string ToString() => Message;
}

/// Rich per-field state for code that wants to switch on it (the neutral FieldStatus enum lives in YFex.Validation).
[Union] public readonly struct FieldState : IUnion
// Pristine | Validating | Valid | Invalid(ViolationList) | Warned(ViolationList)
```

## 3. Rules declared directly on a view model

Where rules belong (respects the Entity / DTO / UI model / Domain separation):

| Case | Where the rules go |
|---|---|
| Form that sends a command/query to the server | on the **message** (baseline fluent config or attributes); the VM borrows with `[ValidatesAs]` (§4) |
| UI-only form (local settings, filters) or UI-only fields (ConfirmEmail) | directly on the **VM** |
| DTO shared between layers | attributes on the **DTO** |
| Domain model state changes | domain invariants returning `Result` (layer 3) — not this library |

Use VM-declared rules when the form is UI-only (local settings, filters) or has extra UI-only fields.

```csharp
public partial class PrinterSettingsVm : MvvmStateObject
{
    [Observable, Required, Length(1, 40)] public partial string PrinterName { get; set; }
    [Observable, Range(1, 10)]            public partial int Copies { get; set; }
    [Observable]                          public partial bool Duplex { get; set; }
    [Observable]                          public partial int PagesPerSheet { get; set; }

    [Invariant]                           // instance invariant on a VM: reads this.Duplex, this.PagesPerSheet (inferred)
    void DuplexNeedsSinglePage(ref ViolationWriter w)
    {
        if (Duplex && PagesPerSheet > 1)
            w.Add(Fields.PagesPerSheet, "printer.duplex-single-page");
    }
}
```

Generated by **YFex.Validation.Generator** (implements the hooks declared by the State generator). The block is a
**sketch** of the shape — the implemented emission is `YFex.Validation.Generator/Emitter.Vm.cs` (hooks take `in T`,
writers use `ViolationWriter.Begin(out var w)`, invariants are gated by `HasFieldErrorsFor`; see 13, Phase 2 log):

```csharp
// (sketch)
partial class PrinterSettingsVm : global::YFex.Validation.IValidationSource
{
    // FieldIds / Fields / Rules.<Prop> exactly as for any validatable type (03 §2)

    partial void __OnValidate_Copies(int value)                       // setter hook
    {
        if (!__ShouldValidateNow("Copies")) return;                   // ValidateOn policy (§6.1)
        var w = new global::YFex.Validation.ViolationWriter();
        Rules.Copies.Check(value, ref w, in Fields.Copies);
        Validation.Replace("Copies", global::YFex.Validation.ViolationSource.Field, 0, ref w);
        w.Dispose();
        // Copies is not read by any invariant → nothing else runs
    }

    partial void __OnValidate_Duplex(bool value)
    {
        // Duplex has no own rules; it is read by DuplexNeedsSinglePage → re-run that invariant
        __RunInvariantsReading(FieldIds.Duplex);
    }

    private void __RunInvariantsReading(ushort field)
    {
        if (IsInUpdate) { __pendingInvariants |= __InvariantMaskFor(field); return; }   // BeginUpdate(): defer to __OnBatchValidated
        if ((__InvariantMaskFor(field) & (1ul << InvariantIds.DuplexNeedsSinglePage)) != 0
            && !Validation.HasErrorsFor("Duplex") && !Validation.HasErrorsFor("PagesPerSheet"))
        {
            var w = new global::YFex.Validation.ViolationWriter();
            w.CurrentOrigin = new(global::YFex.Validation.ViolationSource.Invariant, InvariantIds.DuplexNeedsSinglePage);
            DuplexNeedsSinglePage(ref w);
            Validation.ReplaceAll(global::YFex.Validation.ViolationSource.Invariant, InvariantIds.DuplexNeedsSinglePage, w.ToResult());
            w.Dispose();
        }
    }

    partial void __OnBatchValidated() { /* run __pendingInvariants once, clear mask */ }

    /// Full validation (Save): every member + every invariant; marks submit attempted.
    public global::YFex.Validation.ValidationResult ValidateAll(string? profile = null) { … }
}
```

Result: turning **Duplex** on shows the error on **PagesPerSheet**; it disappears when either changes.

## 4. `[ValidatesAs<T>]` — borrowing the contract's rules (D15)

**Meaning:** "this VM is an editing surface for `T` (a command, query or DTO); validate my properties
with T's rules." Rules are declared **once**, on the contract shared by client and server (as
attributes or fluent config — 03 §4, D14). It is **optional**.

### 4.1 Name convention, `[MapsTo]`, `Prefix`

```csharp
// contract (fluent in baseline config, or attributes)
//   CreateCustomer: Name Required+Length(2,80), Email Required+Email, Document Required+CpfOrCnpj, Address.City Required

[ValidatesAs<Customer.Commands.CreateCustomer>]
public partial class CustomerFormVm : MvvmStateObject
{
    [Observable] public partial string Name { get; set; }                         // same name → borrows Name rules
    [Observable] public partial string Email { get; set; }
    [Observable, MapsTo("Document")] public partial string CpfCnpj { get; set; }   // different name → explicit
    [Observable, MapsTo("Address.City")] public partial string City { get; set; }  // flattened nested member

    // extra UI-only rules merge with the borrowed ones
    [Observable, Required] public partial string ConfirmEmail { get; set; }
    [Observable, Length(max: 60)] public partial string Nickname { get; set; }

    [Invariant] void EmailsMatch(ref ViolationWriter w)
    { if (ConfirmEmail != Email) w.Add(Fields.ConfirmEmail, "customer.email-mismatch"); }
}
```

`[MapsTo]` works in **both directions**: (1) rules flow contract → VM (CpfCnpj gets Document's
rules); (2) server violations flow back (server path `"Document"` → shown on `CpfCnpj`).

### 4.2 Multiple contracts

```csharp
[ValidatesAs<CreateCustomer>]
[ValidatesAs<CreateAddress>(Prefix = "Address")]   // CreateAddress.City ↔ vm.AddressCity, server path "City" (from CreateAddress) ↔ AddressCity
public partial class CustomerWithAddressVm : MvvmStateObject { … }
```

If one VM property matches two contracts without `Prefix`/`[MapsTo]` → error YFV0024 (ambiguous).

### 4.3 Profiles (strictness levels)

```csharp
[ValidatesAs<Sale.Commands.SaveSaleDraft>(Profile = "draft")]   // draft: most fields optional
[ValidatesAs<Sale.Commands.CloseSale>(Profile = "final")]       // final: everything required
public partial class SaleVm : MvvmStateObject { … }

// per-keystroke validation uses the active profile (default = first declared, change with Validation.Profile = "final")
// Validation: ValidateAll("draft") before autosave; ValidateAll("final") before closing
```

### 4.4 What is generated for `[ValidatesAs]`

Sketch — implemented in `Emitter.Vm.cs` (`__RunField_X` runs borrowed `T.Rules.X.Check(value, ref w, in path, profile)`
then own rules; `IValidationSource` is implemented explicitly; `ValidateAll` lives on the root VM):

```csharp
// (sketch)
partial class CustomerFormVm : global::YFex.Validation.IValidationSource
{
    partial void __OnValidate_Name(string value)
    {
        var w = new global::YFex.Validation.ViolationWriter();
        global::Customer.Commands.CreateCustomer.Rules.Name.Check(value, ref w, in Fields.Name);  // borrowed (by convention, always exists)
        Validation.Replace("Name", global::YFex.Validation.ViolationSource.Field, 0, ref w);
        w.Dispose();
    }
    partial void __OnValidate_CpfCnpj(string value)
    {
        var w = new global::YFex.Validation.ViolationWriter();
        global::Customer.Commands.CreateCustomer.Rules.Document.Check(value, ref w, in Fields.CpfCnpj); // via MapsTo, VM path
        Validation.Replace("CpfCnpj", global::YFex.Validation.ViolationSource.Field, 0, ref w);
        w.Dispose();
    }
    partial void __OnValidate_Nickname(string value) { /* own rule Length(max:60) */ }

    // constraints: borrowed merged with own (most restrictive)
    public bool TryGetConstraints(global::System.ReadOnlySpan<char> path, out global::YFex.Validation.FieldConstraints c)
    {
        switch (path)
        {
            case "Name":    c = global::Customer.Commands.CreateCustomer.Rules.Name.Constraints; return true;
            case "CpfCnpj": c = global::Customer.Commands.CreateCustomer.Rules.Document.Constraints; return true;
            case "City":    c = global::Customer.Commands.AddressDto.Rules.City.Constraints; return true;
            case "Nickname": c = Rules.Nickname.Constraints; return true;
        }
        return __TryGetDecisionConstraints(path, out c);   // §9.3 (dynamic), false when no [ConstraintsFrom]
    }

    /// Contract path ↔ VM path (both directions). Used by ApplyServer and ValidateAll.
    internal readonly struct __CreateCustomerPathMap : global::YFex.Validation.IPathMap
    {
        public bool TryMap(in global::YFex.FieldPath from, out global::YFex.FieldPath to)
            => from.Template switch
            {
                "Name" => Set(Fields.Name, out to),
                "Document" => Set(Fields.CpfCnpj, out to),
                "Address.City" => Set(Fields.City, out to),
                _ => Unmapped(out to),                     // unmapped paths go to the form-level summary
            };
    }

    /// Builds the contract from the VM. Emitted only when EVERY required contract member maps;
    /// otherwise the generator emits `public partial CreateCustomer ToCreateCustomer();` and you
    /// implement it (info YFV0020 lists the unmapped members).
    public global::Customer.Commands.CreateCustomer ToCreateCustomer()
        => new() { Name = Name, Email = Email, Document = CpfCnpj, Address = new() { City = City } };

    /// Submit validation: contract's Validate (members + contract invariants) remapped to VM paths
    /// + VM's own rules/invariants. Marks submit attempted.
    public global::YFex.Validation.ValidationResult ValidateAll(string? profile = null) { … }
}
```

### 4.5 Queries

Same mechanism for filters/search VMs:

```csharp
[ValidatesAs<Reports.Queries.SalesReport>]
public partial class SalesReportFilterVm : MvvmStateObject
{
    [Observable] public partial DateOnly From { get; set; }
    [Observable] public partial DateOnly To { get; set; }       // SalesReport invariant "max 1 year" shows here
    [Observable] public partial int PageSize { get; set; }      // Range(1,200, Coerce=true): the VM clamps on commit instead of erroring
}
```

### 4.6 Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| YFV0020 | Info | Contract members not mapped by any VM property (listed); `ToX()` becomes a partial you implement |
| YFV0021 | Error | `[MapsTo("X")]` target does not exist on the contract |
| YFV0022 | Error | VM property type incompatible with the contract member type |
| YFV0023 | Warning | VM rule is **looser** than the contract (e.g. VM `Length(max:100)`, contract 80) — the server would reject |
| YFV0024 | Error | Property matched by more than one `[ValidatesAs]` without `Prefix`/`[MapsTo]` |

## 5. `IValidationSource` implementation

Generated on every VM with validation (02 §7.1): `TryGetConstraints` (§4.4), `GetVisibleViolations`
→ `Validation.GetVisible`, `GetStatus`, `Touch` → `Validation.Touch` + (ValidateOn.Commit) validate
that field now, events forwarded from the bag and from Decision changes (§9.3).

## 6. When to validate and when to show (D16)

### 6.1 Validate timing

```csharp
namespace YFex.State;

/// Class-level defaults. Trigger = when rules RUN; Show = when violations become VISIBLE.
[AttributeUsage(AttributeTargets.Class)]
public sealed class ValidationOptionsAttribute : Attribute
{
    public ValidateOn Trigger { get; init; } = ValidateOn.Change;
    public ShowErrors Show { get; init; } = ShowErrors.AfterTouched;
}

/// Per-property override of the trigger.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ValidateOnAttribute(ValidateOn trigger) : Attribute;

public enum ValidateOn : byte
{
    Change,   // every setter call (with YValueInput CommitMode=Commit this is effectively on commit)
    Commit,   // only when the control reports Touch (blur/Enter) — the setter hook stores and defers
    Submit,   // only in ValidateAll
}

public enum ShowErrors : byte
{
    Immediately,     // as soon as validated
    AfterTouched,    // after the field lost focus once (default)
    AfterSubmit,     // only after Save was attempted
}
```

### 6.2 Display policy
`GetVisible(path)` returns violations when: `Immediately`; or `AfterTouched` and `IsTouched(path)`
or `SubmitAttempted`; or `AfterSubmit` and `SubmitAttempted`. **Server** and **Rule** origin violations
are always visible (the user needs to see them) — and so are **Manual** ones (app code added them on purpose).
`HasErrors` ignores visibility (Gates must be correct).

### 6.3 Debounce (YFex.State, D16)

`[Debounce(ms)]` already exists for methods. Extend `AttributeTargets` to `Property | Field`:
on an `[Observable]` property it **debounces the async rules of that property** (sync rules still run
immediately). Reuse the implementation in `YFex.State/Timing` (make a public `Debouncer` primitive if
it's internal, so other features can use it too).

```csharp
[Observable, ValidateWith<EmailNotTaken>, Debounce(400)]
public partial string Email { get; set; }
```

Timeline (Commit + AfterTouched): type "joh" → nothing · Tab → format ❌ shown · fix + Tab → format ✅,
async check starts after 400 ms (status Validating → spinner) · edit again → previous check cancelled
(ticket superseded) · answer → "already registered" ❌.

### 6.4 Batching
`using (BeginUpdate()) { A = …; B = …; }` (existing `BatchScope`): field rules run per setter, but
invariants and decision recomputation are coalesced into **one** run at scope end
(`__OnBatchValidated`, and `[ReactsTo]` already coalesces inside updates).

## 7. `ApplyServer` — server results back onto fields

```csharp
public enum ServerOutcome : byte { Ok, Queued, Invalid, Failed }

/// Extension methods on StateObject (YFex.State references YFex.Cqrs? NO — see note).
/// 1. Error with ErrorType.ValidationProblem → take Error.Violations (09 §5)
/// 2. map each contract path → VM path with the generated IPathMap of the [ValidatesAs] contract that
///    matches the command type (TCommand); unmapped/empty paths → form-level summary
/// 3. store with Source = Server (always visible)
/// 4. a Server violation on a path is cleared automatically the next time that property changes
///    (the server's verdict is stale after an edit)
/// 5. Queued → nothing to show (returns Queued so the VM can toast "saved offline")
ServerOutcome ApplyServer<TCommand, TResult>(this StateObject vm, in QueueableResult<TResult> result);
ServerOutcome ApplyServer<TCommand>(this StateObject vm, in QueueableResult result);
ServerOutcome ApplyServer<TQuery, TResult>(this StateObject vm, in CacheableQueryResult<TResult> result);
```

> **Implemented in Phase 2:** generated `ApplyServer<TContract>(in ValidationResult)` (maps paths, returns `Ok`/`Invalid`)
> and `ValidationBag.ApplyServer(in ValidationResult)`. The Cqrs-result overloads below are Phase 3.
>
> **Layering note:** YFex.State must not reference YFex.Cqrs. Put these extensions in
> **YFex.Mvvm** (Layer 3, already bridges State + NavigatR) or a small generated partial on the VM
> (preferred: generated `ApplyServer(in QueueableResult<T>)` members on VMs that have `[ValidatesAs]`
> for a command, emitted only when the compilation references YFex.Cqrs).

Usage (your dispatcher, `IDispatcher.CommandAsync`):

```csharp
[StateCommand, Gate(nameof(CanSave))]
async Task SaveAsync(CancellationToken ct)
{
    if (!ValidateAll().IsValid) return;                                      // client check first
    var result = await _dispatcher.CommandAsync<Customer.Commands.CreateCustomer, CustomerId>(ToCreateCustomer(), ct);
    switch (ApplyServer(in result))
    {
        case ServerOutcome.Ok:     _navigator.NavigateTo(new CustomerRoute(result.OkValue)); break;
        case ServerOutcome.Queued: _toast.Info("customer.saved-offline"); break;
        // Invalid → errors already on the fields; Failed → form-level summary shows it
    }
}
```

## 8. `MvvmStateObject` INDEI changes

- `GetErrors(path)` → `ErrorItem`s from `Validation.GetVisible(path)`; null/empty path → form-level + all visible.
- `HasErrors` (INDEI) → any **visible** Error (so controls turn red only when policy allows).
- `ErrorsChanged` raised from `ValidationChanged`, and when visibility changes (Touch, submit attempt).
- Warnings/Info are returned too; the Avalonia template colors by `ErrorItem.Severity` (08 §5).

## 9. Decisions (YFex.Rules) in view models

### 9.1 Gather async, decide sync

```csharp
public partial class SaleVm : MvvmStateObject
{
    [Observable] public partial CustomerId? CustomerId { get; set; }
    [Observable] public partial CustomerTier Tier { get; set; }                 // async fact
    [Observable] public partial PaymentMethod Payment { get; set; }
    [Observable] public partial int Installments { get; set; }
    [Observable] public partial Percentual ManualDiscount { get; set; }

    /// Latest decision. [ConstraintsFrom] = merge its limits/options into IValidationSource for VM
    /// properties with the same names as situation fields (or [MapsTo]), and put its violations
    /// (Requested checks, Deny/Warn) into the bag with Source = Rule.
    [Observable, ConstraintsFrom] public partial Decision<SaleSituation> Policy { get; set; }

    // GATHER (async): only when the customer changes; cancel previous; debounce typing in a search box
    [ReactsTo(nameof(CustomerId), CancelPrevious = true), Debounce(300)]
    async Task LoadCustomerFactsAsync(CancellationToken ct) { … Tier = …; }

    // DECIDE (sync, microseconds): on every relevant change, no debounce
    [ReactsTo(nameof(Tier), nameof(Payment), nameof(Installments), nameof(ManualDiscount), nameof(Lines))]
    void RecomputePolicy() => Policy = SaleRules.Evaluate(BuildSituation(), new() { Trace = TraceLevel.Full });
}
```

### 9.2 `BuildSituation()`

Written by hand:

```csharp
SaleSituation BuildSituation() => new(
    _tenant, _store, Channel.Pos, _businessDate,          // context captured once when the sale opened
    Tier, IsBirthdayMonth, _actor,
    Payment, Installments, ManualDiscount,
    Lines.ToSituations());                                 // StateList<SaleLineVm> → ImmutableArray<SaleLineSituation>
```

or generated (⚑P8):

```csharp
[SituationSource<SaleSituation>]        // generator emits `SaleSituation BuildSituation()` mapping same-named
public partial class SaleVm …           // properties/fields; unmatched situation fields must be provided by
                                        // `partial TenantId __Situation_Tenant();` style partial methods (error lists them)
```

### 9.3 Decision constraints & violations → UI

When `Policy` changes (fingerprint differs):
1. For each situation field mapped to a VM property: raise `ConstraintsChanged(path)` so controls
   re-read `TryGetConstraints` (which merges `Policy.ConstraintsFor(field)`).
2. `Validation.ReplaceAll(Source = Rule, owner 0, Policy.Violations mapped to VM paths)`.
3. Approvals → the VM exposes `Policy.Verdict.IsNeedsApproval` and the approval UI (08 §8).

`CanClose` example: `Policy.Verdict.IsAllowed && !Validation.HasErrors`.

## 10. Tests (Phase 2)

- Every Phase 0 test still passes through the new path.
- VM own rules: setter → violation; `ShowErrors` policies; `Touch`; submit.
- Invariant re-run clears only its own violations; batching runs invariants once.
- `[ValidatesAs]`: borrowed rules, `[MapsTo]` both directions, `Prefix`, `Profile`, diagnostics YFV0020–24.
- Async: debounce, cancellation, stale ticket ignored, exception routed.
- `ApplyServer`: mapped path, unmapped → form-level, cleared on edit, Queued.
- INDEI: visible-only, `ErrorItem` identity stable between `GetErrors` calls (no re-boxing).
- Allocation: setting a valid value on a validated property allocates 0 bytes.
