using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>An ISO 4217 alphabetic currency code. The open set permits future codes without an enum release.</summary>
[TypeConverter(typeof(CurrencyTypeConverter))]
[JsonConverter(typeof(CurrencyJsonConverter))]
public readonly record struct Currency : IParsable<Currency>, ISpanParsable<Currency>
{
    private readonly string? _code;

    private Currency(string code) => _code = code;

    /// <summary>The code; the default value represents BRL for existing fiscal callers.</summary>
    public string Code => _code ?? "BRL";

    public static Currency BRL => default;
    public static Currency USD => new("USD");
    public static Currency EUR => new("EUR");

    /// <summary>ISO 4217 minor-unit digits for common exceptions; two for other codes.</summary>
    public int MinorUnitDigits => Code switch
    {
        "CLF" or "UYW" => 4,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF" or "UGX" or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        _ => 2,
    };

    public string Symbol => Code switch
    {
        "BRL" => "R$", "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "¥", _ => Code,
    };

    public static Currency Parse(string s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var currency) ? currency : throw new FormatException("A currency code must contain three ASCII letters.");

    public static Currency Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var currency) ? currency : throw new FormatException("A currency code must contain three ASCII letters.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Currency result) =>
        TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Currency result)
    {
        s = s.Trim();
        if (s.Length != 3 || !char.IsAsciiLetter(s[0]) || !char.IsAsciiLetter(s[1]) || !char.IsAsciiLetter(s[2]))
        {
            result = default;
            return false;
        }

        Span<char> code = stackalloc char[3];
        for (var i = 0; i < 3; i++) code[i] = char.ToUpperInvariant(s[i]);
        var normalized = new string(code);
        result = normalized == "BRL" ? default : new Currency(normalized);
        return true;
    }

    public override string ToString() => Code;
}

public sealed class CurrencyTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? Currency.Parse(s, culture) : base.ConvertFrom(context, culture, value);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is Currency currency
            ? currency.Code : base.ConvertTo(context, culture, value, destinationType);
}

public sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Currency.TryParse(reader.GetString(), null, out var currency)
            ? currency : throw new JsonException("Invalid currency code.");

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) => writer.WriteStringValue(value.Code);

    public override Currency ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Currency.Parse(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Code);
}
