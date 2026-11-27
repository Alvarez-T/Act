using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YFex.Canonical;
using YFex.Diagnostics;
using YFex.Privacy;

namespace YFex;

/// <summary>
/// An e-mail address (authentication ⚑AN3): trimmed, lower-cased, an internationalised domain converted to punycode. Generalised from
/// <c>YFex.Entity.Public.Email</c> so infrastructure libraries (authentication, delivery, privacy) can name a contact without a domain
/// repository. The local part is lower-cased too (pragmatic: every mainstream provider treats it case-insensitively).
/// <code>
/// var address = EmailAddress.Parse(" Ana.Souza@FarmaViva.com.br ");
/// address.Value;                    // "ana.souza@farmaviva.com.br"
/// address.Domain;                   // "farmaviva.com.br" (a slice, no allocation)
/// Classified.Mask(address);         // "a***@farmaviva.com.br"
/// </code>
/// <para>Personal data (contact): logs write the mask, never the address. <see cref="IClassifiedData{TSelf}"/> is written by hand
/// because YFex core does not run the Privacy generator (⚑N16).</para>
/// </summary>
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{email}", Identifier = true)]
[TypeConverter(typeof(EmailAddressTypeConverter))]
[JsonConverter(typeof(EmailAddressJsonConverter))]
public readonly struct EmailAddress :
    IEquatable<EmailAddress>,
    ISpanFormattable,
    IUtf8SpanFormattable,
    ISpanParsable<EmailAddress>,
    ICanonicalValue<EmailAddress>,
    ILogValue<EmailAddress>,
    IClassifiedData<EmailAddress>,
    IValueObject
{
    /// <summary>Longest address accepted (RFC 5321 path limit minus the brackets).</summary>
    public const int MaxLength = 254;

    /// <summary>Longest local part (RFC 5321 §4.5.3.1.1).</summary>
    public const int MaxLocalLength = 64;

    private readonly string? _value;
    private readonly byte _at;   // index of '@' (≤ 64)

    private EmailAddress(string value, int at)
    {
        _value = value;
        _at = (byte)at;
    }

    /// <summary>The normalised address; empty for <see langword="default"/>.</summary>
    public string Value => _value ?? "";

    /// <summary>True for <see langword="default"/> (no address).</summary>
    public bool IsEmpty => _value is null;

    /// <summary>The part before <c>@</c>.</summary>
    public ReadOnlySpan<char> Local => _value is null ? default : _value.AsSpan(0, _at);

    /// <summary>The domain (ASCII, punycode for internationalised names).</summary>
    public ReadOnlySpan<char> Domain => _value is null ? default : _value.AsSpan(_at + 1);

    /// <summary>Parses and normalises <paramref name="text"/>; false when it is not a usable address.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out EmailAddress address)
    {
        address = default;
        text = text.Trim();
        if (text.IsEmpty || text.Length > 1024) return false;
        int at = text.LastIndexOf('@');
        if (at <= 0 || at > MaxLocalLength || at == text.Length - 1) return false;
        ReadOnlySpan<char> local = text[..at];
        ReadOnlySpan<char> domain = text[(at + 1)..];
        if (!IsValidLocal(local)) return false;

        string? asciiDomain = null;
        if (!Ascii.IsValid(domain))
        {
            try
            {
                asciiDomain = new IdnMapping().GetAscii(domain.ToString());
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            domain = asciiDomain;
        }
        if (!IsValidDomain(domain)) return false;

        int length = local.Length + 1 + domain.Length;
        if (length > MaxLength) return false;
        string value = string.Create(length, (local.ToString(), domain.ToString()), static (buffer, parts) =>
        {
            Ascii.ToLower(parts.Item1, buffer, out int written);
            buffer[written] = '@';
            Ascii.ToLower(parts.Item2, buffer[(written + 1)..], out _);
        });
        address = new EmailAddress(value, at);
        return true;
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out EmailAddress result) => TryParse(s, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out EmailAddress result)
        => TryParse(s.AsSpan(), out result);

    public static EmailAddress Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, out EmailAddress result) ? result : throw new FormatException("Not a valid e-mail address.");

    public static EmailAddress Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    // ── equality and formatting ──────────────────────────────────────────────────────────────────────────────────────────

    public bool Equals(EmailAddress other) => string.Equals(_value, other._value, StringComparison.Ordinal);
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is EmailAddress other && Equals(other);
    public override int GetHashCode() => _value is null ? 0 : string.GetHashCode(_value.AsSpan());
    public static bool operator ==(EmailAddress left, EmailAddress right) => left.Equals(right);
    public static bool operator !=(EmailAddress left, EmailAddress right) => !left.Equals(right);

    /// <summary>The address. Personal data: never log it (logs use the mask through <see cref="ILogValue{TSelf}"/>).</summary>
    public override string ToString() => Value;
    public string ToString(string? format, IFormatProvider? formatProvider) => Value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        bool copied = Value.AsSpan().TryCopyTo(destination);
        charsWritten = copied ? Value.Length : 0;
        return copied;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => Encoding.UTF8.TryGetBytes(Value, utf8Destination, out bytesWritten);

    // ── canonical, log and privacy ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ASCII bytes of <see cref="Value"/>.</summary>
    public static int MaxCanonicalLength => MaxLength;

    public static bool TryWriteCanonical(in EmailAddress value, Span<byte> destination, out int written)
        => Encoding.ASCII.TryGetBytes(value.Value, destination, out written);

    /// <summary>Logs the mask (<c>a***@domain</c>), never the address.</summary>
    static void ILogValue<EmailAddress>.Write(in EmailAddress value, ref LogWriter writer, ushort field)
    {
        Span<char> masked = stackalloc char[MaxLength + 4];
        TryWriteMasked(in value, masked, out int written);
        writer.Write(field, masked[..written]);
    }

    /// <summary>Personal data, category <c>contact</c>.</summary>
    public static DataClassification Classification { get; } = new(DataSensitivity.Personal, DataCategory.Contact);

    public static int MaxMaskedLength => MaxLength + 4;

    /// <summary><c>ana.souza@farmaviva.com.br</c> → <c>a***@farmaviva.com.br</c>.</summary>
    public static bool TryWriteMasked(in EmailAddress value, Span<char> destination, out int written)
        => Masks.TryWriteEmail(value.Value, destination, out written);

    // ── validation ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsValidLocal(ReadOnlySpan<char> local)
    {
        if (local[0] == '.' || local[^1] == '.') return false;
        for (int i = 0; i < local.Length; i++)
        {
            char c = local[i];
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if (c == '.')
            {
                if (local[i - 1] == '.') return false;
                continue;
            }
            if ("!#$%&'*+/=?^_`{|}~-".Contains(c)) continue;
            return false;
        }
        return true;
    }

    private static bool IsValidDomain(ReadOnlySpan<char> domain)
    {
        if (domain.Length > 253 || domain.IndexOf('.') < 0) return false;
        int labelStart = 0;
        for (int i = 0; i <= domain.Length; i++)
        {
            if (i < domain.Length && domain[i] != '.')
            {
                char c = domain[i];
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
                continue;
            }
            int labelLength = i - labelStart;
            if (labelLength is 0 or > 63) return false;
            if (domain[labelStart] == '-' || domain[i - 1] == '-') return false;
            labelStart = i + 1;
        }
        // The top-level label is never all digits (rejects "user@1.2.3.4" written without brackets).
        int lastDot = domain.LastIndexOf('.');
        return domain[(lastDot + 1)..].ContainsAnyExcept("0123456789");
    }
}

/// <summary>JSON as the address string.</summary>
public sealed class EmailAddressJsonConverter : JsonConverter<EmailAddress>
{
    public override EmailAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        string? text = reader.GetString();
        return EmailAddress.TryParse(text, null, out EmailAddress address)
            ? address
            : throw new JsonException("Not a valid e-mail address.");
    }

    public override void Write(Utf8JsonWriter writer, EmailAddress value, JsonSerializerOptions options)
    {
        if (value.IsEmpty) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}
