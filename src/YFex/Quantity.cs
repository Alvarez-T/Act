using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Act.Utils;

/// <summary>
/// A quantity with up to 4-decimal scale (DF-e <c>qCom</c>/<c>qTrib</c> carry up to
/// 4 fraction digits). Value object with the modern numeric/parse/format interfaces.
///
/// <code>
/// Quantity q = 3.5m;        // implicit
/// (q * 2m);                 // 7.0000
/// q.ToXmlString();          // "3.5" (invariant, trimmed)
/// </code>
/// </summary>
public readonly record struct Quantity :
    IComparable<Quantity>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<Quantity>,
    ISpanParsable<Quantity>,
    IAdditionOperators<Quantity, Quantity, Quantity>,
    ISubtractionOperators<Quantity, Quantity, Quantity>,
    IMultiplyOperators<Quantity, decimal, Quantity>,
    IComparisonOperators<Quantity, Quantity, bool>,
    IMinMaxValue<Quantity>,
    IAdditiveIdentity<Quantity, Quantity>
{
    public const int Scale = 4;
    private readonly decimal _value;
    private Quantity(decimal v, bool _) => _value = v;
    public Quantity(decimal value) => _value = Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    public decimal Value => _value;
    public bool IsZero => _value == 0m;
    public bool IsPositive => _value > 0m;

    public static readonly Quantity Zero = default;
    public static Quantity MinValue => new(decimal.MinValue, false);
    public static Quantity MaxValue => new(decimal.MaxValue, false);
    static Quantity IMinMaxValue<Quantity>.MinValue => MinValue;
    static Quantity IMinMaxValue<Quantity>.MaxValue => MaxValue;
    static Quantity IAdditiveIdentity<Quantity, Quantity>.AdditiveIdentity => Zero;

    public static Quantity operator +(Quantity a, Quantity b) => new(a._value + b._value);
    public static Quantity operator -(Quantity a, Quantity b) => new(a._value - b._value);
    public static Quantity operator *(Quantity a, decimal factor) => new(a._value * factor);
    public static bool operator <(Quantity a, Quantity b) => a._value < b._value;
    public static bool operator >(Quantity a, Quantity b) => a._value > b._value;
    public static bool operator <=(Quantity a, Quantity b) => a._value <= b._value;
    public static bool operator >=(Quantity a, Quantity b) => a._value >= b._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Quantity(decimal value) => new(value);
    public static explicit operator decimal(Quantity q) => q._value;

    public int CompareTo(Quantity other) => _value.CompareTo(other._value);
    public int CompareTo(object? obj) => obj is Quantity q ? CompareTo(q) : 1;

    public override string ToString() => ToString(null, CultureInfo.CurrentCulture);
    public string ToString(string? format, IFormatProvider? provider)
        => _value.ToString(format ?? "0.####", provider ?? CultureInfo.CurrentCulture);
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => _value.TryFormat(destination, out charsWritten, format.IsEmpty ? "0.####" : format, provider);

    /// <summary>Invariant, trimmed to significant decimals — the DF-e wire form.</summary>
    public string ToXmlString() => _value.ToString("0.####", CultureInfo.InvariantCulture);

    public static bool IsValid([NotNullWhen(true)] string? s) => TryParse(s, out _);
    public static Quantity Parse(string s) => Parse(s.AsSpan(), null);
    public static Quantity Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), provider);
    public static Quantity Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out var q) ? q : throw new FormatException($"\"{s}\" is not a valid quantity.");
    public static bool TryParse([NotNullWhen(true)] string? s, out Quantity result) => TryParse(s.AsSpan(), null, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Quantity result) => TryParse(s.AsSpan(), provider, out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Quantity result)
    {
        if (decimal.TryParse(s, NumberStyles.Number, provider ?? CultureInfo.CurrentCulture, out var d) ||
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
        {
            result = new Quantity(d);
            return true;
        }
        result = default;
        return false;
    }
}
