using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex.Reasons;

/// <summary>
/// Stable identity of one item of a "why" explanation (a rule set, stage, group, rule, table row, line or condition): what a
/// UI filters by ("drill in"), links to and sends back, never a text shown to people (⚑WH1, validation 14 §3.5). The form is
/// <c>{scheme}:{id}</c>: the scheme names what the producer identifies (<c>rule</c>, <c>stage</c> …) and the id is the
/// producer's stable code (<c>sale.installments#7f3c…</c>). Producers such as YFex.Rules build their keys; everyone else only
/// compares, stores and parses them.
/// </summary>
/// <remarks>
/// <b>Type only</b>: YFex core holds the key so Rules, Validation, Authorization and the UI can name it without each other.
/// Grammar: the scheme is non-empty and made of ASCII lower-case letters, digits and <c>-</c>; the id is non-empty and has no
/// white space or control characters. Equality is ordinal.
/// </remarks>
/// <example><code>
/// var row = WhyKey.Create("rule", "sale.installments#7f3c0a…");
/// row.ToString();                                  // "rule:sale.installments#7f3c0a…"
/// row.Scheme.SequenceEqual("rule");                // true
/// WhyKey.TryParse("rule:sale.installments#7f3c0a…", null, out var back) &amp;&amp; back == row;   // true
/// </code></example>
[JsonConverter(typeof(WhyKeyJsonConverter))]
public readonly struct WhyKey : IEquatable<WhyKey>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<WhyKey>,
    global::YFex.Diagnostics.ILogValue<WhyKey>
{
    private readonly string? _value;
    private readonly int _colon;

    private WhyKey(string value, int colon)
    {
        _value = value;
        _colon = colon;
    }

    /// <summary>Parses the stable form <c>{scheme}:{id}</c>; throws when it is malformed.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not follow the key grammar.</exception>
    /// <example><code>var key = new WhyKey("stage:sale/payment");</code></example>
    public WhyKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int colon = SplitAt(value);
        if (colon < 0) throw new ArgumentException($"'{value}' is not a why key ({{scheme}}:{{id}}).", nameof(value));
        _value = value;
        _colon = colon;
    }

    /// <summary>Builds a key from its scheme and id; throws when either breaks the grammar.</summary>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> or <paramref name="id"/> is malformed.</exception>
    /// <example><code>WhyKey.Create("group", "sale/Installments");   // "group:sale/Installments"</code></example>
    public static WhyKey Create(ReadOnlySpan<char> scheme, ReadOnlySpan<char> id)
    {
        if (!IsScheme(scheme)) throw new ArgumentException($"'{scheme}' is not a why key scheme (a-z, 0-9, '-').", nameof(scheme));
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a why key id (non-empty, no white space).", nameof(id));
        return new WhyKey(string.Concat(scheme, ":", id), scheme.Length);
    }

    /// <summary>The stable form; <c>""</c> for <see langword="default"/>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True for <see langword="default"/> (no key).</summary>
    public bool IsEmpty => _value is null;

    /// <summary>What the key identifies (<c>"rule"</c>); empty for <see langword="default"/>.</summary>
    public ReadOnlySpan<char> Scheme => _value is null ? default : _value.AsSpan(0, _colon);

    /// <summary>The producer's id after the scheme (<c>"sale.installments#7f3c…"</c>); empty for <see langword="default"/>.</summary>
    public ReadOnlySpan<char> Id => _value is null ? default : _value.AsSpan(_colon + 1);

    /// <summary>True when the key has scheme <paramref name="scheme"/> (ordinal).</summary>
    /// <example><code>if (segment.Key.HasScheme("rule")) OpenRule(segment.Key);</code></example>
    public bool HasScheme(ReadOnlySpan<char> scheme) => _value is not null && Scheme.SequenceEqual(scheme);

    /// <summary>Parses text that arrived as data (wire, store); false when it is not a key.</summary>
    public static bool TryCreate([NotNullWhen(true)] string? value, out WhyKey key)
    {
        int colon = value is null ? -1 : SplitAt(value);
        key = colon < 0 ? default : new WhyKey(value!, colon);
        return colon >= 0;
    }

    public bool Equals(WhyKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is WhyKey other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public static bool operator ==(WhyKey left, WhyKey right) => left.Equals(right);

    public static bool operator !=(WhyKey left, WhyKey right) => !left.Equals(right);

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (Value.AsSpan().TryCopyTo(destination))
        {
            charsWritten = Value.Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => Encoding.UTF8.TryGetBytes(Value, utf8Destination, out bytesWritten);

    public static WhyKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out WhyKey key) ? key : throw new FormatException($"'{s}' is not a why key ({{scheme}}:{{id}}).");

    public static WhyKey Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new WhyKey(s);
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out WhyKey result)
    {
        int colon = SplitAt(s);
        result = colon < 0 ? default : new WhyKey(s.ToString(), colon);
        return colon >= 0;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out WhyKey result) => TryCreate(s, out result);

    static void global::YFex.Diagnostics.ILogValue<WhyKey>.Write(in WhyKey value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
    {
        if (value._value is null) writer.WriteNull(field);
        else writer.Write(field, value._value);
    }

    /// <summary>Index of the scheme's ':' when <paramref name="value"/> follows the grammar; −1 otherwise.</summary>
    private static int SplitAt(ReadOnlySpan<char> value)
    {
        int colon = value.IndexOf(':');
        return colon > 0 && IsScheme(value[..colon]) && IsId(value[(colon + 1)..]) ? colon : -1;
    }

    private static bool IsScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty) return false;
        foreach (char c in scheme)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) return false;
        return true;
    }

    private static bool IsId(ReadOnlySpan<char> id)
    {
        if (id.IsEmpty) return false;
        foreach (char c in id)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        return true;
    }
}

/// <summary>JSON form of <see cref="WhyKey"/>: its stable text, <see langword="null"/> for an empty key.</summary>
public sealed class WhyKeyJsonConverter : JsonConverter<WhyKey>
{
    public override WhyKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        return WhyKey.TryCreate(reader.GetString(), out WhyKey key) ? key : throw new JsonException("Not a valid why key.");
    }

    public override void Write(Utf8JsonWriter writer, WhyKey value, JsonSerializerOptions options)
    {
        if (value.IsEmpty) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}
