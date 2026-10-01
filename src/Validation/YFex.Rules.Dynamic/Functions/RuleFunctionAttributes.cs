namespace YFex.Rules.Dynamic;

/// <summary>
/// Marks a container of rule functions (the YFex.Rules generator emits their descriptors):
/// <list type="bullet">
/// <item><b>a static partial class</b> with <c>[RuleFunction]</c> static methods → generated <c>All</c> (implemented functions);</item>
/// <item><b>a partial interface</b> with <c>[RuleFunction]</c> <c>static abstract</c> methods → generated <c>Contracts</c> (abstract
///   signatures a library declares) and <c>Implement&lt;TImpl&gt;()</c> (the same functions bound to a domain type implementing the
///   interface — static dispatch, no reflection).</item>
/// </list>
/// <code>
/// [RuleFunctions(Category = "calendar")]
/// public partial interface ICalendarFunctions
/// {
///     [RuleFunction("isHoliday", Description = "True when the date is a public holiday where the sale happens.")]
///     static abstract bool IsHoliday(DateOnly date);
/// }
/// var catalog = FunctionCatalog.BuiltIn.With(ICalendarFunctions.Implement&lt;BrazilCalendar&gt;().AsSpan());
/// </code>
/// Parameter/return types: bool, int, long, decimal, double, string, DateOnly, DateTime, TimeOnly, Guid, enums and any
/// <see cref="IRawValue{TSelf}"/> type (Money, Mass, Percentual, app types). Functions must be pure and synchronous.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
public sealed class RuleFunctionsAttribute : Attribute
{
    public string? Category { get; init; }
}

/// <summary>A rule function (see <see cref="RuleFunctionsAttribute"/>). Name defaults to the method name in camelCase.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RuleFunctionAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    /// <summary>What the function does — shown to rule authors and to AI (required in practice: YFR0044 warns without it).</summary>
    public string? Description { get; init; }
}

/// <summary>Conversions used by generated function adapters (and hand-written ones).</summary>
public static class RuleFunctionArgs
{
    public static T Get<T>(in RawValue value)
        => RawValueConvert.TryGet(in value, out T result)
            ? result
            : throw new InvalidOperationException($"Rule function argument {value} is not a {typeof(T).Name}.");

    public static TEnum GetEnum<TEnum>(in RawValue value) where TEnum : unmanaged, Enum
        => value.TryGetEnum(out TEnum result)
            ? result
            : throw new InvalidOperationException($"Rule function argument {value} is not a {typeof(TEnum).Name}.");

    public static RawValue Result<T>(T value) => RawValueConvert.From(value);

    public static RawValue ResultEnum<TEnum>(TEnum value) where TEnum : unmanaged, Enum => RawValue.FromEnum(value);
}
