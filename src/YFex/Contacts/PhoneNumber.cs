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
/// <para>Country rules come from <see cref="PhoneRegion"/>: <see cref="TryParse(ReadOnlySpan{char}, PhoneRegion, out PhoneNumber)"/>
/// validates against a numbering plan; <see cref="Region"/>, <see cref="AreaCode"/>, <see cref="IsMobile"/> and the <c>"N"</c> format
/// read it; <see cref="IExtractable{TSelf}"/> finds numbers of <see cref="PhoneRegion.Default"/> in free text.
/// The parsers that take a calling code check only the E.164 shape.</para>
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
    IClassifiedData<PhoneNumber>,
    IExtractable<PhoneNumber>
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

    /// <summary>The built-in numbering plan of <see cref="CountryCallingCode"/> (<see cref="PhoneRegion.Known"/>), or null.</summary>
    public PhoneRegion? Region => _e164 is null ? null : PhoneRegion.FromCallingCode(_countryCallingCode);

    /// <summary>The area code (Brazil's DDD <c>"11"</c>, NANP <c>"212"</c>); empty when <see cref="Region"/> has none.</summary>
    public ReadOnlySpan<char> AreaCode
    {
        get
        {
            int digits = Region?.AreaCodeLength ?? 0;
            ReadOnlySpan<char> national = NationalNumber;
            return digits > 0 && national.Length > digits ? national[..digits] : default;
        }
    }

    /// <summary>Whether the line is a mobile, as <see cref="Region"/> encodes it; null when it does not (NANP) or is unknown.</summary>
    public bool? IsMobile => Region?.IsMobile(NationalNumber);

    /// <summary>
    /// Parses with a numbering plan: a national number (<c>"(11) 98765-4321"</c>, trunk and carrier prefixes as the plan allows)
    /// must be valid in <paramref name="region"/>; an international one (<c>"+1 212 555 0100"</c>) must be valid in its own
    /// country's built-in plan, or have the E.164 shape when there is none.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, PhoneRegion region, out PhoneNumber number)
    {
        ArgumentNullException.ThrowIfNull(region);
        number = default;
        text = text.Trim();
        if (text.IsEmpty || text.Length > 64) return false;
        Span<char> digits = stackalloc char[32];
        if (!TryCollectDigits(text, digits, out int count, out bool international, out _)) return false;
        return TryFromDigits(digits[..count], international, formatted: true, [region], out number);
    }

    /// <summary>Parses with a numbering plan (see <see cref="TryParse(ReadOnlySpan{char}, PhoneRegion, out PhoneNumber)"/>).</summary>
    public static PhoneNumber Parse(ReadOnlySpan<char> text, PhoneRegion region)
        => TryParse(text, region, out PhoneNumber number) ? number : throw new FormatException($"Not a valid {region.Country} phone number.");

    /// <summary>The first phone of <see cref="PhoneRegion.Default"/> in <paramref name="text"/> (see the overload with regions).</summary>
    public static bool TryExtract(ReadOnlySpan<char> text, out PhoneNumber value, out int start, out int length)
        => TryExtract(text, PhoneRegion.DefaultRegions, out value, out start, out length);

    /// <summary>
    /// The first phone in <paramref name="text"/> that one of <paramref name="regions"/> accepts (in order), or that has a
    /// <c>+</c>/<c>00</c> country code: <c>(11) 98765-4321</c>, <c>11 3456-7890</c>, <c>+55 11 987654321</c>,
    /// <c>(212) 555-0100</c>. Bare digit runs count only when the plan is confident (a Brazilian mobile with its DDD); a match is
    /// never glued to letters or other digits. Allocates only on a match.
    /// </summary>
    public static bool TryExtract(ReadOnlySpan<char> text, ReadOnlySpan<PhoneRegion> regions, out PhoneNumber value, out int start, out int length)
    {
        value = default;
        start = 0;
        length = 0;
        if (text.IndexOfAnyInRange('0', '9') < 0) return false;
        Span<int> ends = stackalloc int[8];
        Span<char> digits = stackalloc char[32];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool opener = char.IsAsciiDigit(c) || (c is '+' or '(' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]));
            if (!opener) continue;
            if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '+')) continue;
            int endCount = GroupEnds(text, i, ends);
            for (int k = endCount - 1; k >= 0; k--)
            {
                int end = ends[k];
                if (!IsEndBoundary(text, end)) continue;
                if (!TryCollectDigits(text[i..end], digits, out int count, out bool international, out bool formatted)) continue;
                if (TryFromDigits(digits[..count], international, formatted, regions, out value))
                {
                    start = i;
                    length = end - i;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

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

    /// <summary><c>"N"</c>: the national layout of <see cref="Region"/> (<c>"(11) 98765-4321"</c>; E.164 when there is no
    /// region); anything else: <see cref="E164"/>.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => format is "N" or "n" && Region is { } region ? region.FormatNational(NationalNumber) : E164;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        string text = format is "N" or "n" ? ToString("N", provider) : E164;
        bool copied = text.AsSpan().TryCopyTo(destination);
        charsWritten = copied ? text.Length : 0;
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

    // ── region parsing and extraction ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Digits of <paramref name="text"/>. A leading <c>+</c> marks it international; separators (space, no-break space,
    /// <c>- . ( ) /</c>) are skipped and make it <paramref name="formatted"/>; any other character, or too many digits, fails.</summary>
    private static bool TryCollectDigits(ReadOnlySpan<char> text, Span<char> digits, out int count, out bool international, out bool formatted)
    {
        count = 0;
        international = false;
        formatted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                if (count == digits.Length) return false;
                digits[count++] = c;
                continue;
            }
            if (c == '+' && i == 0) international = true;
            else if (c is not (' ' or '-' or '.' or '(' or ')' or '/' or '\u00A0')) return false;
            formatted = true;
        }
        return count > 0;
    }

    /// <summary>A number from collected digits: international ones by their own country's plan, national ones by the first of
    /// <paramref name="regions"/> that accepts them (bare digit runs only where the plan says so).</summary>
    private static bool TryFromDigits(ReadOnlySpan<char> digits, bool international, bool formatted, ReadOnlySpan<PhoneRegion> regions, out PhoneNumber number)
    {
        number = default;
        if (!international && digits.StartsWith("00"))
        {
            international = true;
            digits = digits[2..];
        }
        if (international)
        {
            int countryDigits = CountryCodeLength(digits);
            if (countryDigits == 0 || digits.Length > MaxDigits) return false;
            ushort code = ushort.Parse(digits[..countryDigits], CultureInfo.InvariantCulture);
            ReadOnlySpan<char> national = digits[countryDigits..];
            PhoneRegion? plan = null;
            foreach (PhoneRegion region in regions)
            {
                if (region.CallingCode != code) continue;
                plan = region;
                break;
            }
            plan ??= PhoneRegion.FromCallingCode(code);
            if (plan is not null ? !plan.IsValidNational(national) : national.Length < MinNationalDigits) return false;
            return TryCreate(code, countryDigits, national, out number);
        }
        foreach (PhoneRegion region in regions)
        {
            ReadOnlySpan<char> national = region.ToNational(digits);
            if (!region.IsValidNational(national)) continue;
            if (!formatted && !region.AcceptsUnformatted(national)) continue;
            int countryDigits = region.CallingCode >= 100 ? 3 : region.CallingCode >= 10 ? 2 : 1;
            if (TryCreate(region.CallingCode, countryDigits, national, out number)) return true;
        }
        return false;
    }

    private static bool TryCreate(ushort code, int countryDigits, ReadOnlySpan<char> national, out PhoneNumber number)
    {
        number = default;
        int length = 1 + countryDigits + national.Length;
        if (length - 1 > MaxDigits) return false;
        Span<char> e164 = stackalloc char[MaxLength + 8];
        e164[0] = '+';
        code.TryFormat(e164[1..], out _, default, CultureInfo.InvariantCulture);
        national.CopyTo(e164[(1 + countryDigits)..]);
        number = new PhoneNumber(new string(e164[..length]), code, countryDigits);
        return true;
    }

    /// <summary>Where each digit group of a phone-shaped run starting at <paramref name="start"/> ends: an optional <c>+</c>,
    /// groups of digits (one may be in parentheses), single separators <c>space - .</c> between them. Stops past 17 digits.</summary>
    private static int GroupEnds(ReadOnlySpan<char> text, int start, Span<int> ends)
    {
        int p = start;
        int count = 0;
        int digits = 0;
        if (text[p] == '+') p++;
        while (p < text.Length && count < ends.Length)
        {
            bool paren = text[p] == '(';
            if (paren) p++;
            int group = p;
            while (p < text.Length && char.IsAsciiDigit(text[p])) p++;
            if (p == group) break;
            digits += p - group;
            if (digits > 17) break;
            if (paren)
            {
                if (p >= text.Length || text[p] != ')') break;
                p++;
            }
            ends[count++] = p;
            if (p + 1 < text.Length && text[p] is ' ' or '-' or '.' && (char.IsAsciiDigit(text[p + 1]) || text[p + 1] == '('))
                p++;
            else if (!(paren && p < text.Length && char.IsAsciiDigit(text[p])))
                break;
        }
        return count;
    }

    /// <summary>A match may not run into letters or digits, nor into <c>. - /</c> followed by a digit.</summary>
    private static bool IsEndBoundary(ReadOnlySpan<char> text, int end)
    {
        if (end >= text.Length) return true;
        char next = text[end];
        if (char.IsLetterOrDigit(next)) return false;
        return !(next is '.' or '-' or '/' && end + 1 < text.Length && char.IsAsciiDigit(text[end + 1]));
    }

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
