# 02 — YFex.Validation (runtime core)

> Load for: **Phase 1**. Also referenced by every other phase.
> Project: `YFex/src/Validation/YFex.Validation` — references **YFex core** (`YFex/src/YFex`) only;
> `net11.0`, `LangVersion preview`, `IsAotCompatible`, `AllowUnsafeBlocks`, generator bundled as
> analyzer (copy `YFex.State.csproj`). Root namespace: `YFex.Validation`.
> Sections marked **[YFex core]** describe base types that Phase 1 adds to **YFex core**
> (`YFex/src/YFex`, namespace `YFex` unless stated), not to YFex.Validation (README D20).

Design goals of this project:
- **Success path allocates nothing.** Violations are only materialized when something fails.
- **No strings built at validation time.** A violation is a code + path + args; text comes from the
  message catalog (10) at the edge.
- **One vocabulary** for shape validation, rule enforcement (YFex.Rules), server responses, fiscal.

---

## 1. Scalars

```csharp
namespace YFex.Validation;

/// How serious a violation is. Only Error blocks (IsValid = false). "Needs approval" is NOT a
/// severity — it is a YFex.Rules directive (⚑P4).
public enum Severity : byte { Info = 0, Warning = 1, Error = 2 }

/// Stable, namespaced identifier of a check or rule. Persisted (audit), sent over the wire,
/// and used as the message-catalog key.
/// Conventions: built-ins are short ("required", "len", "range", "email", "pattern");
/// module codes are prefixed ("br.document", "sale.discount.cap", "nfe.539", "stock.out").
/// Always create from string literals / static readonly fields so no allocation happens per use.
public readonly record struct RuleCode(string Value)
{
    public static implicit operator RuleCode(string value) => new(value);
    public override string ToString() => Value;
}

/// Compile-time field identifier inside ONE type, emitted by the generator as consts
/// (e.g. CreateCustomer.FieldIds.Name = 0). Used as dictionary-free keys in ValidationBag.
public readonly record struct FieldId(ushort Value);
```

### 1.1 `ValueKind` / `RawValue` — **[YFex core]** (shared with Composer, Capabilities, Persistence…)

File: `YFex/src/YFex/Values/RawValue.cs` (+ `ValueKind.cs`, `Payload16.cs`). Namespace `YFex`.

```csharp
namespace YFex;

/// Kind of scalar stored in a RawValue.
/// ⚠ Superseded by D35 (13 "Domain-agnostic refactor"): ValueKind now holds storage primitives only
/// (Money…Length and Custom removed); semantic types implement IRawValue<TSelf> and travel as a typed
/// RawValue carrying their RawType id. The listing below is the original Phase 1 design.
public enum ValueKind : byte
{
    None, Bool, Int32, Int64, Decimal, Double, Text, Date, Time, DateTime, Guid,
    Money, Percentual, Quantity, Mass, Length, EnumCode, Custom = 128
}

/// Non-boxing tagged scalar (≈32 bytes). Used for: rule arguments (RuleArgs), numeric/date bounds
/// in FieldConstraints, Situation field reads by id (YFex.Rules schema), decision-table cells.
/// Payload layout: 16-byte [InlineArray(2)] ulong written with Unsafe.As for long/double/decimal/
/// DateTime/Guid; strings go to the reference slot.
/// Example: RawValue.From(80) ; RawValue.From(Money.Of(1000)) ; RawValue.FromEnum(PaymentMethod.Pix)
public readonly struct RawValue : IEquatable<RawValue>, IComparable<RawValue>
{
    private readonly Payload16 _raw;      // [InlineArray(2)] ulong
    private readonly string? _text;       // only for ValueKind.Text
    public ValueKind Kind { get; }

    public static RawValue From(int v);   public static RawValue From(long v);
    public static RawValue From(decimal v); public static RawValue From(double v);
    public static RawValue From(DateOnly v); public static RawValue From(string v);
    // Ecosystem value types live in the same assembly → direct factories/readers, no extensions needed
    public static RawValue From(Money v);     public static RawValue From(Percentual v);
    public static RawValue From(Quantity v);  public static RawValue From(Mass v); public static RawValue From(Length v);
    /// Stores the underlying value; formatting/wire use the enum's EnumCode when it has one
    /// (YFex.Extensions.EnumExtensions.GetCode) → stable codes across renames.
    public static RawValue FromEnum<TEnum>(TEnum v) where TEnum : unmanaged, Enum;

    public bool TryGet(out long v);  public bool TryGet(out decimal v);  // typed reads, no boxing
    public bool TryGet(out double v); public bool TryGet(out DateOnly v); public bool TryGet(out string? v);
    public bool TryGet(out Money v); public bool TryGet(out Percentual v); public bool TryGet(out Quantity v);
    public bool TryGetEnum<TEnum>(out TEnum v) where TEnum : unmanaged, Enum;

    /// Formats for messages ("80", "R$ 1.000,00", "10%") using the kind + culture. Span-based.
    public bool TryFormat(Span<char> dest, out int written, IFormatProvider? provider);

    /// Compares two values of the same (or numerically compatible) kind. Different incompatible
    /// kinds throw — the generator guarantees compatible kinds at compile time.
    public int CompareTo(RawValue other);
}
```

