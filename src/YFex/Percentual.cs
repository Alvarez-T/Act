using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using YFex.Converters;
using YFex.Json;

namespace Act.Utils;

// ── TypeConverter declared here so XAML binding (WPF / Avalonia) works
// without a separate [assembly:…] registration.
[TypeConverter(typeof(PercentualTypeConverter))]
[JsonConverter(typeof(PercentualJsonConverter))]
public readonly record struct Percentual
    : IComparable<Percentual>,
      IComparable,
      IFormattable,
      ISpanFormattable,          // .NET 6+ — zero-alloc ToString into a Span<char>
      ISpanParsable<Percentual>, // .NET 7+ — IParsable + TryParse(ReadOnlySpan<char>)
      IParsable<Percentual>,     // .NET 7+ — generic-parser discovery, minimal-API binding
      IAdditionOperators<Percentual, Percentual, Percentual>,
      ISubtractionOperators<Percentual, Percentual, Percentual>,
      IMultiplyOperators<Percentual, decimal, Percentual>,
      IDivisionOperators<Percentual, decimal, Percentual>,
      IUnaryNegationOperators<Percentual, Percentual>,
      IComparisonOperators<Percentual, Percentual, bool>
{
    // ── Internal storage ─────────────────────────────────────────────────────
    // Stored as the percentage value (e.g. 47.5 for "47.5 %"), never as a
    // fraction.  All arithmetic stays in percentage-value space; the /100
    // conversion happens ONLY at the point of explicit extraction.
    private readonly decimal _percentageValue;

    // ── Configuration ────────────────────────────────────────────────────────
    /// <summary>
    /// Decimal places used when no explicit value is provided.
    /// Prefer constructor overloads with an explicit argument over mutating
    /// this in multi-threaded code.
    /// </summary>
    public static int DefaultDecimalPlaces { get; set; } = 2;

    /// <summary>Decimal places this instance was rounded to at construction.</summary>
    public int DecimalPlaces { get; }

    // ── Well-known constants ─────────────────────────────────────────────────
    public static Percentual Zero => new(0m, DefaultDecimalPlaces);
    public static Percentual Full => new(100m, DefaultDecimalPlaces);
    public static Percentual Half => new(50m, DefaultDecimalPlaces);

    // ── Construction ─────────────────────────────────────────────────────────
    /// <param name="percentageValue">
    ///   The percentage value as displayed — 47.5 means "47.5 %", NOT 0.475.
    /// </param>
    public Percentual(decimal percentageValue, int decimalPlaces)
    {
        if (decimalPlaces < 0)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces), "Must be ≥ 0.");

        DecimalPlaces = decimalPlaces;
        _percentageValue = Math.Round(percentageValue, decimalPlaces, MidpointRounding.AwayFromZero);
    }

    // ── Named factory methods (replace the dangerous implicit operators) ──────

    /// <summary>
    /// Creates a <see cref="Percentual"/> from a percentage value.
    /// <c>FromPercentage(47.5m)</c> → "47.5 %"
    /// </summary>
    public static Percentual FromPercentage(decimal percentageValue, int? decimalPlaces = null)
        => new(percentageValue, decimalPlaces ?? DefaultDecimalPlaces);

    /// <summary>
    /// Creates a <see cref="Percentual"/> from a decimal fraction.
    /// <c>FromFraction(0.475m)</c> → "47.5 %"
    /// </summary>
    public static Percentual FromFraction(decimal fractionValue, int? decimalPlaces = null)
        => new(fractionValue * 100m, decimalPlaces ?? DefaultDecimalPlaces);

    // ── Extraction ───────────────────────────────────────────────────────────

    /// <summary>Returns the raw percentage value (47.5 for "47.5 %").</summary>
    public decimal ToPercentageValue() => _percentageValue;

    /// <summary>Returns the fraction equivalent (0.475 for "47.5 %").</summary>
    public decimal ToFraction() => _percentageValue / 100m;

    // ── Domain helpers ───────────────────────────────────────────────────────

    /// <summary>Applies this percentage to <paramref name="value"/>.</summary>
    public decimal ApplyTo(decimal value) => value * _percentageValue / 100m;

    /// <summary>Returns <c>100 % − this</c>.</summary>
    public Percentual Complement() => new(100m - _percentageValue, DecimalPlaces);

    /// <summary>Clamps this value between [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public Percentual Clamp(Percentual min, Percentual max)
    {
        if (min._percentageValue > max._percentageValue)
            throw new ArgumentException("min must be ≤ max.");
        return new(Math.Clamp(_percentageValue, min._percentageValue, max._percentageValue), DecimalPlaces);
    }

    /// <summary>Returns a copy re-rounded to <paramref name="decimalPlaces"/>.</summary>
    public Percentual WithDecimalPlaces(int decimalPlaces) => new(_percentageValue, decimalPlaces);

    public bool IsZero => _percentageValue == 0m;
    public bool IsFull => _percentageValue == 100m;
    public bool IsPositive => _percentageValue > 0m;
    public bool IsNegative => _percentageValue < 0m;

    public static Percentual operator +(Percentual left, Percentual right)
        => new(left._percentageValue + right._percentageValue,
               Math.Max(left.DecimalPlaces, right.DecimalPlaces));

    public static Percentual operator -(Percentual left, Percentual right)
        => new(left._percentageValue - right._percentageValue,
               Math.Max(left.DecimalPlaces, right.DecimalPlaces));

    /// <summary>Scale the percentage: <c>50% * 2m = 100%</c></summary>
    public static Percentual operator *(Percentual left, decimal right)
        => new(left._percentageValue * right, left.DecimalPlaces);

    /// <summary>Divide the percentage: <c>100% / 4m = 25%</c></summary>
    public static Percentual operator /(Percentual left, decimal right)
    {
        if (right == 0m) throw new DivideByZeroException();
        return new(left._percentageValue / right, left.DecimalPlaces);
    }

    public static Percentual operator -(Percentual value)
        => new(-value._percentageValue, value.DecimalPlaces);

    public static Percentual operator +(Percentual value) => value;

    // Comparison operators (IComparisonOperators + IComparable)
    public static bool operator <(Percentual l, Percentual r) => l._percentageValue < r._percentageValue;
    public static bool operator >(Percentual l, Percentual r) => l._percentageValue > r._percentageValue;
    public static bool operator <=(Percentual l, Percentual r) => l._percentageValue <= r._percentageValue;
    public static bool operator >=(Percentual l, Percentual r) => l._percentageValue >= r._percentageValue;

    public int CompareTo(Percentual other) => _percentageValue.CompareTo(other._percentageValue);
    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is Percentual p) return CompareTo(p);
        throw new ArgumentException($"Object must be of type {nameof(Percentual)}.");
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses "47.5" or "47.5 %" — the trailing % is optional.
    /// A value without % is treated as a percentage value already (not a fraction).
    /// </summary>
    public static Percentual Parse(string s, int? decimalPlaces = null)
        => Parse(s.AsSpan(), null, decimalPlaces);

    public static Percentual Parse(string s, IFormatProvider? provider)
        => Parse(s.AsSpan(), provider, null);

    public static Percentual Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
        => Parse(s, provider, null);

    private static Percentual Parse(ReadOnlySpan<char> s, IFormatProvider? provider, int? decimalPlaces)
    {
        if (!TryParseCore(s, provider, decimalPlaces, out var result))
            throw new FormatException($"Cannot parse '{s}' as a Percentual.");
        return result;
    }

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        out Percentual result) => TryParse(s.AsSpan(), null, out result);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        out Percentual result) => TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(
        ReadOnlySpan<char> s,
        IFormatProvider? provider,
        out Percentual result) => TryParseCore(s, provider, null, out result);

    private static bool TryParseCore(
        ReadOnlySpan<char> s,
        IFormatProvider? provider,
        int? decimalPlaces,
        out Percentual result)
    {
        result = default;
        var span = s.Trim();
        if (span.IsEmpty) return false;

        bool hasSymbol = span[^1] == '%';
        var numberSpan = hasSymbol ? span[..^1].TrimEnd() : span;

        if (!decimal.TryParse(numberSpan, NumberStyles.Number,
                              provider ?? CultureInfo.InvariantCulture, out var value))
            return false;

        // Without a '%' symbol we still treat the number as a percentage value
        // (e.g. "47.5" → 47.5 %, same as "47.5 %").  Use FromFraction explicitly
        // if you have a raw fraction.
        result = new Percentual(value, decimalPlaces ?? DefaultDecimalPlaces);
        return true;
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Returns e.g. "47.50" (no symbol). Use <see cref="ToStringWithSymbol"/> for "47.50 %".</summary>
    public override string ToString()
        => _percentageValue.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (string.IsNullOrEmpty(format))
            return ToString();
        // Allow the caller to pass "F4" etc. directly.
        return _percentageValue.ToString(format, formatProvider ?? CultureInfo.InvariantCulture);
    }

    /// <summary>Zero-alloc formatting into a destination <see cref="Span{T}"/>.</summary>
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        var fmt = format.IsEmpty
            ? $"F{DecimalPlaces}".AsSpan()
            : format;
        return _percentageValue.TryFormat(destination, out charsWritten, fmt,
                                          provider ?? CultureInfo.InvariantCulture);
    }

    /// <summary>Returns "47.50 %".</summary>
    public string ToStringWithSymbol()
        => $"{ToString()} %";

    /// <summary>Returns "47.50 %" with an explicit decimal-places override.</summary>
    public string ToStringWithSymbol(int decimalPlaces)
        => $"{_percentageValue.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture)} %";

    // ── Equality (record struct gives us this, but let's be explicit) ─────────
    // record struct already generates Equals(Percentual) and GetHashCode()
    // from ALL fields.  Because _percentageValue already encodes the rounded
    // value, two Percentuals with the same _percentageValue but different
    // DecimalPlaces are NOT equal — which is the correct semantics.
}