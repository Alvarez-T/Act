namespace YFex.Expressions;

/// <summary>
/// Marks a container of expression functions (YFex.Expressions.Generator emits their descriptors):
/// <list type="bullet">
/// <item><b>a static partial class</b> with <c>[ExprFunction]</c> static methods → generated <c>All</c> (implemented functions);</item>
/// <item><b>a partial interface</b> with <c>[ExprFunction]</c> <c>static abstract</c> methods → generated <c>Contracts</c> (abstract
///   signatures a library declares) and <c>Implement&lt;TImpl&gt;()</c> (the same functions bound to a domain type implementing the
///   interface — static dispatch, no reflection).</item>
/// </list>
/// <code>
/// [ExprFunctions(Category = "calendar")]
/// public partial interface ICalendarFunctions
/// {
///     [ExprFunction("isHoliday", Description = "True when the date is a public holiday where the sale happens.")]
///     static abstract bool IsHoliday(DateOnly date);
/// }
/// var catalog = FunctionCatalog.BuiltIn.With(ICalendarFunctions.Implement&lt;BrazilCalendar&gt;().AsSpan());
/// </code>
/// Parameter/return types: bool, int, long, decimal, double, string, DateOnly, DateTime, TimeOnly, Guid, enums and any
/// <see cref="IRawValue{TSelf}"/> type (Money, Mass, Percentual, app types). Functions must be pure and synchronous.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
public sealed class ExprFunctionsAttribute : Attribute
{
    public string? Category { get; init; }
}

/// <summary>An expression function (see <see cref="ExprFunctionsAttribute"/>). Name defaults to the method name in camelCase.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ExprFunctionAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    /// <summary>What the function does — shown to authors and to AI (required in practice: the generator warns without it).</summary>
    public string? Description { get; init; }
    /// <summary>False when the result depends on context (clock, user…): hosts requiring replayable results reject it. Default true.</summary>
    public bool Deterministic { get; init; } = true;
}

/// <summary>Conversions used by generated function adapters (and hand-written ones).</summary>
public static class ExprFunctionArgs
{
    public static T Get<T>(in RawValue value)
        => RawValueConvert.TryGet(in value, out T result)
            ? result
            : throw new InvalidOperationException($"Function argument {value} is not a {typeof(T).Name}.");

    public static TEnum GetEnum<TEnum>(in RawValue value) where TEnum : unmanaged, Enum
        => value.TryGetEnum(out TEnum result)
            ? result
            : throw new InvalidOperationException($"Function argument {value} is not a {typeof(TEnum).Name}.");

    public static RawValue Result<T>(T value) => RawValueConvert.From(value);

    public static RawValue ResultEnum<TEnum>(TEnum value) where TEnum : unmanaged, Enum => RawValue.FromEnum(value);
}
