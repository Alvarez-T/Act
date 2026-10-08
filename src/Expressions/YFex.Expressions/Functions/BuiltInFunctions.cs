using System.Collections.Immutable;

namespace YFex.Expressions;

/// <summary>
/// Default functions of expressions. All are total and null-propagating (a null argument gives null — the interpreter
/// handles that before calling), keep the argument's value type (<c>round(Money) → Money</c>) and never read clocks
/// (use <c>{"var":"$effectiveDate"}</c>).
/// </summary>
public static class BuiltInFunctions
{
    private static ExprFunctionParameter P(string name, TypeRef type, string? description = null) => new(name, type, description);
    private static readonly ImmutableArray<TypeConstraint> Numeric = [TypeConstraint.Numeric];
    private static readonly ImmutableArray<TypeConstraint> Comparable = [TypeConstraint.Comparable];

    public static ImmutableArray<ExprFunction> All { get; } =
    [
        // ── Math ────────────────────────────────────────────────────────────
        ExprFunction.Create("abs", TypeRef.T, [P("value", TypeRef.T)], static a => Map(a[0], Math.Abs),
            "Absolute value, same type as the argument.", Numeric, "math"),
        ExprFunction.Create("min", TypeRef.T, [P("a", TypeRef.T), P("b", TypeRef.T)], static a => a[0].CompareTo(a[1]) <= 0 ? a[0] : a[1],
            "The smaller of two values of the same type (numbers, amounts, dates, text).", Comparable, "math"),
        ExprFunction.Create("max", TypeRef.T, [P("a", TypeRef.T), P("b", TypeRef.T)], static a => a[0].CompareTo(a[1]) >= 0 ? a[0] : a[1],
            "The larger of two values of the same type (numbers, amounts, dates, text).", Comparable, "math"),
        ExprFunction.Create("clamp", TypeRef.T, [P("value", TypeRef.T), P("min", TypeRef.T), P("max", TypeRef.T)],
            static a => a[0].CompareTo(a[1]) < 0 ? a[1] : a[0].CompareTo(a[2]) > 0 ? a[2] : a[0],
            "The value limited to [min, max].", Comparable, "math"),
        ExprFunction.Create("round", TypeRef.T, [P("value", TypeRef.T), P("decimals", ExprType.Int, "Digits after the decimal point (0–10).")],
            static a => Num(a[0], Math.Round(Dec(a[0]), (int)Math.Clamp(Long(a[1]), 0, 10), MidpointRounding.AwayFromZero)),
            "Rounds half away from zero to a number of decimals, keeping the type.", Numeric, "math"),
        ExprFunction.Create("floor", TypeRef.T, [P("value", TypeRef.T)], static a => Map(a[0], Math.Floor),
            "Largest whole value ≤ the argument.", Numeric, "math"),
        ExprFunction.Create("ceiling", TypeRef.T, [P("value", TypeRef.T)], static a => Map(a[0], Math.Ceiling),
            "Smallest whole value ≥ the argument.", Numeric, "math"),
        ExprFunction.Create("percentOf", TypeRef.T, [P("amount", TypeRef.T), P("percent", TypeRef.U, "Percent, e.g. 5 for 5% (a number or a Percentual).")],
            static a => Num(a[0], Dec(a[0]) * Dec(a[1]) / 100m),
            "percent% of amount, in the amount's type: percentOf(Total, 5) = 5% of Total.", [TypeConstraint.Numeric, TypeConstraint.Numeric], "math"),

        // ── Text ────────────────────────────────────────────────────────────
        ExprFunction.Create("lower", ExprType.Text, [P("text", ExprType.Text)], static a => RawValue.From(Str(a[0]).ToLowerInvariant()),
            "Lower-case (invariant).", category: "text"),
        ExprFunction.Create("upper", ExprType.Text, [P("text", ExprType.Text)], static a => RawValue.From(Str(a[0]).ToUpperInvariant()),
            "Upper-case (invariant).", category: "text"),
        ExprFunction.Create("trim", ExprType.Text, [P("text", ExprType.Text)], static a => RawValue.From(Str(a[0]).Trim()),
            "Without leading/trailing spaces.", category: "text"),
        ExprFunction.Create("length", ExprType.Int, [P("text", ExprType.Text)], static a => RawValue.From((long)Str(a[0]).Length),
            "Number of characters.", category: "text"),
        ExprFunction.Create("contains", ExprType.Bool, [P("text", ExprType.Text), P("part", ExprType.Text)],
            static a => RawValue.From(Str(a[0]).Contains(Str(a[1]), StringComparison.OrdinalIgnoreCase)),
            "True when text contains part (case-insensitive).", category: "text"),
        ExprFunction.Create("startsWith", ExprType.Bool, [P("text", ExprType.Text), P("prefix", ExprType.Text)],
            static a => RawValue.From(Str(a[0]).StartsWith(Str(a[1]), StringComparison.OrdinalIgnoreCase)),
            "True when text starts with prefix (case-insensitive).", category: "text"),
        ExprFunction.Create("endsWith", ExprType.Bool, [P("text", ExprType.Text), P("suffix", ExprType.Text)],
            static a => RawValue.From(Str(a[0]).EndsWith(Str(a[1]), StringComparison.OrdinalIgnoreCase)),
            "True when text ends with suffix (case-insensitive).", category: "text"),

        // ── Dates ───────────────────────────────────────────────────────────
        ExprFunction.Create("year", ExprType.Int, [P("date", ExprType.Date)], static a => RawValue.From((long)Date(a[0]).Year), "Year of a date.", category: "date"),
        ExprFunction.Create("month", ExprType.Int, [P("date", ExprType.Date)], static a => RawValue.From((long)Date(a[0]).Month), "Month 1–12.", category: "date"),
        ExprFunction.Create("day", ExprType.Int, [P("date", ExprType.Date)], static a => RawValue.From((long)Date(a[0]).Day), "Day of month 1–31.", category: "date"),
        ExprFunction.Create("dayOfWeek", ExprType.Int, [P("date", ExprType.Date)],
            static a => RawValue.From((long)(Date(a[0]).DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Date(a[0]).DayOfWeek)),
            "ISO day of week: 1 = Monday … 7 = Sunday.", category: "date"),
        ExprFunction.Create("addDays", ExprType.Date, [P("date", ExprType.Date), P("days", ExprType.Int)],
            static a => RawValue.From(Date(a[0]).AddDays((int)Math.Clamp(Long(a[1]), -36_500, 36_500))), "date + days.", category: "date"),
        ExprFunction.Create("daysBetween", ExprType.Int, [P("from", ExprType.Date), P("to", ExprType.Date)],
            static a => RawValue.From((long)(Date(a[1]).DayNumber - Date(a[0]).DayNumber)), "Days from 'from' to 'to' (negative when earlier).", category: "date"),
        ExprFunction.Create("date", ExprType.Date, [P("year", ExprType.Int), P("month", ExprType.Int), P("day", ExprType.Int)],
            static a => TryDate(Long(a[0]), Long(a[1]), Long(a[2])), "A date from its parts (null when invalid).", category: "date"),
    ];