> Composer uses `RawValue` directly for slots, expression values, constraints, the extension bag and
> AI tool I/O; its field type is `RawType` + `FieldConstraints` + UI-only display hints
> (composer-design rev 2, §6). The `IFieldValue<T>`/`Value` union of composer-design rev 1 were
> dropped in favour of `IRawValue<TSelf>` (D35) and `RawValue`.

---

## 2. Where: `FieldPath` — **[YFex core]**

File: `YFex/src/YFex/Paths/FieldPath.cs`. Namespace `YFex`. Reusable for any "member path with
indices" (violations, Composer bindings, Xml/fiscal element paths, UI binding paths).

```csharp
/// Location of a violation. Template is a compile-time constant emitted by the generator
/// ("Name", "Address.City", "Lines[].Quantity"); Indices fills the "[]" placeholders in order.
/// No string concatenation happens until something renders it (ToString / TryFormat).
///   FieldPath.Of("Name")                        → Name
///   FieldPath.Of("Lines[].Quantity", 3)          → Lines[3].Quantity
///   FieldPath.Of("Orders[].Lines[].Qty", 1, 4)   → Orders[1].Lines[4].Qty
/// Empty (default) = object-level violation (shown in a form-level summary).
public readonly struct FieldPath : IEquatable<FieldPath>
{
    public string? Template { get; }       // null = object level
    public IndexPath Indices { get; }      // up to 4 indices
    public static FieldPath Of(string template);
    public static FieldPath Of(string template, int i0);
    public static FieldPath Of(string template, int i0, int i1);
    public FieldPath WithIndex(int index);                  // fills the next "[]" (used when validating Each)
    public FieldPath Prefixed(string parentTemplate, int? index = null); // nested validatable: "Address" + "City" → "Address.City"
    public bool TryFormat(Span<char> dest, out int written);
    public override string ToString();                       // allocates; only for logs/wire
    public static bool TryParse(ReadOnlySpan<char> text, out FieldPath path); // wire → FieldPath (server responses)
}

/// [InlineArray(4)] int + byte Count. Deeper nesting than 4 collection levels → generator diagnostic YFV0014.
public struct IndexPath { … }                                // [YFex core] too
```

---

## 3. What: `Violation`, `RuleArgs`, origin

