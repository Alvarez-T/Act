using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex;

/// <summary>
/// A validity window: valid from <see cref="From"/> (inclusive) until <see cref="Until"/> (exclusive), or forever when
/// <see cref="Until"/> is null. One reusable type for every "valid from … until …" in the ecosystem: temporary
/// permission grants, approval tokens, permission snapshots, and later rule effective dates, consent validity, legal holds
/// and logging TTLs (authorization-consolidation ⚑Z13).
///
/// <code>
/// var shift = ValidityPeriod.For(now, TimeSpan.FromHours(2));
/// shift.Contains(now.AddHours(1));   // true
/// shift.IsExpired(now.AddHours(3));  // true
/// shift.Remaining(now);              // 02:00:00
/// shift.ToString();                  // "2026-10-02T10:00:00.0000000+00:00/2026-10-02T12:00:00.0000000+00:00"
/// ValidityPeriod.Starting(now).ToString();   // "2026-10-02T10:00:00.0000000+00:00/.." (open-ended, ISO 8601-2)
/// </code>
///
/// <para><b>Canonical forms.</b> Text = ISO 8601 interval <c>from/until</c>, <c>..</c> for an open end (JSON uses it as a
/// string). Binary = <see cref="BinarySize"/> bytes: UTC ticks of <see cref="From"/> then of <see cref="Until"/>
/// (0 = open end), little-endian — what signed payloads hash. Equality compares instants; the offset is not part of the
/// value (the binary and text forms are UTC).</para>
/// </summary>
[TypeConverter(typeof(ValidityPeriodTypeConverter))]
[JsonConverter(typeof(ValidityPeriodJsonConverter))]
public readonly struct ValidityPeriod :
    IEquatable<ValidityPeriod>,
    ISpanFormattable,
    ISpanParsable<ValidityPeriod>,
    global::YFex.Diagnostics.ILogValue<ValidityPeriod>
{
    /// <summary>Size of the canonical binary form (<see cref="TryWriteBytes"/>).</summary>
    public const int BinarySize = 16;

    private const string OpenEnd = "..";

    /// <summary>Logs as its canonical text (no allocation: formatted on the stack).</summary>
    static void global::YFex.Diagnostics.ILogValue<ValidityPeriod>.Write(in ValidityPeriod value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
    {
        Span<char> buffer = stackalloc char[72];
        if (value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture)) writer.Write(field, buffer[..written]);
        else writer.Write(field, value.ToString());
    }

    /// <summary>Creates a window; <paramref name="until"/> must be later than <paramref name="from"/> (null = never expires).</summary>
    public ValidityPeriod(DateTimeOffset from, DateTimeOffset? until = null)
    {
        if (until is { } end && end <= from)
            throw new ArgumentOutOfRangeException(nameof(until), until, "The end of a validity period must be later than its start.");
        From = from;
        Until = until;
    }

    /// <summary>First instant the period is valid (inclusive).</summary>
    public DateTimeOffset From { get; }

    /// <summary>First instant the period is no longer valid (exclusive); null = open-ended.</summary>
    public DateTimeOffset? Until { get; }

    /// <summary>True when the period has no end.</summary>
    public bool IsOpenEnded => Until is null;

    /// <summary>Length of the period; null when open-ended.</summary>
    public TimeSpan? Duration => Until is { } end ? end - From : null;

    /// <summary>A period of <paramref name="duration"/> starting at <paramref name="from"/>.</summary>
    public static ValidityPeriod For(DateTimeOffset from, TimeSpan duration) => new(from, from + duration);

    /// <summary>An open-ended period starting at <paramref name="from"/>.</summary>
    public static ValidityPeriod Starting(DateTimeOffset from) => new(from);

    /// <summary>True when <paramref name="now"/> lies inside the period (<c>From ≤ now &lt; Until</c>).</summary>
    public bool Contains(DateTimeOffset now) => now >= From && (Until is not { } end || now < end);

    /// <summary>True when the period has ended at <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => Until is { } end && now >= end;

    /// <summary>True when the period has not started yet at <paramref name="now"/>.</summary>
    public bool IsPending(DateTimeOffset now) => now < From;

    /// <summary>Time left at <paramref name="now"/> (zero once expired); null when open-ended.</summary>
    public TimeSpan? Remaining(DateTimeOffset now)
        => Until is { } end ? (end > now ? end - now : TimeSpan.Zero) : null;

    /// <summary>True when both periods share at least one instant.</summary>
    public bool Overlaps(in ValidityPeriod other)
        => (Until is not { } end || other.From < end) && (other.Until is not { } otherEnd || From < otherEnd);

    /// <summary>This period cut at <paramref name="latestEnd"/> when it would end later (or never).</summary>
    public ValidityPeriod CapEnd(DateTimeOffset latestEnd)
        => Until is { } end && end <= latestEnd ? this : new(From, latestEnd);

    // ── Binary ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the canonical <see cref="BinarySize"/>-byte form (UTC ticks of From, then of Until or 0).</summary>
    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < BinarySize) return false;
        BinaryPrimitives.WriteInt64LittleEndian(destination, From.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Until?.UtcTicks ?? 0L);
        return true;
    }

    /// <summary>Reads the canonical binary form written by <see cref="TryWriteBytes"/>.</summary>
    public static bool TryReadBytes(ReadOnlySpan<byte> source, out ValidityPeriod value)
    {
        value = default;
        if (source.Length < BinarySize) return false;
        long from = BinaryPrimitives.ReadInt64LittleEndian(source);
        long until = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        if (from < DateTime.MinValue.Ticks || from > DateTime.MaxValue.Ticks) return false;
        if (until != 0 && (until <= from || until > DateTime.MaxValue.Ticks)) return false;
        value = new ValidityPeriod(new DateTimeOffset(from, TimeSpan.Zero), until == 0 ? null : new DateTimeOffset(until, TimeSpan.Zero));
        return true;
    }

    // ── Text ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Span<char> buffer = stackalloc char[72];
        return TryFormat(buffer, out int written, format, formatProvider) ? new string(buffer[..written]) : string.Empty;
    }

    /// <summary>Formats as the ISO 8601 interval <c>from/until</c> (round-trip "O" instants, UTC; <c>..</c> = open end).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        if (!From.ToUniversalTime().TryFormat(destination, out int written, "O", CultureInfo.InvariantCulture)) return false;
        int position = written;
        if (position >= destination.Length) return false;
        destination[position++] = '/';
        if (Until is { } end)
        {
            if (!end.ToUniversalTime().TryFormat(destination[position..], out written, "O", CultureInfo.InvariantCulture)) return false;
            position += written;
        }
        else
        {
            if (!OpenEnd.AsSpan().TryCopyTo(destination[position..])) return false;
            position += OpenEnd.Length;
        }
        charsWritten = position;
        return true;
    }

    public static ValidityPeriod Parse(string s, IFormatProvider? provider = null)
        => TryParse(s, provider, out var value) ? value : throw new FormatException($"'{s}' is not a validity period (expected 'from/until' or 'from/..').");

    public static ValidityPeriod Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out var value) ? value : throw new FormatException($"'{s}' is not a validity period (expected 'from/until' or 'from/..').");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ValidityPeriod result)
        => TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ValidityPeriod result)
    {
        result = default;
        s = s.Trim();
        int slash = s.IndexOf('/');
        if (slash <= 0) return false;
        if (!DateTimeOffset.TryParse(s[..slash], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from)) return false;
        var rest = s[(slash + 1)..];
        if (rest.SequenceEqual(OpenEnd)) { result = new ValidityPeriod(from); return true; }
        if (!DateTimeOffset.TryParse(rest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) || until <= from) return false;
        result = new ValidityPeriod(from, until);
        return true;
    }

    // ── Equality ───────────────────────────────────────────────────────────────────────────────────────────────────

    public bool Equals(ValidityPeriod other) => From.Equals(other.From) && Nullable.Equals(Until, other.Until);
    public override bool Equals(object? obj) => obj is ValidityPeriod other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(From.UtcTicks, Until?.UtcTicks ?? 0L);
    public static bool operator ==(ValidityPeriod left, ValidityPeriod right) => left.Equals(right);
    public static bool operator !=(ValidityPeriod left, ValidityPeriod right) => !left.Equals(right);

    public void Deconstruct(out DateTimeOffset from, out DateTimeOffset? until)
    {
        from = From;
        until = Until;
    }
}

/// <summary>XAML/<see cref="TypeConverter"/> support for <see cref="ValidityPeriod"/> ↔ its canonical text.</summary>
public sealed class ValidityPeriodTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value is string s ? ValidityPeriod.Parse(s, culture) : base.ConvertFrom(context, culture, value);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        => value is ValidityPeriod period && destinationType == typeof(string)
            ? period.ToString()
            : base.ConvertTo(context, culture, value, destinationType);
}

/// <summary>Serializes <see cref="ValidityPeriod"/> as its canonical ISO 8601 interval string (<c>"from/until"</c>, <c>"from/.."</c>).</summary>
public sealed class ValidityPeriodJsonConverter : JsonConverter<ValidityPeriod>
{
    public override ValidityPeriod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ValidityPeriod.TryParse(reader.GetString(), CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException($"Cannot read ValidityPeriod from {reader.TokenType}.");

    public override void Write(Utf8JsonWriter writer, ValidityPeriod value, JsonSerializerOptions options)
    {
        Span<char> buffer = stackalloc char[72];
        if (value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture)) writer.WriteStringValue(buffer[..written]);
        else writer.WriteStringValue(value.ToString());
    }

    public override ValidityPeriod ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ValidityPeriod.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ValidityPeriod value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
