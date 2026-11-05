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
/// A phone number in E.164 form (authentication ⚑AN3): country calling code + national number, digits only, at most 15 digits.
/// National formats (Brazil's DDD, trunk prefixes) are parsed with a default country calling code; carrier and line-type metadata
/// are deliberately absent (whether a number takes SMS or WhatsApp is the sender's answer).
/// <code>
/// var phone = PhoneNumber.Parse("+55 (11) 98765-4321");
/// phone.E164;                                 // "+5511987654321"
/// phone.CountryCallingCode;                   // 55
/// phone.NationalNumber;                       // "11987654321"
/// PhoneNumber.TryParse("(11) 98765-4321", defaultCountryCallingCode: 55, out var same);   // same == phone
/// Classified.Mask(phone);                     // "**********4321"
/// </code>
/// <para>Personal data (contact): logs write the mask. <see cref="IClassifiedData{TSelf}"/> is hand-written (core, ⚑N16).</para>
/// </summary>
[Sensitive(DataSensitivity.Personal, Category = "contact", Mask = "{last:4}", Identifier = true)]
[TypeConverter(typeof(PhoneNumberTypeConverter))]
[JsonConverter(typeof(PhoneNumberJsonConverter))]
public readonly struct PhoneNumber :
    IEquatable<PhoneNumber>,
    ISpanFormattable,
    IUtf8SpanFormattable,
    ISpanParsable<PhoneNumber>,
    ICanonicalValue<PhoneNumber>,
    ILogValue<PhoneNumber>,
    IClassifiedData<PhoneNumber>
{
    /// <summary>Most digits of an E.164 number (country code included).</summary>
    public const int MaxDigits = 15;

    /// <summary>Fewest national digits accepted (short numbers and service codes are not contacts).</summary>
    public const int MinNationalDigits = 4;

    /// <summary>Longest <see cref="E164"/> text: '+' and 15 digits.</summary>
    public const int MaxLength = MaxDigits + 1;

    private readonly string? _e164;
    private readonly ushort _countryCallingCode;
    private readonly byte _countryDigits;

    private PhoneNumber(string e164, ushort countryCallingCode, int countryDigits)
    {
        _e164 = e164;
        _countryCallingCode = countryCallingCode;
        _countryDigits = (byte)countryDigits;
    }

    /// <summary>The ITU-T country calling code (55, 1, 351 …); 0 for <see langword="default"/>.</summary>
    public ushort CountryCallingCode => _countryCallingCode;

    /// <summary>The national (significant) number, digits only.</summary>
    public ReadOnlySpan<char> NationalNumber => _e164 is null ? default : _e164.AsSpan(1 + _countryDigits);

    /// <summary><c>+</c> followed by the digits; empty for <see langword="default"/>.</summary>
    public string E164 => _e164 ?? "";

    /// <summary>True for <see langword="default"/> (no number).</summary>
    public bool IsEmpty => _e164 is null;

    /// <summary>
    /// Parses <c>"+55 (11) 98765-4321"</c>, <c>"0055 11 98765 4321"</c> or, with <paramref name="defaultCountryCallingCode"/>, a
    /// national number (<c>"(11) 98765-4321"</c>; one leading trunk <c>0</c> is dropped, except for Italy where it is significant).
    /// Separators <c>space - . ( ) /</c> are ignored; anything else fails.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, ushort defaultCountryCallingCode, out PhoneNumber number)
    {
        number = default;
        text = text.Trim();
        if (text.IsEmpty || text.Length > 64) return false;

        Span<char> digits = stackalloc char[32];
        int count = 0;
        bool international = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '+' && i == 0)
            {
                international = true;
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                if (count == digits.Length) return false;
                digits[count++] = c;
                continue;
            }
            if (c is ' ' or '-' or '.' or '(' or ')' or '/' or ' ') continue;
            return false;
        }
        ReadOnlySpan<char> all = digits[..count];
        if (!international && all.StartsWith("00"))
        {
            international = true;
            all = all[2..];
        }

        Span<char> e164 = stackalloc char[MaxLength + 8];
        int length;
        int countryDigits;
        ushort countryCode;
        if (international)
        {
            countryDigits = CountryCodeLength(all);
            if (countryDigits == 0 || all.Length - countryDigits < MinNationalDigits || all.Length > MaxDigits) return false;
            countryCode = ushort.Parse(all[..countryDigits], CultureInfo.InvariantCulture);
            e164[0] = '+';
            all.CopyTo(e164[1..]);
            length = 1 + all.Length;
        }
        else
        {
            if (defaultCountryCallingCode is 0 or > 999) return false;
            countryCode = defaultCountryCallingCode;
            if (countryCode != 39 && all.Length > 0 && all[0] == '0') all = all[1..];
            if (all.Length < MinNationalDigits) return false;
            e164[0] = '+';
            countryCode.TryFormat(e164[1..], out countryDigits, default, CultureInfo.InvariantCulture);
            if (CountryCodeLength(e164[1..(1 + countryDigits)]) != countryDigits) return false;   // not an assigned code shape
            if (countryDigits + all.Length > MaxDigits) return false;
            all.CopyTo(e164[(1 + countryDigits)..]);
            length = 1 + countryDigits + all.Length;
        }
        number = new PhoneNumber(new string(e164[..length]), countryCode, countryDigits);
        return true;
    }

    /// <summary>An international number (<c>+</c> or <c>00</c> prefix required).</summary>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out PhoneNumber result) => TryParse(s, 0, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out PhoneNumber result)
        => TryParse(s.AsSpan(), 0, out result);

    public static PhoneNumber Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, 0, out PhoneNumber result) ? result : throw new FormatException("Not a valid international phone number.");

    public static PhoneNumber Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <summary>Parses with a default country calling code for national numbers.</summary>
    public static PhoneNumber Parse(ReadOnlySpan<char> s, ushort defaultCountryCallingCode)
        => TryParse(s, defaultCountryCallingCode, out PhoneNumber result) ? result : throw new FormatException("Not a valid phone number.");

    // ── equality and formatting ──────────────────────────────────────────────────────────────────────────────────────────

    public bool Equals(PhoneNumber other) => string.Equals(_e164, other._e164, StringComparison.Ordinal);
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is PhoneNumber other && Equals(other);
    public override int GetHashCode() => _e164 is null ? 0 : string.GetHashCode(_e164.AsSpan());
    public static bool operator ==(PhoneNumber left, PhoneNumber right) => left.Equals(right);
    public static bool operator !=(PhoneNumber left, PhoneNumber right) => !left.Equals(right);

    /// <summary>The E.164 text. Personal data: never log it (logs use the mask).</summary>
    public override string ToString() => E164;
    public string ToString(string? format, IFormatProvider? formatProvider) => E164;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        bool copied = E164.AsSpan().TryCopyTo(destination);
        charsWritten = copied ? E164.Length : 0;
        return copied;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => Encoding.ASCII.TryGetBytes(E164, utf8Destination, out bytesWritten);

    // ── canonical, log and privacy ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ASCII bytes of <see cref="E164"/>.</summary>
    public static int MaxCanonicalLength => MaxLength;

    public static bool TryWriteCanonical(in PhoneNumber value, Span<byte> destination, out int written)
        => Encoding.ASCII.TryGetBytes(value.E164, destination, out written);

    static void ILogValue<PhoneNumber>.Write(in PhoneNumber value, ref LogWriter writer, ushort field)
    {
        Span<char> masked = stackalloc char[MaxLength];
        TryWriteMasked(in value, masked, out int written);
        writer.Write(field, masked[..written]);
    }

    /// <summary>Personal data, category <c>contact</c>.</summary>
    public static DataClassification Classification { get; } = new(DataSensitivity.Personal, DataCategory.Contact);

    public static int MaxMaskedLength => MaxLength;

    /// <summary>Only the last four digits stay visible.</summary>
    public static bool TryWriteMasked(in PhoneNumber value, Span<char> destination, out int written)
        => Masks.TryWriteLast(value.E164, 4, destination, out written);

    // ── country calling codes (ITU-T E.164 assignment shape: 1-, 2- or 3-digit, prefix-free) ─────────────────────────────

    /// <summary>Length of the country calling code at the start of <paramref name="digits"/>; 0 when none can start there.</summary>
    internal static int CountryCodeLength(ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty || digits[0] == '0') return 0;
        if (digits[0] is '1' or '7') return 1;
        if (digits.Length < 2) return 0;
        int two = (digits[0] - '0') * 10 + (digits[1] - '0');
        if (IsTwoDigitCode(two)) return 2;
        return digits.Length < 3 ? 0 : 3;
    }

    private static bool IsTwoDigitCode(int code) => code switch
    {
        20 or 27 => true,
        30 or 31 or 32 or 33 or 34 or 36 or 39 => true,
        40 or 41 or 43 or 44 or 45 or 46 or 47 or 48 or 49 => true,
        51 or 52 or 53 or 54 or 55 or 56 or 57 or 58 => true,
        60 or 61 or 62 or 63 or 64 or 65 or 66 => true,
        81 or 82 or 84 or 86 => true,
        90 or 91 or 92 or 93 or 94 or 95 or 98 => true,
        _ => false,
    };
}

/// <summary>JSON as the E.164 string.</summary>
public sealed class PhoneNumberJsonConverter : JsonConverter<PhoneNumber>
{
    public override PhoneNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        string? text = reader.GetString();
        return PhoneNumber.TryParse(text, null, out PhoneNumber number)
            ? number
            : throw new JsonException("Not a valid international phone number.");
    }

    public override void Write(Utf8JsonWriter writer, PhoneNumber value, JsonSerializerOptions options)
    {
        if (value.IsEmpty) writer.WriteNullValue();
        else writer.WriteStringValue(value.E164);
    }
}