```csharp
/// Up to two typed arguments for message formatting ("{field} must be between {0} and {1}").
/// Example: Length(2,80) failure → RuleArgs(RawValue.From(2), RawValue.From(80)).
[InlineArray(2)] public struct RuleArgsBuffer { private RawValue _e0; }
public readonly struct RuleArgs
{
    public int Count { get; }
    public RawValue this[int index] { get; }
    public static RuleArgs None => default;
    public RuleArgs(RawValue a0); public RuleArgs(RawValue a0, RawValue a1);
}

/// Who produced a violation. The owner lets a producer REPLACE only its own previous violations
/// (e.g. re-running an invariant clears the invariant's old errors but not the field's own).
public enum ViolationSource : byte
{
    Field = 0,      // attribute/fluent rule on a single member
    Invariant = 1,  // [Invariant] method (cross-field)
    Rule = 2,       // YFex.Rules decision (limits/deny/warn)
    Context = 3,    // async server-side context rule (stock, uniqueness)
    Server = 4,     // came back from the server in a response (ApplyServer)
    Manual = 5,     // app code called Validation.Add(...)
}
public readonly record struct ViolationOrigin(ViolationSource Source, ushort OwnerId);
// OwnerId: generated id of the invariant / rule; 0 for Field/Server/Manual.

/// One failed check. ~120 bytes: always pass by `in`, store only in arrays (never boxed).
public readonly struct Violation
{
    public FieldPath Path { get; }
    public RuleCode Code { get; }
    public Severity Severity { get; }
    public RuleArgs Args { get; }
    public ViolationOrigin Origin { get; }
    /// Optional literal text that bypasses the catalog. Use only for runtime-authored rules whose
    /// message is stored with the rule version (10 §4) or for adapters (FluentValidation).
    public string? Detail { get; }

    public Violation(in FieldPath path, RuleCode code, Severity severity = Severity.Error,
                     in RuleArgs args = default, ViolationOrigin origin = default, string? detail = null);
}
```

---

## 4. Collecting: `ViolationWriter`, `ViolationCollector`, `ViolationList`

```csharp
/// Stack-only collector used by ALL generated Validate methods and by rules.
/// Holds 4 violations inline (no allocation); the 5th moves everything to an ArrayPool<Violation> array.
/// Must be disposed (returns the pooled array). C# forbids passing a `using` variable by `ref` (CS1657),
/// so writers are opened with Begin: `w` is an ordinary local, the scope disposes it via a ref field
/// (same cost as try/finally, no allocation):
///
///   using var scope = ViolationWriter.Begin(out var w);
///   CreateCustomer.Validate(in cmd, ref w);
///   if (w.HasErrors) return w.ToResult();   // ToResult allocates exactly once (array sized to Count)
///
/// State lives in an internal non-ref struct `ViolationStorage` embedded in the writer, because a ref
/// field cannot have a ref struct type (CS9050); ViolationScope holds `ref ViolationStorage`.
public ref struct ViolationWriter
{
    private ViolationStorage _storage;          // [InlineArray(4)] Violation + ArrayPool spill + counts
    public static ViolationScope Begin([UnscopedRef] out ViolationWriter writer);
    public int Count { get; }
    public int ErrorCount { get; }              // Severity.Error only — generated Validate compares before/after
    public bool HasErrors { get; }              // ErrorCount > 0
    public ViolationOrigin CurrentOrigin { get; set; } // stamped on each Add; generated code sets it per invariant/rule

    public void Add(in Violation v);
    public void Add(in FieldPath path, RuleCode code, Severity severity = Severity.Error);
    public void Add(in FieldPath path, RuleCode code, RawValue a0, Severity severity = Severity.Error);
    public void Add(in FieldPath path, RuleCode code, RawValue a0, RawValue a1, Severity severity = Severity.Error);

    /// True if any Error already exists for exactly this path. Used by generated code to skip
    /// invariants whose inputs already failed ("avoid noise").
    public readonly bool HasErrorFor(in FieldPath path);

    public readonly ReadOnlySpan<Violation> AsSpan();   // valid only until the next Add/Dispose
    public ValidationResult ToResult();                 // copies into an exact-size array; Valid when empty
    public void Dispose();
}

/// Heap collector for async code (a ref struct cannot cross await). Used by IAsyncValueRule and
/// IContextRule. Pooled: rent from ViolationCollector.Rent(), return with Dispose().
/// Not thread-safe: context rules run sequentially.
public sealed class ViolationCollector : IDisposable
{
    public static ViolationCollector Rent();
    public int Count { get; }
    public bool HasErrors { get; }
    public void Add(in Violation v);
    public void Add(in FieldPath path, RuleCode code, RawValue a0 = default, Severity severity = Severity.Error);
    public void AddRange(in ValidationResult other);
    public ValidationResult ToResult();
    public void Dispose();
}

/// Immutable list of violations. Wraps an exact-size array (or nothing). Struct enumerator; no LINQ.
public readonly struct ViolationList
{
    public int Count { get; }
    public ref readonly Violation this[int index] { get; }
    public ReadOnlySpan<Violation> AsSpan();
    public Enumerator GetEnumerator();                 // ref struct enumerator
    public ViolationList For(in FieldPath path);        // filtered copy (allocates only if a subset)
    public bool Any(Severity atLeast);
    public static ViolationList Empty => default;
}
```

