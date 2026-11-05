using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>
/// A monetary amount with ISO currency identity and currency-specific minor-unit scale, with
/// semantics — culture-aware formatting (default pt-BR <c>R$</c>), invariant wire
/// output, allocation/splitting without losing minor units, and modern
/// numeric/parse/format interfaces. The default currency is BRL for fiscal callers.
///
/// <code>
/// Money m = 1234.5m;            // implicit from decimal
/// m.ToString();                 // "R$ 1.234,50"
/// m.ToXmlString();              // "1234.50"  (invariant, F2)
/// (m * 0.18m);                  // Money 222.21
/// m.Allocate(3);                // [411.50, 411.50, 411.50] (remainder spread)
/// </code>
/// </summary>
[TypeConverter(typeof(MoneyTypeConverter))]
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money :
    IComparable<Money>,
    IComparable,
    IFormattable,
    ISpanFormattable,
    IParsable<Money>,
    ISpanParsable<Money>,
    IAdditionOperators<Money, Money, Money>,
    ISubtractionOperators<Money, Money, Money>,
    IMultiplyOperators<Money, decimal, Money>,
    IDivisionOperators<Money, decimal, Money>,
    IUnaryNegationOperators<Money, Money>,
    IComparisonOperators<Money, Money, bool>,
    IMinMaxValue<Money>,
    IAdditiveIdentity<Money, Money>,
    IAmount<Money>,
    global::YFex.Diagnostics.ILogValue<Money>
{
    /// <summary>Logs as its raw value: no boxing and no formatting on the caller (YFex.Diagnostics).</summary>
    static void global::YFex.Diagnostics.ILogValue<Money>.Write(in Money value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
        => writer.WriteRawValue(field, in value);

    /// <summary>Default BRL monetary scale (2 decimal places).</summary>
    public const int Scale = 2;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly decimal _amount;
    private readonly Currency _currency;
    private Money(decimal amount, bool _) { _amount = amount; _currency = default; } // raw, no rounding (for bounds)

    public Money(decimal amount) : this(amount, Currency.BRL) { }

    public Money(decimal amount, Currency currency)
    {
        _currency = currency;
        _amount = Math.Round(amount, currency.MinorUnitDigits, MidpointRounding.AwayFromZero);
    }

    /// <summary>The rounded decimal amount.</summary>
    public decimal Amount => _amount;
    public Currency Currency => _currency;
    /// <summary>The amount in hundredths for two-decimal currencies; use <see cref="MinorUnits"/> otherwise.</summary>
    public long Cents => Currency.MinorUnitDigits == 2
        ? (long)Math.Round(_amount * 100m, MidpointRounding.AwayFromZero)
        : throw new InvalidOperationException("Cents is available only for two-decimal currencies; use MinorUnits.");

    /// <summary>The amount expressed in the currency's smallest ordinary unit.</summary>
    public long MinorUnits => (long)(_amount * Pow10(Currency.MinorUnitDigits));

    private static decimal Pow10(int digits) => digits switch { 0 => 1m, 3 => 1000m, 4 => 10000m, _ => 100m };

    public bool IsZero => _amount == 0m;
    public bool IsPositive => _amount > 0m;
    public bool IsNegative => _amount < 0m;
    public Money Abs() => new(Math.Abs(_amount), Currency);

    /// <summary>Labels the same numeric amount in another currency; no exchange conversion is performed.</summary>
    public Money WithCurrency(Currency currency) => new(_amount, currency);

    // ── Well-known values ────────────────────────────────────────────────────
    public static readonly Money Zero = default;
    public static Money MinValue => new(decimal.MinValue, false);
    public static Money MaxValue => new(decimal.MaxValue, false);
    static Money IMinMaxValue<Money>.MinValue => MinValue;
    static Money IMinMaxValue<Money>.MaxValue => MaxValue;
    static Money IAdditiveIdentity<Money, Money>.AdditiveIdentity => Zero;

    /// <summary>Raw type <c>"money"</c> (decimal storage) — rules, tables, bounds, wire.</summary>
    public static RawType RawType { get; } = RawType.Define<Money>("money", ValueKind.Decimal, RawType.SpanFormatter<Money>());

    public RawValue ToRaw() => Currency == Currency.BRL
        ? RawValue.Typed(RawType, _amount)
        : throw new InvalidOperationException("The legacy money raw type represents BRL only.");

    /// <summary>Reads Money, or a plain number as an amount.</summary>
    public static bool TryFromRaw(in RawValue raw, out Money value)
    {
        bool ok = raw.TryRead(RawType, out decimal d);
        value = ok ? new Money(d) : default;
        return ok;
    }

    /// <summary>Creates a money value from a whole number of cents.</summary>
    public static Money FromCents(long cents) => new(cents / 100m);

    public static Money FromMinorUnits(long minorUnits, Currency currency) =>
        new(minorUnits / Pow10(currency.MinorUnitDigits), currency);

    // ── Money operations ─────────────────────────────────────────────────────

    /// <summary>Applies a percentage (e.g. a tax rate) and returns the resulting amount.</summary>
    public Money Percent(Percentual percentual) => new(percentual.ApplyTo(_amount), Currency);

    /// <summary>
    /// Splits this amount into <paramref name="parts"/> as evenly as possible,
    /// distributing the leftover cents one-by-one so the parts sum exactly to the whole.
    /// </summary>
    public Money[] Allocate(int parts)
    {
        if (parts <= 0) throw new ArgumentOutOfRangeException(nameof(parts));
        long total = MinorUnits;
        long each = total / parts;
        long rem = total - each * parts;
        var result = new Money[parts];
        for (int i = 0; i < parts; i++)
            result[i] = FromMinorUnits(each + (i < Math.Abs(rem) ? Math.Sign(rem) : 0), Currency);
        return result;
    }

    /// <summary>Allocates by integer weights (e.g. split a freight cost across items).</summary>
    public Money[] Allocate(ReadOnlySpan<int> weights)
    {
        long totalWeight = 0;
        foreach (var w in weights) totalWeight += w;
        if (totalWeight == 0) throw new ArgumentException("Weights must not sum to zero.", nameof(weights));

        long total = MinorUnits, allocated = 0;
        var result = new Money[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            long share = total * weights[i] / totalWeight;
            result[i] = FromMinorUnits(share, Currency);
            allocated += share;
        }
        // Drop the rounding remainder onto the first part.
        if (weights.Length > 0) result[0] = FromMinorUnits(result[0].MinorUnits + (total - allocated), Currency);
        return result;
    }

    // ── Operators ────────────────────────────────────────────────────────────
    public static Money operator +(Money a, Money b) => new(a._amount + b._amount, SameCurrency(a, b));
    public static Money operator -(Money a, Money b) => new(a._amount - b._amount, SameCurrency(a, b));
    public static Money operator -(Money a) => new(-a._amount, a.Currency);
    public static Money operator *(Money a, decimal factor) => new(a._amount * factor, a.Currency);
    public static Money operator *(decimal factor, Money a) => new(a._amount * factor, a.Currency);
    public static Money operator /(Money a, decimal divisor) => new(a._amount / divisor, a.Currency);

    private static Currency SameCurrency(Money a, Money b) => a.Currency == b.Currency
        ? a.Currency : throw new InvalidOperationException("Convert currencies explicitly before arithmetic.");

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;
    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;
    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Money(decimal amount) => new(amount);
    public static explicit operator decimal(Money m) => m._amount;

    public int CompareTo(Money other) => Currency == other.Currency
        ? _amount.CompareTo(other._amount) : throw new InvalidOperationException("Cannot compare different currencies.");
    public int CompareTo(object? obj) => obj is Money m ? CompareTo(m) : 1;

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Default display: pt-BR currency, e.g. "R$ 1.234,50".</summary>
    public override string ToString() => ToString("C", PtBr);

    /// <summary>
    /// Format specifiers: <c>C</c>/<c>G</c> currency (default pt-BR), <c>N</c>/<c>F</c>
    /// number, <c>X</c> invariant wire form (F2). Provider overrides the culture.
    /// </summary>
    public string ToString(string? format, IFormatProvider? provider)
    {
        provider ??= PtBr;
        var numberFormat = (NumberFormatInfo)NumberFormatInfo.GetInstance(provider).Clone();
        numberFormat.CurrencySymbol = Currency.Symbol;
        numberFormat.CurrencyDecimalDigits = Currency.MinorUnitDigits;
        var digits = Currency.MinorUnitDigits.ToString(CultureInfo.InvariantCulture);
        return (format ?? "C") switch
        {
            "X" or "x" => _amount.ToString("F" + digits, CultureInfo.InvariantCulture),
            "N" or "n" or "F" or "f" => _amount.ToString("N" + digits, numberFormat),
            _ => _amount.ToString("C" + digits, numberFormat),
        };
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var s = ToString(format.IsEmpty ? null : new string(format), provider);
        if (s.AsSpan().TryCopyTo(destination)) { charsWritten = s.Length; return true; }
        charsWritten = 0; return false;
    }

    /// <summary>Invariant, fixed 2 decimals with a dot — the DF-e XML wire form.</summary>
    public string ToXmlString() => _amount.ToString("F" + Currency.MinorUnitDigits, CultureInfo.InvariantCulture);

    // ── Parsing ──────────────────────────────────────────────────────────────

    public static bool IsValid([NotNullWhen(true)] string? s) => TryParse(s, out _);

    public static Money Parse(string s) => Parse(s.AsSpan(), null);
    public static Money Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), provider);
    public static Money Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out var m) ? m : throw new FormatException($"\"{s}\" is not a valid monetary value.");

    public static bool TryParse([NotNullWhen(true)] string? s, out Money result) => TryParse(s.AsSpan(), null, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Money result) => TryParse(s.AsSpan(), provider, out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Money result) =>
        TryParse(s, Currency.BRL, provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, Currency currency, IFormatProvider? provider, out Money result)
    {
        const NumberStyles styles = NumberStyles.Currency | NumberStyles.AllowLeadingSign;
        var numberFormat = (NumberFormatInfo)NumberFormatInfo.GetInstance(provider ?? PtBr).Clone();
        numberFormat.CurrencySymbol = currency.Symbol;
        if (decimal.TryParse(s, styles, numberFormat, out var d) ||
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
        {
            result = new Money(d, currency);
            return true;
        }
        result = default;
        return false;
    }

    // record struct supplies value Equals/GetHashCode over _amount.
}

