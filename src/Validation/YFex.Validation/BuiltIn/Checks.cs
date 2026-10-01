using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace YFex.Validation.BuiltIn;

/// <summary>Primitive checks used by generated code. Small, allocation-free, inlinable.</summary>
public static class Checks
{
    /// <summary>Single unsigned compare: <c>min ≤ length ≤ max</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LengthWithin(int length, int min, int max) => (uint)(length - min) <= (uint)(max - min);

    /// <summary>Inclusive range check for any comparable value (numbers, dates, Money, Percentual…).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Within<T>(T value, T min, T max) where T : IComparable<T>
        => value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0;

    /// <summary>Clamps <paramref name="value"/> into the inclusive range (used by <c>Range(Coerce = true)</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        => value.CompareTo(min) < 0 ? min : value.CompareTo(max) > 0 ? max : value;

    /// <summary>True for <c>default(T)</c> (Required on value types).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefault<T>(T value) => EqualityComparer<T>.Default.Equals(value, default!);

    /// <summary>True when every char is an ASCII digit (empty = true).</summary>
    public static bool IsDigitsOnly(ReadOnlySpan<char> value) => value.IndexOfAnyExcept(s_digits) < 0;

    private static readonly SearchValues<char> s_digits = SearchValues.Create("0123456789");
}

/// <summary>
/// Span-based e-mail shape check: exactly one <c>@</c>, non-empty local part and domain, domain contains a
/// <c>.</c> not at its ends, no whitespace/control/forbidden chars. Not RFC-complete on purpose.
/// Also usable as <c>[ValidateWith&lt;Email&gt;]</c>.
/// </summary>
public readonly struct Email : ITextRule
{
    private static readonly SearchValues<char> s_forbidden =
        SearchValues.Create(" \t\r\n\"(),:;<>[\\]");

    public static RuleCode Code => RuleCodes.Email;

    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.Length is < 3 or > 254) return false;
        if (value.ContainsAny(s_forbidden)) return false;

        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@')) return false;

        ReadOnlySpan<char> domain = value[(at + 1)..];
        int dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1 && domain[0] != '.' && !domain.Contains("..", StringComparison.Ordinal)
               && !ContainsControl(value);
    }

    private static bool ContainsControl(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
            if (char.IsControl(c)) return true;
        return false;
    }
}

/// <summary>Only ASCII digits (<c>[ValidateWith&lt;DigitsOnly&gt;]</c>). Code <c>digits</c>.</summary>
public readonly struct DigitsOnly : ITextRule
{
    public static RuleCode Code => "digits";
    public static bool IsValid(ReadOnlySpan<char> value) => Checks.IsDigitsOnly(value);
}

/// <summary>Only ASCII letters and digits. Code <c>alnum</c>.</summary>
public readonly struct AsciiLettersOrDigits : ITextRule
{
    public static RuleCode Code => "alnum";
    public static bool IsValid(ReadOnlySpan<char> value) => Ascii.IsValid(value) && value.IndexOfAnyExcept(s_alnum) < 0;

    private static readonly SearchValues<char> s_alnum =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
}