---

## 5. Results

```csharp
/// THE validation result for the whole ecosystem (D4). Replaces YFex.State.Validation.ValidationResult
/// (single) and YFex.Cqrs.ValidationResult.
/// Valid ⇔ no Error-severity violations (warnings/infos may be present and are still shown).
public readonly struct ValidationResult
{
    public ViolationList Violations { get; }
    public bool IsValid { get; }
    public bool HasWarnings { get; }
    public static ValidationResult Valid => default;
    public static ValidationResult Of(in Violation v);             // single failure (allocates 1-elem array)
    public static ValidationResult Of(ViolationList list);
    public ValidationResult Merge(in ValidationResult other);       // concat (allocates only if both non-empty)
    /// Rewrites paths (e.g. command paths → VM paths, or prefixing "Address."). Used by ApplyServer/ValidatesAs.
    public ValidationResult MapPaths<TMap>(ref TMap map) where TMap : struct, IPathMap, allows ref struct;
}

public interface IPathMap { bool TryMap(in FieldPath from, out FieldPath to); } // generated per VM/command pair

/// Proof that a value passed shape validation (attributes/fluent/invariants).
/// Only YFex.Validation can create it (private ctor) — via Validator.Check. Handlers can require
/// Valid<T> so that unvalidated input cannot reach them at compile time.
public readonly struct Valid<T>
{
    public T Value { get; }
    internal Valid(T value);
}

/// Parse-don't-validate result.
[Union]
public readonly struct Validated<T> : IUnion
{
    // cases: Valid<T> | Invalid
    public bool TryGetValue(out Valid<T> valid);
    public bool TryGetValue(out Invalid invalid);
    public bool IsValid { get; }
    …
}
public readonly record struct Invalid(ValidationResult Result);

public static class Validator
{
    /// Runs T's generated Validate. Zero allocation on success.
    ///   var v = Validator.Check(cmd);
    ///   if (v.TryGetValue(out Invalid bad)) return bad.Result;
    ///   Handle(v.TryGetValue(out Valid<CreateCustomer> ok) ? ok : default);
    public static Validated<T> Check<T>(in T value) where T : IValidatable<T>;
    public static ValidationResult Validate<T>(in T value) where T : IValidatable<T>;
    public static bool IsValid<T>(in T value) where T : IValidatable<T>;
}
```

---

## 6. Contracts

