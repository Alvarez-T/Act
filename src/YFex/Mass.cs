using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>Units <see cref="Mass"/> can be expressed in. Canonical storage is the gram.</summary>
public enum MassUnit
{
    /// <summary>Milligram (mg) — 0.001 g.</summary>
    Milligram,
    /// <summary>Gram (g) — the canonical unit.</summary>
    Gram,
    /// <summary>Kilogram (kg) — 1000 g. The DF-e mass unit.</summary>
    Kilogram,
    /// <summary>Metric tonne (t) — 1 000 000 g.</summary>
    Tonne,
    /// <summary>Ounce (oz), avoirdupois — 28.349523125 g.</summary>
    Ounce,
    /// <summary>Pound (lb), avoirdupois — 453.59237 g.</summary>
    Pound,
    /// <summary>Stone (st) — 6350.29318 g (14 lb).</summary>
    Stone,
}

/// <summary>
/// A physical mass stored canonically in grams (up to 4-decimal / 0.1&#160;mg scale),
/// unit-agnostic in storage and unit-aware only when you read or format it. Mirrors the
/// value-object shape of <see cref="Quantity"/> / <see cref="Money"/> — implicit from a
/// gram <see cref="decimal"/>, full numeric/parse/format interfaces, and exact metric ↔
/// imperial conversions (pound = 453.59237 g, exact by definition).
///
/// <code>
/// var m = Mass.Kilograms(3.5m);   // 3500 g
/// m.InPounds;                     // 7.7162 (lb)
/// (m * 2m).ToString("kg");        // "7 kg"
/// m.To(MassUnit.Gram);            // 3500.0000
/// m.ToXmlString();                // "3.5" (kg, invariant — the DF-e wire form)
/// </code>
/// </summary>
[TypeConverter(typeof(MassTypeConverter))]
[JsonConverter(typeof(MassJsonConverter))]
public readonly record struct Mass :
    IComparable<Mass>,
    IComparable,
    IFormattable,
    IParsable<Mass>,
    IAdditionOperators<Mass, Mass, Mass>,
    ISubtractionOperators<Mass, Mass, Mass>,
    IMultiplyOperators<Mass, decimal, Mass>,
    IDivisionOperators<Mass, decimal, Mass>,
    IComparisonOperators<Mass, Mass, bool>,
    IMinMaxValue<Mass>,
    IAdditiveIdentity<Mass, Mass>,
    IAmount<Mass>
{
    /// <summary>Canonical scale (grams, 4 decimal places → 0.1&#160;mg resolution).</summary>
    public const int Scale = 4;

    // grams per unit (exact by definition for avoirdupois imperial units).
    private const decimal GPerMilligram = 0.001m;
    private const decimal GPerGram = 1m;
    private const decimal GPerKilogram = 1000m;
    private const decimal GPerTonne = 1_000_000m;
    private const decimal GPerOunce = 28.349523125m;
    private const decimal GPerPound = 453.59237m;
    private const decimal GPerStone = 6350.29318m;

    private readonly decimal _grams;
    private Mass(decimal grams, bool _) => _grams = grams; // raw, no rounding (for bounds)
    private Mass(decimal grams) => _grams = Math.Round(grams, Scale, MidpointRounding.AwayFromZero);

    /// <summary>The canonical value in grams.</summary>
    public decimal Grams => _grams;

    public bool IsZero => _grams == 0m;
    public bool IsPositive => _grams > 0m;
    public bool IsNegative => _grams < 0m;
    public Mass Abs() => new(Math.Abs(_grams));

    // ── Unit-typed accessors ─────────────────────────────────────────────────
    public decimal InMilligrams => _grams / GPerMilligram;
    public decimal InKilograms => _grams / GPerKilogram;
    public decimal InTonnes => _grams / GPerTonne;
    public decimal InOunces => _grams / GPerOunce;
    public decimal InPounds => _grams / GPerPound;
    public decimal InStones => _grams / GPerStone;

    private static decimal GramsPer(MassUnit unit) => unit switch
    {
        MassUnit.Milligram => GPerMilligram,
        MassUnit.Gram => GPerGram,
        MassUnit.Kilogram => GPerKilogram,
        MassUnit.Tonne => GPerTonne,
        MassUnit.Ounce => GPerOunce,
        MassUnit.Pound => GPerPound,
        MassUnit.Stone => GPerStone,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    /// <summary>The value expressed in <paramref name="unit"/>.</summary>
    public decimal To(MassUnit unit) => _grams / GramsPer(unit);

    // ── Factories ────────────────────────────────────────────────────────────
    public static Mass FromGrams(decimal value) => new(value);
    public static Mass Milligrams(decimal value) => new(value * GPerMilligram);
    public static Mass Kilograms(decimal value) => new(value * GPerKilogram);
    public static Mass Tonnes(decimal value) => new(value * GPerTonne);
    public static Mass Ounces(decimal value) => new(value * GPerOunce);
    public static Mass Pounds(decimal value) => new(value * GPerPound);
    public static Mass Stones(decimal value) => new(value * GPerStone);

    /// <summary>Creates a mass from a value in an arbitrary <paramref name="unit"/>.</summary>
    public static Mass From(decimal value, MassUnit unit) => new(value * GramsPer(unit));

    // ── Well-known values ────────────────────────────────────────────────────
    public static readonly Mass Zero = default;
    public static Mass MinValue => new(decimal.MinValue, false);
    public static Mass MaxValue => new(decimal.MaxValue, false);
    static Mass IMinMaxValue<Mass>.MinValue => MinValue;
    static Mass IMinMaxValue<Mass>.MaxValue => MaxValue;
    static Mass IAdditiveIdentity<Mass, Mass>.AdditiveIdentity => Zero;

    /// <summary>Raw type <c>"mass"</c> (decimal storage, grams).</summary>
    public static RawType RawType { get; } = RawType.Define<Mass>("mass", ValueKind.Decimal);

    public RawValue ToRaw() => RawValue.Typed(RawType, _grams);

    /// <summary>Reads Mass (plain numbers are grams).</summary>
    public static bool TryFromRaw(in RawValue raw, out Mass value)
    {
        bool ok = raw.TryRead(RawType, out decimal d);
        value = ok ? FromGrams(d) : default;
        return ok;
    }

    // ── Operators ────────────────────────────────────────────────────────────
    public static Mass operator +(Mass a, Mass b) => new(a._grams + b._grams);
    public static Mass operator -(Mass a, Mass b) => new(a._grams - b._grams);
    public static Mass operator -(Mass a) => new(-a._grams);
    public static Mass operator *(Mass a, decimal factor) => new(a._grams * factor);
    public static Mass operator *(decimal factor, Mass a) => new(a._grams * factor);
    public static Mass operator /(Mass a, decimal divisor) => new(a._grams / divisor);
    /// <summary>The dimensionless ratio between two masses.</summary>
    public static decimal operator /(Mass a, Mass b) => a._grams / b._grams;

    public static bool operator <(Mass a, Mass b) => a._grams < b._grams;
    public static bool operator >(Mass a, Mass b) => a._grams > b._grams;
    public static bool operator <=(Mass a, Mass b) => a._grams <= b._grams;
    public static bool operator >=(Mass a, Mass b) => a._grams >= b._grams;

    /// <summary>Implicit from a gram <see cref="decimal"/> (canonical unit).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Mass(decimal grams) => new(grams);
    public static explicit operator decimal(Mass m) => m._grams;

    public int CompareTo(Mass other) => _grams.CompareTo(other._grams);
    public int CompareTo(object? obj) => obj is Mass m ? CompareTo(m) : 1;

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Default display: canonical grams, e.g. "3500 g".</summary>
    public override string ToString() => ToString(null, CultureInfo.CurrentCulture);

    /// <summary>
    /// Format is a unit token — <c>mg</c>, <c>g</c>, <c>kg</c>, <c>t</c>, <c>oz</c>,
    /// <c>lb</c>, <c>st</c> — rendering the value in that unit with its suffix
    /// (e.g. <c>"kg"</c> → "3.5 kg"). Null/empty renders canonical grams.
    /// </summary>
    public string ToString(string? format, IFormatProvider? provider)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (string.IsNullOrEmpty(format))
            return _grams.ToString("0.####", provider) + " g";

        var unit = ParseUnit(format);
        return To(unit).ToString("0.####", provider) + " " + Suffix(unit);
    }

    /// <summary>Invariant kilograms, trimmed — the DF-e XML wire form (e.g. "3.5").</summary>
    public string ToXmlString() => InKilograms.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Suffix(MassUnit unit) => unit switch
    {
        MassUnit.Milligram => "mg",
        MassUnit.Gram => "g",
        MassUnit.Kilogram => "kg",
        MassUnit.Tonne => "t",
        MassUnit.Ounce => "oz",
        MassUnit.Pound => "lb",
        MassUnit.Stone => "st",
        _ => "",
    };

    private static MassUnit ParseUnit(ReadOnlySpan<char> token) => token.Trim() switch
    {
        "mg" => MassUnit.Milligram,
        "g" => MassUnit.Gram,
        "kg" => MassUnit.Kilogram,
        "t" => MassUnit.Tonne,
        "oz" => MassUnit.Ounce,
        "lb" => MassUnit.Pound,
        "st" => MassUnit.Stone,
        _ => throw new FormatException($"Unknown mass unit \"{token.ToString()}\"."),
    };

    // ── Parsing ──────────────────────────────────────────────────────────────
    // Accepts a bare number (grams) or "<number> <unit>", e.g. "3.5 kg", "16oz".

    public static bool IsValid([NotNullWhen(true)] string? s) => TryParse(s, out _);

    public static Mass Parse(string s) => Parse(s, null);
    public static Mass Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var m) ? m : throw new FormatException($"\"{s}\" is not a valid mass.");

    public static bool TryParse([NotNullWhen(true)] string? s, out Mass result) => TryParse(s, null, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Mass result)
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

        var unit = MassUnit.Gram;
        if (!unitPart.IsEmpty)
        {
            try { unit = ParseUnit(unitPart); }
            catch (FormatException) { return false; }
        }

        result = From(value, unit);
        return true;
    }

    // record struct supplies value Equals/GetHashCode over _grams.
}

/// <summary>XAML/<see cref="TypeConverter"/> support for <see cref="Mass"/> ↔ string.</summary>
public sealed class MassTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof(decimal) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s => Mass.Parse(s, culture),
            decimal d => Mass.FromGrams(d),
            _ => base.ConvertFrom(context, culture, value),
        };

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || destinationType == typeof(decimal) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is Mass m)
        {
            if (destinationType == typeof(string)) return m.ToString("kg", culture);
            if (destinationType == typeof(decimal)) return m.Grams;
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}

/// <summary>
/// Serializes <see cref="Mass"/> as a JSON number in canonical grams. Reads a number
/// (grams) or a string (tolerant of unit suffixes, e.g. "3.5 kg").
/// </summary>
public sealed class MassJsonConverter : JsonConverter<Mass>
{
    public override Mass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => Mass.FromGrams(reader.GetDecimal()),
            JsonTokenType.String when Mass.TryParse(reader.GetString(), out var m) => m,
            _ => throw new JsonException($"Cannot read Mass from {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, Mass value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Grams);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Mass value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToXmlString());

    public override Mass ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Mass.Parse(reader.GetString() ?? "0");
}
