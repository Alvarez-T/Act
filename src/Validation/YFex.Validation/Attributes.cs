namespace YFex.Validation;

// Attributes are only READ by YFex.Validation.Generator at compile time — never instantiated at runtime.

/// <summary>Forces <see cref="IValidatable{TSelf}"/> generation even without member rules
/// (e.g. only invariants, or rules added by fluent configuration).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ValidatableAttribute : Attribute;

/// <summary>
/// Base of every rule attribute. Common named arguments:
/// <list type="bullet">
/// <item><see cref="Severity"/> — <c>Severity.Warning</c> produces a warning instead of an error.</item>
/// <item><see cref="Code"/> — overrides the rule code (message key).</item>
/// <item><see cref="Profile"/> — rule only active for that profile (e.g. <c>"final"</c>); default = all.</item>
/// <item><see cref="When"/> — only when the named <b>bool</b> member is true (the ONLY conditional allowed;
/// anything more complex → <see cref="InvariantAttribute"/> or YFex.Rules).</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = true)]
public abstract class ValidationRuleAttribute : Attribute
{
    public Severity Severity { get; init; } = Severity.Error;
    public string? Code { get; init; }
    public string? Profile { get; init; }
    public string? When { get; init; }
}

/// <summary>null / default / empty-or-whitespace string / empty collection fails. Code <c>required</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RequiredAttribute : ValidationRuleAttribute;

/// <summary>Empty string / empty collection fails; whitespace allowed. Code <c>notempty</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NotEmptyAttribute : ValidationRuleAttribute;

/// <summary>
/// Length bounds of a string or collection (inclusive). <c>[Length(2, 80)]</c>, <c>[Length(max: 80)]</c>.
/// null is not checked (use <see cref="RequiredAttribute"/>). Code <c>len</c> (args min, max).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class LengthAttribute(int min = 0, int max = int.MaxValue) : ValidationRuleAttribute
{
    public int Min { get; } = min;
    public int Max { get; } = max;
}

/// <summary>
/// Inclusive bounds for numbers, dates, <see cref="Money"/>, <see cref="Percentual"/>… The string overload is
/// for types attributes cannot express (decimal, DateOnly, Money): parsed with invariant culture.
/// <see cref="Coerce"/> = clamp instead of failing (queries/paging; applied by the pipeline).
/// Code <c>range</c> (args min, max).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RangeAttribute : ValidationRuleAttribute
{
    public RangeAttribute(int min, int max) { Min = min; Max = max; }
    public RangeAttribute(long min, long max) { Min = min; Max = max; }
    public RangeAttribute(double min, double max) { Min = min; Max = max; }
    public RangeAttribute(string min, string max) { Min = min; Max = max; }

    public object Min { get; }
    public object Max { get; }
    public bool Coerce { get; init; }
}

/// <summary>Regular-expression match for strings (null/empty not checked). Code <c>pattern</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class PatternAttribute(string pattern) : ValidationRuleAttribute
{
    public string Pattern { get; } = pattern;
}

/// <summary>Span-based e-mail shape check (not RFC-complete on purpose). Code <c>email</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EmailAttribute : ValidationRuleAttribute;

/// <summary>
/// Calls a rule type statically: <see cref="IValueRule{T}"/>, <see cref="IValueRule{T,TArg}"/>,
/// <see cref="ITextRule"/>, <see cref="ITextRule{TArg}"/> or <see cref="IAsyncValueRule{T}"/> (async rules
/// are not part of the synchronous <c>Validate</c>). Code = <c>TRule.Code</c>.
/// <c>[ValidateWith&lt;CpfOrCnpj&gt;]</c>, <c>[ValidateWith&lt;MultipleOf&gt;(5)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ValidateWithAttribute<TRule> : ValidationRuleAttribute
{
    public ValidateWithAttribute() { }
    public ValidateWithAttribute(object argument) => Argument = argument;
    public object? Argument { get; }
}

/// <summary>
/// Applies the member's other rules to <b>each element</b> of a collection (Required/NotEmpty stay on the
/// collection itself). When the element type is validatable, every element is validated with indexed
/// paths (<c>Lines[3].Quantity</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EachAttribute : Attribute;

/// <summary>
/// Cross-field check. Signatures: <c>static void X(in T v, ref ViolationWriter w)</c> (also <c>bool</c>
/// return), or instance <c>void X(ref ViolationWriter w)</c>. The generator infers the members read and
/// runs the invariant only when none of them already failed; <see cref="Reads"/> overrides inference.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InvariantAttribute : Attribute
{
    public string[]? Reads { get; init; }
}

/// <summary>Overrides the code of every failure of this member (rules without their own <c>Code</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class FieldCodeAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}

/// <summary>
/// Strictness level for profile-scoped rules (<c>Profile = "final"</c>). Name-based so profiles match
/// across assemblies. <see cref="All"/> (default) runs every rule.
/// </summary>
public readonly record struct ProfileId(string? Name)
{
    public static ProfileId All => default;

    public static implicit operator ProfileId(string name) => new(name);

    /// <summary>True when rules of <paramref name="profile"/> run under this id.</summary>
    public bool Includes(string profile) => Name is null || string.Equals(Name, profile, StringComparison.Ordinal);
}

// ── View models (YFex.State, 07 §4) ─────────────────────────────────────────────────────

/// <summary>
/// "This view model is an editing surface for <typeparamref name="T"/> (a command, query or DTO): validate my
/// properties with T's rules" (D15). Properties borrow T's member rules by name (or <see cref="MapsToAttribute"/>);
/// the generator also emits the contract ↔ VM path map, <c>ApplyServer&lt;T&gt;</c>, <c>ToT()</c> and submit
/// validation. Only meaningful on <c>YFex.State.StateObject</c> subclasses.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ValidatesAsAttribute<T> : Attribute
{
    /// <summary>VM property name = <c>Prefix + contract member name</c> (<c>Prefix = "Address"</c>: City ↔ AddressCity).</summary>
    public string? Prefix { get; init; }

    /// <summary>Strictness profile: the contract applies when the bag's active profile is this one (07 §4.3).</summary>
    public string? Profile { get; init; }
}

/// <summary>
/// Maps a view-model property to a contract member path of a <c>[ValidatesAs]</c> contract
/// (<c>"Document"</c>, <c>"Address.City"</c>). Works in both directions: rules flow contract → VM, server
/// violations flow back to this property.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class MapsToAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