```csharp
/// Implemented by the GENERATOR on every validatable type (never by hand).
public interface IValidatable<TSelf> where TSelf : IValidatable<TSelf>
{
    /// Runs all member rules then invariants; returns true when no Error was added.
    static abstract bool Validate(in TSelf value, ref ViolationWriter w);
    /// Runs the rules of ONE member (and nothing else). Used by UI per-field validation.
    static abstract bool ValidateMember(FieldId member, in TSelf value, ref ViolationWriter w);
    /// Static metadata for a member path ("Name", "Address.City"). Switch-based, no dictionary.
    static abstract bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints);
    static abstract bool TryGetFieldId(ReadOnlySpan<char> path, out FieldId id);
}

// ── Value rules: used by [ValidateWith<T>] (⚑P1) ────────────────────────────────────
// All static abstract → generated code calls them directly (inlinable, no instance, no DI).

/// Rule over a typed value.
///   public readonly struct EvenNumber : IValueRule<int>
///   { public static RuleCode Code => "even"; public static bool IsValid(in int v) => (v & 1) == 0; }
public interface IValueRule<T>
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(in T value);
}

/// Rule with ONE compile-time argument taken from the attribute: [ValidateWith<MultipleOf>(5)].
/// The generator emits MultipleOf.IsValid(value, 5) and passes RawValue.From(5) as message arg 0.
public interface IValueRule<T, TArg>
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(in T value, TArg arg);
}

/// Text rule over a span → works for string properties AND raw UI text before parsing.
///   public readonly struct CpfOrCnpj : ITextRule
///   {
///       public static RuleCode Code => "br.document";
///       public static bool IsValid(ReadOnlySpan<char> v) => Cpf.TryParse(v, out _) || Cnpj.TryParse(v, out _);
///   }
public interface ITextRule
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(ReadOnlySpan<char> value);
}
public interface ITextRule<TArg> { static abstract RuleCode Code { get; } static abstract bool IsValid(ReadOnlySpan<char> value, TArg arg); }

/// Async value rule (UI: debounced by YFex.State; server: runs in the context stage).
/// Must be pure w.r.t. the value; I/O allowed. Honour ct.
///   public readonly struct EmailNotTaken : IAsyncValueRule<string> { … static ValueTask<bool> IsValidAsync(string v, IServiceProvider sp, CancellationToken ct) … }
public interface IAsyncValueRule<T>
{
    static abstract RuleCode Code { get; }
    static abstract ValueTask<bool> IsValidAsync(T value, IServiceProvider services, CancellationToken ct);
}

/// Whole-object async server rule (layer 1 "context"): uniqueness, FK exists, stock…
/// Instance interface (DI-constructed by Cqrs through generated factories — no Activator).
///   public sealed class StockAvailable(IStockQueries stock) : IContextRule<CloseSale>
///   {
///       public async ValueTask CheckAsync(CloseSale c, ViolationCollector v, CancellationToken ct)
///       { foreach (var m in await stock.MissingFor(c.SaleId, ct))
///             v.Add(FieldPath.Of("Lines[].Quantity", m.Index), "stock.out", RawValue.From(m.Available)); }
///   }
public interface IContextRule<in T>
{
    ValueTask CheckAsync(T value, ViolationCollector violations, CancellationToken ct);
}
```

---

## 7. Constraints (what the UI projects onto controls)

