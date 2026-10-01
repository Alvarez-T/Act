namespace YFex.Validation;

/// <summary>
/// How serious a violation is. Only <see cref="Error"/> blocks (<c>IsValid = false</c>).
/// "Needs approval" is NOT a severity — it is a YFex.Rules directive (D21/P4).
/// Values match the legacy <c>YFex.State.Validation.ValidationSeverity</c>.
/// </summary>
public enum Severity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Stable, namespaced identifier of a check or rule. Persisted (audit), sent over the wire and used
/// as the message-catalog key. Built-ins are short (<c>"required"</c>, <c>"len"</c>, <c>"range"</c>,
/// <c>"email"</c>, <c>"pattern"</c>); module codes are prefixed (<c>"br.document"</c>,
/// <c>"sale.discount.cap"</c>, <c>"nfe.539"</c>). Create from string literals / static readonly
/// fields so no allocation happens per use.
/// </summary>
public readonly record struct RuleCode(string Value)
{
    public static implicit operator RuleCode(string value) => new(value);

    /// <summary>True for <c>default(RuleCode)</c>.</summary>
    public bool IsEmpty => Value is null;

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Compile-time identifier of a member inside ONE validatable type, emitted by the generator as
/// constants (<c>CreateCustomer.FieldIds.Name = 0</c>). Dictionary-free key for per-field state.
/// </summary>
public readonly record struct FieldId(ushort Value);

/// <summary>Built-in rule codes used by generated code and the built-in checks.</summary>
public static class RuleCodes
{
    public const string Required = "required";
    public const string NotEmpty = "notempty";
    /// <summary>Both bounds (args: min, max).</summary>
    public const string Length = "len";
    /// <summary>Only a lower bound (arg: min).</summary>
    public const string LengthMin = "len.min";
    /// <summary>Only an upper bound (arg: max).</summary>
    public const string LengthMax = "len.max";
    public const string Range = "range";
    public const string Pattern = "pattern";
    public const string Email = "email";
}