/// <summary>XAML/<see cref="TypeConverter"/> support for <see cref="Money"/> ↔ string.</summary>
public sealed class MoneyTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || sourceType == typeof(decimal) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s => Money.Parse(s, culture),
            decimal d => new Money(d),
            _ => base.ConvertFrom(context, culture, value),
        };

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || destinationType == typeof(decimal) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is Money m)
        {
            if (destinationType == typeof(string)) return m.ToString("N", culture);
            if (destinationType == typeof(decimal)) return m.Amount;
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}

/// <summary>
/// BRL keeps the legacy JSON number; other currencies use an amount/currency object.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return new Money(reader.GetDecimal());
        if (reader.TokenType == JsonTokenType.String && Money.TryParse(reader.GetString(), out var parsed)) return parsed;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a monetary amount.");
        decimal? amount = null;
        Currency? currency = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var property = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            if (property == "amount") amount = reader.GetDecimal();
            else if (property == "currency") currency = Currency.Parse(reader.GetString() ?? string.Empty);
            else reader.Skip();
        }
        return amount is { } value && currency is { } code
            ? new Money(value, code) : throw new JsonException("Money requires amount and currency.");
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        if (value.Currency == Currency.BRL) { writer.WriteNumberValue(value.Amount); return; }
        writer.WriteStartObject();
        writer.WriteNumber("amount", value.Amount);
        writer.WriteString("currency", value.Currency.Code);
        writer.WriteEndObject();
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Currency == Currency.BRL
            ? value.ToXmlString() : value.Currency.Code + " " + value.ToXmlString());

    public override Money ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? string.Empty;
        var space = text.IndexOf(' ');
        if (space > 0 && Currency.TryParse(text.AsSpan(0, space), null, out var currency) &&
            Money.TryParse(text.AsSpan(space + 1), currency, CultureInfo.InvariantCulture, out var value)) return value;
        return Money.Parse(text);
    }
}