```csharp
/// (YFex.Validation — uses RawValue/OptionSet from YFex core.) Machine-readable limits of ONE field. Static constraints come from attributes/fluent
/// (generated consts); dynamic constraints come from a YFex.Rules Decision. Merge = most restrictive.
/// Example: [Required, Length(2, 80)] string Name → Required=true, MinLength=2, MaxLength=80.
///          Decision "Installments ≤ 6" → Max = RawValue.From(6), Options = {1..6}.
public readonly struct FieldConstraints
{
    public ConstraintFlags Flags { get; }       // which members below are set
    public bool Required { get; }
    public int MinLength { get; }               // valid when Flags.HasMinLength
    public int MaxLength { get; }
    public RawValue Min { get; }                // numeric/date/money bound (inclusive)
    public RawValue Max { get; }
    public RawValue Step { get; }               // optional, for steppers
    public OptionSet Options { get; }           // allowed discrete values (Grant directive / enum subset)
    public RuleCode Format { get; }             // mask/format hint: "br.document", "email", "phone"
    public Severity MaxSeverity { get; }        // strongest severity among the field's rules (UI can show "required" marker only for Error)

    public FieldConstraints Merge(in FieldConstraints other);   // most restrictive wins (max of mins, min of maxes, intersection of options)
    public static FieldConstraints None => default;
    /// Factory used by generated code; only the named arguments passed are flagged as set.
    public static FieldConstraints Create(bool required = false, int? minLength = null, int? maxLength = null,
        RawValue min = default, RawValue max = default, RawValue step = default,
        OptionSet options = default, RuleCode format = default, Severity maxSeverity = Severity.Error);
}

[Flags] public enum ConstraintFlags : ushort { None = 0, Required = 1, MinLength = 2, MaxLength = 4, Min = 8, Max = 16, Step = 32, Options = 64, Format = 128 }

/// [YFex core] — file YFex/src/YFex/Values/OptionSet.cs, namespace YFex (also used by Composer enum
/// facets, filters). Set of allowed discrete values. ≤ 64 small non-negative integers (installment
/// counts, enum values) stored as a ulong bitmask; larger sets use an exact-size RawValue[].
///   OptionSet.Range(1, 6)  → bits 1..6
public readonly struct OptionSet
{
    public bool IsEmpty { get; }                // empty = "no restriction", NOT "nothing allowed"
    public bool Contains(long value);
    public OptionSet Intersect(in OptionSet other);
    public Enumerator GetEnumerator();          // struct enumerator of RawValue
    public static OptionSet Range(int fromInclusive, int toInclusive);
    public static OptionSet Of(ReadOnlySpan<long> values);
}
```

### 7.1 `IValidationSource` — the UI-neutral channel (implemented by generated VM code, 07)

```csharp
/// What a UI control needs from its DataContext. Framework-neutral (Avalonia/Uno/Blazor adapters
/// consume it). Paths are the VM's own property paths ("Name", "Lines[2].Quantity").
public interface IValidationSource
{
    /// Static (attributes/fluent/borrowed) merged with dynamic (current Decision) constraints.
    bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints);
    /// Violations currently VISIBLE for path (display policy applied: touched/submitted, see 07 §6).
    ViolationList GetVisibleViolations(ReadOnlySpan<char> path);
    /// Field state for spinners/icons: Pristine | Validating | Valid | Invalid | Warned.
    FieldStatus GetStatus(ReadOnlySpan<char> path);
    /// Controls call this on blur/commit so "show after touched" works.
    void Touch(ReadOnlySpan<char> path);
    /// Raised with the VM path when violations/status change ("" = everything / form-level).
    event Action<string>? ViolationsChanged;
    /// Raised when constraints change (a new Decision arrived) so controls re-read Min/Max/Options.
    event Action<string>? ConstraintsChanged;
}

public enum FieldStatus : byte { Pristine, Validating, Valid, Invalid, Warned }
```

---

## 8. Attributes (namespace `YFex.Validation`)

Attributes are **only read by the generator at compile time** — never instantiated at runtime — so
their constructors can take any constant arguments freely. All `AttributeUsage` include
`Property | Field | Parameter` (parameter = positional record members with `[property: …]`
not required; the generator maps primary-ctor parameters to properties).

