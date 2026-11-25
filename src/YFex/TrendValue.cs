using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>
/// A value compared with an earlier one ("sales vs last month"): <see cref="Previous"/> and <see cref="Current"/>, with
/// the <see cref="Absolute"/> difference, the <see cref="Relative"/> change (null when the previous value is 0, so
/// nobody divides by zero) and the <see cref="Direction"/>. Dashboards, KPI cards, report headers, alerts that compare
/// two windows (plan UI §24 ⚑UI200).
/// </summary>
/// <remarks>
/// <para>The relative change is taken against the size of the previous value: from −100 to −50 is +50 %, so a rise always
/// reads as a positive change. <see cref="WithFlatBelow"/> treats changes under a threshold as <see cref="TrendDirection.Flat"/>.</para>
/// <para>Formatting: <c>R</c> (default) the relative change with its sign ("+12.4 %", "−3.1 %"; the absolute change when
/// the relative one is undefined), <c>A</c> the absolute change ("+3", "−1,250.5"); a number after the letter gives the
/// decimal places (<c>R1</c> default, <c>A</c> as the value has them). The sign is the minus sign U+2212.</para>
/// <para>Not implemented, on purpose: <c>IComparable</c> (two trends have no natural order), parsing (a trend never
/// arrives as text), <c>TypeConverter</c> (the UI binds the relative change as a number).</para>
/// </remarks>
/// <example><code>
/// var sales = TrendValue.Of(current: 84_210m, previous: 74_920m);   // Up, Relative = 12.40 %
/// $"{sales}"                                                      // "+12.4 %"
/// sales.ToString("A0", culture)                                   // "+9,290"
/// if (TrendValue.TryOf(thisMonth, lastMonth, out var revenue)) …   // Money: same currency only
/// TrendValue.Of(1001m, 1000m).WithFlatBelow(Percentual.FromPercentage(0.5m)).Direction   // Flat
/// </code></example>
[JsonConverter(typeof(TrendValueJsonConverter))]
public readonly record struct TrendValue
    : IEquatable<TrendValue>,
      ISpanFormattable,
      IUtf8SpanFormattable,
      global::YFex.Diagnostics.ILogValue<TrendValue>
{
    private const char Minus = '−';
    private static readonly string[] LogNames = ["previous", "current"];

    private TrendValue(decimal previous, decimal current, TrendDirection direction)
    {
        Previous = previous;
        Current = current;
        Relative = previous == 0m ? null : Percentual.FromFraction((current - previous) / Math.Abs(previous));
        Direction = direction;
    }

    /// <summary>The earlier value (last month, the previous window).</summary>
    public decimal Previous { get; }

    /// <summary>The value now.</summary>
    public decimal Current { get; }

    /// <summary><see cref="Current"/> minus <see cref="Previous"/>.</summary>
    public decimal Absolute => Current - Previous;

    /// <summary>The change against the size of <see cref="Previous"/>; null when <see cref="Previous"/> is 0.</summary>
    public Percentual? Relative { get; }

    /// <summary>Up, down or flat (a change under <see cref="WithFlatBelow"/>'s threshold is flat).</summary>
    public TrendDirection Direction { get; }

    /// <summary>The trend from <paramref name="previous"/> to <paramref name="current"/>.</summary>
    public static TrendValue Of(decimal current, decimal previous) =>
        new(previous, current, current > previous ? TrendDirection.Up : current < previous ? TrendDirection.Down : TrendDirection.Flat);

    /// <summary>The trend between two amounts of the same currency; false (and <c>default</c>) when the currencies differ.</summary>
    public static bool TryOf(Money current, Money previous, out TrendValue result)
    {
        if (current.Currency != previous.Currency)
        {
            result = default;
            return false;
        }
        result = Of(current.Amount, previous.Amount);
        return true;
    }

    /// <summary>The trend between two quantities. A <see cref="Quantity"/> carries no unit, so the caller keeps both in
    /// the same one; always true (the same shape as the <see cref="Money"/> overload, for when quantities gain units).</summary>
    public static bool TryOf(Quantity current, Quantity previous, out TrendValue result)
    {
        result = Of(current.Value, previous.Value);
        return true;
    }

    /// <summary>A copy that reads as <see cref="TrendDirection.Flat"/> when the relative change is smaller than
    /// <paramref name="threshold"/> in size (with no relative change, only when nothing changed).</summary>
    public TrendValue WithFlatBelow(Percentual threshold)
    {
        var flat = Relative is { } relative
            ? Math.Abs(relative.ToPercentageValue()) < Math.Abs(threshold.ToPercentageValue())
            : Absolute == 0m;
        return flat ? new TrendValue(Previous, Current, TrendDirection.Flat) : this;
    }

    /// <summary>The same values read as flat (JSON round trip of a threshold-flattened trend).</summary>
    internal TrendValue AsFlat() => new(Previous, Current, TrendDirection.Flat);

    static void global::YFex.Diagnostics.ILogValue<TrendValue>.Write(in TrendValue value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
    {
        if (!writer.BeginObject(field, LogNames)) return;
        writer.Write(0, value.Previous);
        writer.Write(1, value.Current);
        writer.EndObject();
    }

    /// <summary>The relative change with its sign ("+12.4 %"), invariant culture.</summary>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Span<char> buffer = stackalloc char[64];
        return TryFormat(buffer, out int written, format, formatProvider) ? buffer[..written].ToString() : Current.ToString(formatProvider);
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        provider ??= CultureInfo.InvariantCulture;
        var absolute = format.Length > 0 && (format[0] is 'A' or 'a');
        int? places = format.Length > 1 && int.TryParse(format[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        var relative = !absolute && Relative is not null;
        var number = relative ? Relative!.Value.ToPercentageValue() : Absolute;
        var digits = relative ? places ?? 1 : places;
        var rounded = digits is { } d ? Math.Round(number, d, MidpointRounding.AwayFromZero) : number;

        if (rounded != 0m)
        {
            if (destination.IsEmpty) return false;
            destination[0] = rounded > 0 ? '+' : Minus;
            charsWritten = 1;
        }
        ReadOnlySpan<char> numberFormat = digits switch
        {
            null => "G",
            0 => "N0",
            1 => "N1",
            2 => "N2",
            3 => "N3",
            4 => "N4",
            5 => "N5",
            _ => "N6",
        };
        if (!Math.Abs(rounded).TryFormat(destination[charsWritten..], out int numberWritten, numberFormat, provider))
            return false;
        charsWritten += numberWritten;
        if (!relative) return true;
        if (destination.Length - charsWritten < 2) return false;
        destination[charsWritten++] = ' ';
        destination[charsWritten++] = '%';
        return true;
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<char> chars = stackalloc char[64];
        if (!TryFormat(chars, out int written, format, provider)) { bytesWritten = 0; return false; }
        return Encoding.UTF8.TryGetBytes(chars[..written], utf8Destination, out bytesWritten);
    }
}

/// <summary>JSON: <c>{"previous": 74920, "current": 84210}</c>, plus <c>"flat": true</c> when a threshold made it flat.</summary>
public sealed class TrendValueJsonConverter : JsonConverter<TrendValue>
{
    public override TrendValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A TrendValue is an object.");
        decimal previous = 0m, current = 0m;
        var flat = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name.");
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "previous": previous = reader.GetDecimal(); break;
                case "current": current = reader.GetDecimal(); break;
                case "flat": flat = reader.GetBoolean(); break;
                default: reader.Skip(); break;
            }
        }
        var value = TrendValue.Of(current, previous);
        return flat ? value.AsFlat() : value;
    }

    public override void Write(Utf8JsonWriter writer, TrendValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("previous", value.Previous);
        writer.WriteNumber("current", value.Current);
        if (value.Direction == TrendDirection.Flat && value.Absolute != 0m) writer.WriteBoolean("flat", true);
        writer.WriteEndObject();
    }
}
