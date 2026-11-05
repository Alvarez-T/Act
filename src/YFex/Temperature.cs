using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>Display and entry units for an absolute temperature.</summary>
public enum TemperatureUnit : byte { Celsius, Fahrenheit, Kelvin }

/// <summary>An absolute temperature stored in kelvins. Absolute temperatures intentionally have no addition operator.</summary>
[TypeConverter(typeof(TemperatureTypeConverter))]
[JsonConverter(typeof(TemperatureJsonConverter))]
public readonly record struct Temperature : IComparable<Temperature>, IFormattable, IParsable<Temperature>, ISpanParsable<Temperature>
{
    public decimal Kelvin { get; }

    private Temperature(decimal kelvin) => Kelvin = kelvin;

    public static Temperature From(decimal value, TemperatureUnit unit)
    {
        var kelvin = unit switch
        {
            TemperatureUnit.Celsius => value + 273.15m,
            TemperatureUnit.Fahrenheit => (value + 459.67m) * 5m / 9m,
            TemperatureUnit.Kelvin => value,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
        if (kelvin < 0m) throw new ArgumentOutOfRangeException(nameof(value), "Below absolute zero.");
        return new Temperature(Math.Round(kelvin, 6, MidpointRounding.AwayFromZero));
    }

    public decimal To(TemperatureUnit unit) => unit switch
    {
        TemperatureUnit.Celsius => Kelvin - 273.15m,
        TemperatureUnit.Fahrenheit => Kelvin * 9m / 5m - 459.67m,
        TemperatureUnit.Kelvin => Kelvin,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    public int CompareTo(Temperature other) => Kelvin.CompareTo(other.Kelvin);
    public override string ToString() => ToString("C", CultureInfo.CurrentCulture);

    public string ToString(string? format, IFormatProvider? provider)
    {
        var unit = UnitFor(format);
        var suffix = unit switch { TemperatureUnit.Fahrenheit => "°F", TemperatureUnit.Kelvin => " K", _ => "°C" };
        return To(unit).ToString("0.##", provider) + suffix;
    }

    private static TemperatureUnit UnitFor(string? format) => format?.ToUpperInvariant() switch
    {
        "F" or "°F" => TemperatureUnit.Fahrenheit,
        "K" => TemperatureUnit.Kelvin,
        _ => TemperatureUnit.Celsius,
    };

    public static Temperature Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), provider);
    public static Temperature Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException("Invalid temperature.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Temperature result) =>
        TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Temperature result)
    {
        s = s.Trim();
        var unit = TemperatureUnit.Celsius;
        if (s.EndsWith("°F", StringComparison.OrdinalIgnoreCase)) { unit = TemperatureUnit.Fahrenheit; s = s[..^2].TrimEnd(); }
        else if (s.EndsWith("°C", StringComparison.OrdinalIgnoreCase)) { s = s[..^2].TrimEnd(); }
        else if (s.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { unit = TemperatureUnit.Kelvin; s = s[..^1].TrimEnd(); }
        else if (s.EndsWith("F", StringComparison.OrdinalIgnoreCase)) { unit = TemperatureUnit.Fahrenheit; s = s[..^1].TrimEnd(); }
        else if (s.EndsWith("C", StringComparison.OrdinalIgnoreCase)) { s = s[..^1].TrimEnd(); }

        result = default;
        if (!decimal.TryParse(s, NumberStyles.Number, provider ?? CultureInfo.CurrentCulture, out var value)) return false;
        var kelvin = unit switch
        {
            TemperatureUnit.Celsius => value + 273.15m,
            TemperatureUnit.Fahrenheit => (value + 459.67m) * 5m / 9m,
            _ => value,
        };
        if (kelvin < 0m) return false;
        result = new Temperature(Math.Round(kelvin, 6, MidpointRounding.AwayFromZero));
        return true;
    }
}

public sealed class TemperatureTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? Temperature.Parse(s, culture) : base.ConvertFrom(context, culture, value);
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is Temperature temperature
            ? temperature.ToString("C", culture) : base.ConvertTo(context, culture, value, destinationType);
}

/// <summary>JSON stores canonical kelvins as a number.</summary>
public sealed class TemperatureJsonConverter : JsonConverter<Temperature>
{
    public override Temperature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? Temperature.From(reader.GetDecimal(), TemperatureUnit.Kelvin)
        : reader.TokenType == JsonTokenType.String ? Temperature.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
        : throw new JsonException("Invalid temperature.");
    public override void Write(Utf8JsonWriter writer, Temperature value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Kelvin);
}
