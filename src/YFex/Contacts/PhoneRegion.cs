using System.Globalization;

namespace YFex;

/// <summary>
/// A country's phone numbering plan: its calling code, national layout (<see cref="Mask"/>) and the rules that tell a real
/// national number from any run of digits. <see cref="PhoneNumber"/> stays one type for every country; the region only adds
/// validation, formatting and extraction. Built in: <see cref="Brazil"/> and <see cref="UnitedStates"/> (NANP); any other
/// country can be described with the constructor (mask only, length checks) or a subclass (its own rules).
/// <code>
/// PhoneNumber.TryParse("(11) 98765-4321", PhoneRegion.Brazil, out var phone);   // validated by Brazil's rules
/// phone.AreaCode;                                                              // "11"
/// phone.IsMobile;                                                              // true
/// phone.ToString("N", null);                                                   // "(11) 98765-4321"
/// PhoneRegion.Default = [PhoneRegion.Brazil];                                  // what IExtractable looks for, app-wide
/// </code>
/// </summary>
public class PhoneRegion
{
    private static volatile PhoneRegion[]? s_default;

    /// <param name="country">ISO 3166-1 alpha-2 code (<c>"PT"</c>).</param>
    /// <param name="callingCode">ITU-T country calling code (351).</param>
    /// <param name="mask">National layout, <c>#</c> = a digit (<c>"### ### ###"</c>); its digit count is the longest national number.</param>
    /// <param name="shortMask">Layout for the shorter national numbers, when the country has two lengths.</param>
    public PhoneRegion(string country, ushort callingCode, string mask, string? shortMask = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        ArgumentException.ThrowIfNullOrWhiteSpace(mask);
        if (callingCode is 0 or > 999) throw new ArgumentOutOfRangeException(nameof(callingCode));
        Country = country;
        CallingCode = callingCode;
        Mask = mask;
        ShortMask = shortMask;
    }

    /// <summary>Brazil (+55): DDD + 8-digit landline (2–5) or 9-digit mobile (9).</summary>
    public static PhoneRegion Brazil { get; } = new BrazilPhoneRegion();

    /// <summary>United States and the other NANP countries (+1): 3-digit area code + 7 digits.</summary>
    public static PhoneRegion UnitedStates { get; } = new UnitedStatesPhoneRegion();

    /// <summary>The regions with built-in rules.</summary>
    public static IReadOnlyList<PhoneRegion> Known { get; } = [Brazil, UnitedStates];

    /// <summary>
    /// The regions <see cref="PhoneNumber"/>'s <see cref="IExtractable{TSelf}"/> looks for in free text, in order (the first that
    /// accepts a number wins). Set once at startup; until then it follows the current culture: <c>*-BR</c> → Brazil,
    /// <c>*-US</c> → United States, anything else → every <see cref="Known"/> region.
    /// </summary>
    public static IReadOnlyList<PhoneRegion> Default
    {
        get => s_default ?? FromCulture(CultureInfo.CurrentCulture);
        set => s_default = value is null ? null : [.. value];
    }

    /// <summary><see cref="Default"/> without a copy (no allocation on the extraction path).</summary>
    internal static ReadOnlySpan<PhoneRegion> DefaultRegions => s_default ?? FromCulture(CultureInfo.CurrentCulture);

    /// <summary>ISO 3166-1 alpha-2 code: <c>"BR"</c>, <c>"US"</c>.</summary>
    public string Country { get; }

    /// <summary>ITU-T country calling code: 55, 1.</summary>
    public ushort CallingCode { get; }

    /// <summary>National layout, <c>#</c> = a digit: <c>"(##) #####-####"</c>.</summary>
    public string Mask { get; }

    /// <summary>Layout for the shorter national numbers (Brazil's landlines), or null.</summary>
    public string? ShortMask { get; }

    /// <summary>Digits of the area code at the start of the national number (Brazil 2, NANP 3); 0 when the plan has none.</summary>
    public virtual int AreaCodeLength => 0;

    /// <summary>Whether <paramref name="national"/> (digits only, no country code or trunk prefix) is a number of this plan.
    /// The base checks only the length against <see cref="Mask"/> and <see cref="ShortMask"/>.</summary>
    public virtual bool IsValidNational(ReadOnlySpan<char> national)
    {
        if (!AllDigits(national)) return false;
        int longest = Slots(Mask);
        int shortest = ShortMask is null ? longest : Slots(ShortMask);
        return national.Length >= Math.Min(shortest, longest) && national.Length <= Math.Max(shortest, longest);
    }

    /// <summary>Whether the line is a mobile; null when the plan does not say (NANP).</summary>
    public virtual bool? IsMobile(ReadOnlySpan<char> national) => null;

    /// <summary>The national number laid out with <see cref="ShortMask"/> or <see cref="Mask"/>: <c>"(11) 98765-4321"</c>.</summary>
    public string FormatNational(ReadOnlySpan<char> national)
    {
        string mask = ShortMask is { } shortMask && national.Length == Slots(shortMask) ? shortMask : Mask;
        if (national.Length != Slots(mask)) return national.ToString();
        return string.Create(mask.Length, (mask, national.ToString()), static (buffer, state) =>
        {
            int next = 0;
            for (int i = 0; i < buffer.Length; i++) buffer[i] = state.mask[i] == '#' ? state.Item2[next++] : state.mask[i];
        });
    }

