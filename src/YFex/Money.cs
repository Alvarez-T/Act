using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>
/// A monetary amount: a <see cref="decimal"/> fixed to 2-decimal scale, with money
/// semantics — culture-aware formatting (default pt-BR <c>R$</c>), invariant wire
/// output, allocation/splitting without losing cents, and the full set of modern
/// numeric/parse/format interfaces. Currency-agnostic in storage (the fiscal domain
/// is BRL); formatting decides presentation.
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
    IAdditiveIdentity<Money, Money>
{
    /// <summary>Fixed monetary scale (2 decimal places).</summary>
    public const int Scale = 2;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly decimal _amount;
    private Money(decimal amount, bool _) => _amount = amount; // raw, no rounding (for bounds)

    public Money(decimal amount) => _amount = Math.Round(amount, Scale, MidpointRounding.AwayFromZero);

    /// <summary>The rounded decimal amount.</summary>
    public decimal Amount => _amount;
    /// <summary>The amount expressed in whole cents (centavos).</summary>
    public long Cents => (long)Math.Round(_amount * 100m, MidpointRounding.AwayFromZero);

    public bool IsZero => _amount == 0m;
    public bool IsPositive => _amount > 0m;
    public bool IsNegative => _amount < 0m;
    public Money Abs() => new(Math.Abs(_amount));

    // ── Well-known values ────────────────────────────────────────────────────
    public static readonly Money Zero = default;
    public static Money MinValue => new(decimal.MinValue, false);
    public static Money MaxValue => new(decimal.MaxValue, false);
    static Money IMinMaxValue<Money>.MinValue => MinValue;
    static Money IMinMaxValue<Money>.MaxValue => MaxValue;
    static Money IAdditiveIdentity<Money, Money>.AdditiveIdentity => Zero;

    /// <summary>Creates a money value from a whole number of cents.</summary>
    public static Money FromCents(long cents) => new(cents / 100m);

    // ── Money operations ─────────────────────────────────────────────────────

    /// <summary>Applies a percentage (e.g. a tax rate) and returns the resulting amount.</summary>
    public Money Percent(Percentual percentual) => new(percentual.ApplyTo(_amount));

    /// <summary>
    /// Splits this amount into <paramref name="parts"/> as evenly as possible,
    /// distributing the leftover cents one-by-one so the parts sum exactly to the whole.
    /// </summary>
    public Money[] Allocate(int parts)
    {
        if (parts <= 0) throw new ArgumentOutOfRangeException(nameof(parts));
        long total = Cents;
        long each = total / parts;
        long rem = total - each * parts;
        var result = new Money[parts];
        for (int i = 0; i < parts; i++)
            result[i] = FromCents(each + (i < Math.Abs(rem) ? Math.Sign(rem) : 0));
        return result;
    }

    /// <summary>Allocates by integer weights (e.g. split a freight cost across items).</summary>
    public Money[] Allocate(ReadOnlySpan<int> weights)
    {
        long totalWeight = 0;
        foreach (var w in weights) totalWeight += w;
        if (totalWeight == 0) throw new ArgumentException("Weights must not sum to zero.", nameof(weights));

        long total = Cents, allocated = 0;
        var result = new Money[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            long share = total * weights[i] / totalWeight;
            result[i] = FromCents(share);
            allocated += share;
        }
        // Drop the rounding remainder onto the first part.
        if (weights.Length > 0) result[0] = FromCents(result[0].Cents + (total - allocated));
        return result;
    }

    // ── Operators ────────────────────────────────────────────────────────────
    public static Money operator +(Money a, Money b) => new(a._amount + b._amount);
    public static Money operator -(Money a, Money b) => new(a._amount - b._amount);
    public static Money operator -(Money a) => new(-a._amount);
    public static Money operator *(Money a, decimal factor) => new(a._amount * factor);
    public static Money operator *(decimal factor, Money a) => new(a._amount * factor);
    public static Money operator /(Money a, decimal divisor) => new(a._amount / divisor);

    public static bool operator <(Money a, Money b) => a._amount < b._amount;
    public static bool operator >(Money a, Money b) => a._amount > b._amount;
    public static bool operator <=(Money a, Money b) => a._amount <= b._amount;
    public static bool operator >=(Money a, Money b) => a._amount >= b._amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Money(decimal amount) => new(amount);
    public static explicit operator decimal(Money m) => m._amount;

    public int CompareTo(Money other) => _amount.CompareTo(other._amount);
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
        return (format ?? "C") switch
        {
            "X" or "x" => _amount.ToString("F2", CultureInfo.InvariantCulture),
            "N" or "n" or "F" or "f" => _amount.ToString("N2", provider),
            _ => _amount.ToString("C2", provider),
        };
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var s = ToString(format.IsEmpty ? null : new string(format), provider);
        if (s.AsSpan().TryCopyTo(destination)) { charsWritten = s.Length; return true; }
        charsWritten = 0; return false;
    }

    /// <summary>Invariant, fixed 2 decimals with a dot — the DF-e XML wire form.</summary>
    public string ToXmlString() => _amount.ToString("F2", CultureInfo.InvariantCulture);

    // ── Parsing ──────────────────────────────────────────────────────────────

    public static bool IsValid([NotNullWhen(true)] string? s) => TryParse(s, out _);

    public static Money Parse(string s) => Parse(s.AsSpan(), null);
    public static Money Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), provider);
    public static Money Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out var m) ? m : throw new FormatException($"\"{s}\" is not a valid monetary value.");

    public static bool TryParse([NotNullWhen(true)] string? s, out Money result) => TryParse(s.AsSpan(), null, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Money result) => TryParse(s.AsSpan(), provider, out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Money result)
    {
        const NumberStyles styles = NumberStyles.Currency | NumberStyles.AllowLeadingSign;
        // Try the supplied/pt-BR culture first, then invariant (wire form).
        if (decimal.TryParse(s, styles, provider ?? PtBr, out var d) ||
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
        {
            result = new Money(d);
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
/// Serializes <see cref="Money"/> as a JSON number (invariant decimal). Reads a
/// number or a string (tolerant of culture-formatted input).
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => new Money(reader.GetDecimal()),
            JsonTokenType.String when Money.TryParse(reader.GetString(), out var m) => m,
            _ => throw new JsonException($"Cannot read Money from {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Amount);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToXmlString());

    public override Money ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Money.Parse(reader.GetString() ?? "0");
}
