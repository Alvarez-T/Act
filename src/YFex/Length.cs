using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>Units <see cref="Length"/> can be expressed in. Canonical storage is the metre.</summary>
public enum LengthUnit
{
    /// <summary>Millimetre (mm) — 0.001 m.</summary>
    Millimetre,
    /// <summary>Centimetre (cm) — 0.01 m.</summary>
    Centimetre,
    /// <summary>Metre (m) — the canonical unit.</summary>
    Metre,
    /// <summary>Kilometre (km) — 1000 m.</summary>
    Kilometre,
    /// <summary>Inch (in) — 0.0254 m (exact by definition).</summary>
    Inch,
    /// <summary>Foot (ft) — 0.3048 m (exact by definition).</summary>
    Foot,
    /// <summary>Yard (yd) — 0.9144 m (exact by definition).</summary>
    Yard,
    /// <summary>Mile (mi) — 1609.344 m (exact by definition).</summary>
    Mile,
}

/// <summary>
/// A physical length stored canonically in metres (up to 4-decimal / 0.1&#160;mm scale),
/// unit-agnostic in storage and unit-aware only when you read or format it. Mirrors the
/// value-object shape of <see cref="Mass"/> / <see cref="Quantity"/> — implicit from a
/// metre <see cref="decimal"/>, full numeric/parse/format interfaces, and exact metric ↔
/// imperial conversions (inch = 0.0254 m, exact by definition).
///
/// <code>
/// var d = Length.Centimetres(150m); // 1.5 m
/// d.InInches;                       // 59.0551 (in)
/// (d * 2m).ToString("cm");          // "300 cm"
/// d.To(LengthUnit.Millimetre);      // 1500.0000
/// d.ToXmlString();                  // "1.5" (m, invariant)
/// </code>
/// </summary>
[TypeConverter(typeof(LengthTypeConverter))]
[JsonConverter(typeof(LengthJsonConverter))]
public readonly record struct Length :
    IComparable<Length>,
    IComparable,
    IFormattable,
    IParsable<Length>,
    IAdditionOperators<Length, Length, Length>,
    ISubtractionOperators<Length, Length, Length>,
    IMultiplyOperators<Length, decimal, Length>,
    IDivisionOperators<Length, decimal, Length>,
    IComparisonOperators<Length, Length, bool>,
    IMinMaxValue<Length>,
    IAdditiveIdentity<Length, Length>,
    IAmount<Length>
{
    /// <summary>Canonical scale (metres, 4 decimal places → 0.1&#160;mm resolution).</summary>
    public const int Scale = 4;

    // metres per unit (exact by definition for imperial units).
    private const decimal MPerMillimetre = 0.001m;
    private const decimal MPerCentimetre = 0.01m;
    private const decimal MPerMetre = 1m;
    private const decimal MPerKilometre = 1000m;
    private const decimal MPerInch = 0.0254m;
    private const decimal MPerFoot = 0.3048m;
    private const decimal MPerYard = 0.9144m;
    private const decimal MPerMile = 1609.344m;

    private readonly decimal _metres;
    private Length(decimal metres, bool _) => _metres = metres; // raw, no rounding (for bounds)
    private Length(decimal metres) => _metres = Math.Round(metres, Scale, MidpointRounding.AwayFromZero);

    /// <summary>The canonical value in metres.</summary>
    public decimal Metres => _metres;

    public bool IsZero => _metres == 0m;
    public bool IsPositive => _metres > 0m;
    public bool IsNegative => _metres < 0m;
    public Length Abs() => new(Math.Abs(_metres));

    // ── Unit-typed accessors ─────────────────────────────────────────────────
    public decimal InMillimetres => _metres / MPerMillimetre;
    public decimal InCentimetres => _metres / MPerCentimetre;
    public decimal InKilometres => _metres / MPerKilometre;
    public decimal InInches => _metres / MPerInch;
    public decimal InFeet => _metres / MPerFoot;
    public decimal InYards => _metres / MPerYard;
    public decimal InMiles => _metres / MPerMile;

    private static decimal MetresPer(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetre => MPerMillimetre,
        LengthUnit.Centimetre => MPerCentimetre,
        LengthUnit.Metre => MPerMetre,
        LengthUnit.Kilometre => MPerKilometre,
        LengthUnit.Inch => MPerInch,
        LengthUnit.Foot => MPerFoot,
        LengthUnit.Yard => MPerYard,
        LengthUnit.Mile => MPerMile,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    /// <summary>The value expressed in <paramref name="unit"/>.</summary>
    public decimal To(LengthUnit unit) => _metres / MetresPer(unit);

    // ── Factories ────────────────────────────────────────────────────────────
    public static Length FromMetres(decimal value) => new(value);
    public static Length Millimetres(decimal value) => new(value * MPerMillimetre);
    public static Length Centimetres(decimal value) => new(value * MPerCentimetre);
    public static Length Kilometres(decimal value) => new(value * MPerKilometre);
    public static Length Inches(decimal value) => new(value * MPerInch);
    public static Length Feet(decimal value) => new(value * MPerFoot);
    public static Length Yards(decimal value) => new(value * MPerYard);
    public static Length Miles(decimal value) => new(value * MPerMile);

    /// <summary>Creates a length from a value in an arbitrary <paramref name="unit"/>.</summary>
    public static Length From(decimal value, LengthUnit unit) => new(value * MetresPer(unit));

    // ── Well-known values ────────────────────────────────────────────────────
    public static readonly Length Zero = default;
    public static Length MinValue => new(decimal.MinValue, false);
    public static Length MaxValue => new(decimal.MaxValue, false);
    static Length IMinMaxValue<Length>.MinValue => MinValue;
    static Length IMinMaxValue<Length>.MaxValue => MaxValue;
    static Length IAdditiveIdentity<Length, Length>.AdditiveIdentity => Zero;

    /// <summary>Raw type <c>"length"</c> (decimal storage, metres).</summary>
    public static RawType RawType { get; } = RawType.Define<Length>("length", ValueKind.Decimal);

    public RawValue ToRaw() => RawValue.Typed(RawType, _metres);

    /// <summary>Reads Length (plain numbers are metres).</summary>
    public static bool TryFromRaw(in RawValue raw, out Length value)
    {
        bool ok = raw.TryRead(RawType, out decimal d);
        value = ok ? FromMetres(d) : default;
        return ok;
    }

    // ── Operators ────────────────────────────────────────────────────────────
    public static Length operator +(Length a, Length b) => new(a._metres + b._metres);
    public static Length operator -(Length a, Length b) => new(a._metres - b._metres);
    public static Length operator -(Length a) => new(-a._metres);
    public static Length operator *(Length a, decimal factor) => new(a._metres * factor);
    public static Length operator *(decimal factor, Length a) => new(a._metres * factor);
    public static Length operator /(Length a, decimal divisor) => new(a._metres / divisor);
    /// <summary>The dimensionless ratio between two lengths.</summary>
    public static decimal operator /(Length a, Length b) => a._metres / b._metres;

    public static bool operator <(Length a, Length b) => a._metres < b._metres;
    public static bool operator >(Length a, Length b) => a._metres > b._metres;
    public static bool operator <=(Length a, Length b) => a._metres <= b._metres;
    public static bool operator >=(Length a, Length b) => a._metres >= b._metres;

    /// <summary>Implicit from a metre <see cref="decimal"/> (canonical unit).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Length(decimal metres) => new(metres);
    public static explicit operator decimal(Length l) => l._metres;

    public int CompareTo(Length other) => _metres.CompareTo(other._metres);
    public int CompareTo(object? obj) => obj is Length l ? CompareTo(l) : 1;

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Default display: canonical metres, e.g. "1.5 m".</summary>
    public override string ToString() => ToString(null, CultureInfo.CurrentCulture);

    /// <summary>
    /// Format is a unit token — <c>mm</c>, <c>cm</c>, <c>m</c>, <c>km</c>, <c>in</c>,
    /// <c>ft</c>, <c>yd</c>, <c>mi</c> — rendering the value in that unit with its suffix
    /// (e.g. <c>"cm"</c> → "150 cm"). Null/empty renders canonical metres.
    /// </summary>
    public string ToString(string? format, IFormatProvider? provider)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (string.IsNullOrEmpty(format))
            return _metres.ToString("0.####", provider) + " m";

        var unit = ParseUnit(format);
        return To(unit).ToString("0.####", provider) + " " + Suffix(unit);
    }

    /// <summary>Invariant metres, trimmed — the wire form (e.g. "1.5").</summary>
    public string ToXmlString() => _metres.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Suffix(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetre => "mm",
        LengthUnit.Centimetre => "cm",
        LengthUnit.Metre => "m",
        LengthUnit.Kilometre => "km",
        LengthUnit.Inch => "in",
        LengthUnit.Foot => "ft",
        LengthUnit.Yard => "yd",
        LengthUnit.Mile => "mi",
        _ => "",
    };

    private static LengthUnit ParseUnit(ReadOnlySpan<char> token) => token.Trim() switch
    {
        "mm" => LengthUnit.Millimetre,
        "cm" => LengthUnit.Centimetre,
        "m" => LengthUnit.Metre,
        "km" => LengthUnit.Kilometre,
        "in" => LengthUnit.Inch,
        "ft" => LengthUnit.Foot,
        "yd" => LengthUnit.Yard,
        "mi" => LengthUnit.Mile,
        _ => throw new FormatException($"Unknown length unit \"{token.ToString()}\"."),
    };

    // ── Parsing ──────────────────────────────────────────────────────────────
    // Accepts a bare number (metres) or "<number> <unit>", e.g. "1.5 m", "12in".

    public static bool IsValid([NotNullWhen(true)] string? s) => TryParse(s, out _);

    public static Length Parse(string s) => Parse(s, null);
    public static Length Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var l) ? l : throw new FormatException($"\"{s}\" is not a valid length.");

    public static bool TryParse([NotNullWhen(true)] string? s, out Length result) => TryParse(s, null, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Length result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var span = s.AsSpan().Trim();
        // Split the trailing unit letters (if any) from the leading number.
        int split = span.Length;
        while (split > 0 && (char.IsLetter(span[split - 1]) || span[split - 1] == ' ')) split--;

        var numberPart = span[..split].Trim();
        var unitPart = span[split..].Trim();

        if (!decimal.TryParse(numberPart, NumberStyles.Number, provider ?? CultureInfo.CurrentCulture, out var value) &&
            !decimal.TryParse(numberPart, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return false;

        var unit = LengthUnit.Metre;
        if (!unitPart.IsEmpty)
        {
            try { unit = ParseUnit(unitPart); }
            catch (FormatException) { return false; }
        }

        result = From(value, unit);
        return true;
    }

    // record struct supplies value Equals/GetHashCode over _metres.
}

/// <summary>XAML/<see cref="TypeConverter"/> support for <see cref="Length"/> ↔ string.</summary>
public sealed class LengthTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof(decimal) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s => Length.Parse(s, culture),
            decimal d => Length.FromMetres(d),
            _ => base.ConvertFrom(context, culture, value),
        };

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || destinationType == typeof(decimal) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is Length l)
        {
            if (destinationType == typeof(string)) return l.ToString("m", culture);
            if (destinationType == typeof(decimal)) return l.Metres;
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}

/// <summary>
/// Serializes <see cref="Length"/> as a JSON number in canonical metres. Reads a number
/// (metres) or a string (tolerant of unit suffixes, e.g. "150 cm").
/// </summary>
public sealed class LengthJsonConverter : JsonConverter<Length>
{
    public override Length Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => Length.FromMetres(reader.GetDecimal()),
            JsonTokenType.String when Length.TryParse(reader.GetString(), out var l) => l,
            _ => throw new JsonException($"Cannot read Length from {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, Length value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Metres);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Length value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToXmlString());

    public override Length ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Length.Parse(reader.GetString() ?? "0");
}