| Attribute | Applies to | Generates | Code / args |
|---|---|---|---|
| `[Validatable]` | class/record/struct | forces `IValidatable<T>` even without member rules (e.g. only invariants / fluent) | — |
| `[Required]` | any | null / default / empty-or-whitespace string / empty collection fails | `required` |
| `[NotEmpty]` | string/collections | empty fails, whitespace allowed | `notempty` |
| `[Length(min, max)]` / `[Length(max: 80)]` | string/collections | length bounds | `len` (min,max) |
| `[Range(min, max, Coerce = false)]` | `IComparable<T>`/number/`DateOnly`/`Money`/`Percentual` | bounds; `Coerce=true` clamps instead of failing (queries/paging) | `range` (min,max) |
| `[Range("0.01", "999.99")]` | decimals, Money, dates | string overload: parsed **at compile time** with `T.Parse(…, InvariantCulture)` (attributes can't take `decimal`) | `range` |
| `[Pattern(@"^\d{5}-\d{3}$")]` | string | emits a `[GeneratedRegex]` partial method | `pattern` |
| `[Email]` | string | span-based check (no regex) | `email` |
| `[ValidateWith<TRule>]` / `[ValidateWith<TRule>(arg)]` | any | calls `IValueRule`/`ITextRule`/`IAsyncValueRule` statically | `TRule.Code` |
| `[Each]` | collections | applies the member's other attributes to **each element**; if element is validatable, validates it with indexed paths | per element |
| `[Invariant]` | method (static on records/structs; instance on StateObjects) | cross-field check; generator infers the members it reads | its own codes |
| `[Invariant(Reads = [nameof(A), nameof(B)])]` | method | explicit inputs (analyzer warns when body reads others) | — |
| `[ValidatesAs<T>(Prefix = null, Profile = null)]` | StateObject VM (07) | borrow T's rules by name | — |
| `[MapsTo("Document")]` / `[MapsTo("Address.City")]` | VM property | name mapping for `[ValidatesAs]` in both directions | — |
| `[FieldCode("custom.code")]` | any attribute-bearing member | overrides the code emitted for that member's failures | — |

Common named arguments on every rule attribute:
- `Severity = Severity.Warning` — produce a warning instead of an error.
- `Code = "my.code"` — override the rule code (message key).
- `Profile = "final"` — rule only active for that profile (07 §4.3); default = all profiles.
- `When = nameof(IsCompany)` — only when the named **bool** member is true (the ONLY conditional
  allowed on attributes; anything more complex → `[Invariant]` or YFex.Rules).

**DataAnnotations compatibility:** the generator also reads `System.ComponentModel.DataAnnotations`
`[Required]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[Range]`, `[RegularExpression]`,
`[EmailAddress]` **from syntax/symbols** (no runtime reflection) and maps them to the built-ins.
Stacking both kinds on one member → warning YFV0010.

## 9. Built-in rule implementations

Static classes in `YFex.Validation.BuiltIn` used by generated code (all `[MethodImpl(AggressiveInlining)]`
where small):

| Check | Implementation notes |
|---|---|
| Required (string) | `string.IsNullOrWhiteSpace` |
| Length | `(uint)(len - min) <= (uint)(max - min)` single compare |
| Range | generic `where T : IComparisonOperators<T,T,bool>` or `IComparable<T>` |
| Email | span scan: one `@`, non-empty local/domain, domain has `.`, no whitespace — `SearchValues<char>` for invalid chars. Not RFC-complete on purpose. |
| Digits only / masks | `Ascii.IsDigit`, `SearchValues<char>` |
| Pattern | `[GeneratedRegex]` emitted per attribute (AOT-safe, source-generated) |
| Each | `for` over `ImmutableArray<T>`/`T[]`/`List<T>`/`IReadOnlyList<T>`/`StateList<T>` (indexer), never `IEnumerable<T>` boxing enumerators — generator picks the fastest shape |

Brazilian document rules (`CpfOrCnpj`, `Cpf`, `Cnpj`, `InscricaoEstadual`) are **not** in core —
they live next to the value objects in `YFex.Entity.Public` as `ITextRule` structs. Prefer value
objects ("parse, don't validate") on domain/command types; use the rules for primitive `string`
fields at UI/DTO boundaries.