    // ── Helpers (also used by app functions) ─────────────────────────────────

    /// <summary>Applies <paramref name="f"/> to a numeric value, keeping its type (Money stays Money).</summary>
    public static RawValue Map(in RawValue value, Func<decimal, decimal> f)
        => value.TryGet(out decimal d) ? RawValueConvert.FromDecimal(f(d), value.Type ?? RawType.Decimal) : RawValue.None;

    /// <summary><paramref name="result"/> in the value type of <paramref name="like"/>.</summary>
    public static RawValue Num(in RawValue like, decimal result) => RawValueConvert.FromDecimal(result, like.Type ?? RawType.Decimal);

    public static decimal Dec(in RawValue v) => v.TryGet(out decimal d) ? d : 0m;
    public static long Long(in RawValue v) => v.TryGet(out long l) ? l : v.TryGet(out decimal d) ? (long)d : 0L;
    public static string Str(in RawValue v) => v.TryGet(out string? s) ? s ?? "" : "";
    public static DateOnly Date(in RawValue v) => v.TryGet(out DateOnly d) ? d : v.TryGet(out DateTime dt) ? DateOnly.FromDateTime(dt) : default;

    private static RawValue TryDate(long y, long m, long d)
        => y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth((int)y, (int)m)
            ? RawValue.From(new DateOnly((int)y, (int)m, (int)d))
            : RawValue.None;
}