    /// <summary>
    /// Turns digits typed without <c>+</c> into the national number: drops the trunk prefix and anything the plan allows in front
    /// (Brazil's carrier code, a leading 55). Returns the slice of <paramref name="digits"/> that is the national number.
    /// The base drops one leading <c>0</c>.
    /// </summary>
    public virtual ReadOnlySpan<char> ToNational(ReadOnlySpan<char> digits) => digits.StartsWith('0') ? digits[1..] : digits;

    /// <summary>Whether a bare run of digits (no separators, no <c>+</c>) in free text is confidently a phone. Formatted
    /// numbers are always considered; bare ones collide with ids, timestamps and amounts, so the base says no.</summary>
    public virtual bool AcceptsUnformatted(ReadOnlySpan<char> national) => false;

    /// <summary>The built-in region for a calling code, or null.</summary>
    public static PhoneRegion? FromCallingCode(ushort callingCode)
    {
        foreach (PhoneRegion region in Known)
            if (region.CallingCode == callingCode) return region;
        return null;
    }

    public override string ToString() => $"{Country} +{CallingCode}";

    private static PhoneRegion[] FromCulture(CultureInfo culture)
    {
        if (culture.Name.EndsWith("-BR", StringComparison.OrdinalIgnoreCase)) return s_brazilOnly;
        if (culture.Name.EndsWith("-US", StringComparison.OrdinalIgnoreCase)) return s_unitedStatesOnly;
        return s_known;
    }

    private static readonly PhoneRegion[] s_brazilOnly = [Brazil];
    private static readonly PhoneRegion[] s_unitedStatesOnly = [UnitedStates];
    private static readonly PhoneRegion[] s_known = [Brazil, UnitedStates];

    private protected static bool AllDigits(ReadOnlySpan<char> digits) => !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');

    private static int Slots(string mask) => mask.AsSpan().Count('#');
}

/// <summary>
/// Brazil: 2-digit DDD (Anatel's assigned list), then an 8-digit landline starting 2–5 or a 9-digit mobile starting 9. Typed
/// forms also carry the trunk 0, a 2-digit carrier code (<c>0 21 11 98765-4321</c>) or the country code without <c>+</c>.
/// </summary>
internal sealed class BrazilPhoneRegion() : PhoneRegion("BR", 55, "(##) #####-####", "(##) ####-####")
{
    public override int AreaCodeLength => 2;

    public override bool IsValidNational(ReadOnlySpan<char> national)
    {
        if (national.Length is not (10 or 11) || !AllDigits(national)) return false;
        if (!IsDdd((national[0] - '0') * 10 + (national[1] - '0'))) return false;
        return national.Length == 11 ? national[2] == '9' : national[2] is >= '2' and <= '5';
    }

    public override bool? IsMobile(ReadOnlySpan<char> national) => national.Length == 11 && national[2] == '9';

    public override ReadOnlySpan<char> ToNational(ReadOnlySpan<char> digits)
    {
        if (digits.StartsWith('0'))
        {
            digits = digits[1..];
            if (digits.Length is 12 or 13) digits = digits[2..];            // carrier code (0 21 11 98765-4321)
        }
        else if (digits.Length is 12 or 13 && digits.StartsWith("55"))
            digits = digits[2..];                                           // country code typed without +
        return digits;
    }

    public override bool AcceptsUnformatted(ReadOnlySpan<char> national) => national.Length == 11 && national[2] == '9';

    // Area codes assigned by Anatel (Plano Geral de Códigos Nacionais).
    private static bool IsDdd(int ddd) => ddd switch
    {
        >= 11 and <= 19 => true,
        21 or 22 or 24 or 27 or 28 => true,
        >= 31 and <= 35 or 37 or 38 => true,
        >= 41 and <= 49 => true,
        51 or 53 or 54 or 55 => true,
        >= 61 and <= 69 => true,
        71 or 73 or 74 or 75 or 77 or 79 => true,
        >= 81 and <= 89 => true,
        >= 91 and <= 99 => true,
        _ => false,
    };
}

/// <summary>
/// North American Numbering Plan (+1): area code <c>[2-9][0-8]X</c>, exchange <c>[2-9]XX</c>, neither an <c>N11</c> service
/// code, 10 digits. A leading 1 (long-distance prefix) is dropped. Line type is not encoded, so <see cref="IsMobile"/> is null.
/// </summary>
internal sealed class UnitedStatesPhoneRegion() : PhoneRegion("US", 1, "(###) ###-####")
{
    public override int AreaCodeLength => 3;

    public override bool IsValidNational(ReadOnlySpan<char> national)
    {
        if (national.Length != 10 || !AllDigits(national)) return false;
        return IsCode(national[..3], areaCode: true) && IsCode(national[3..6], areaCode: false);
    }

    public override ReadOnlySpan<char> ToNational(ReadOnlySpan<char> digits)
        => digits.Length == 11 && digits[0] == '1' ? digits[1..] : digits;

    private static bool IsCode(ReadOnlySpan<char> code, bool areaCode)
    {
        if (code[0] is < '2' or > '9') return false;
        if (areaCode && code[1] == '9') return false;                       // [2-9][0-8]X
        return !(code[1] == '1' && code[2] == '1');                         // N11 = service codes (211, 411, 911)
    }
}
