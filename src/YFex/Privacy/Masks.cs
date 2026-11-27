namespace YFex.Privacy;

/// <summary>Mask presets for values without a fixed layout (privacy design 02 §7.2).</summary>
public enum MaskPreset : byte
{
    /// <summary>No mask: the value is shown as is (not personal data).</summary>
    None = 0,

    /// <summary><c>******</c>: nothing of the value is shown.</summary>
    Hidden = 1,

    /// <summary><c>j***@acme.com.br</c>: first character of the local part, full domain.</summary>
    Email = 2,

    /// <summary><c>J. S. S.</c>: initials of the words with 3+ characters (does not reveal the length).</summary>
    Initials = 3,

    /// <summary><c>****4321</c>: fixed 4-star prefix + the last N characters.</summary>
    Last = 4,

    /// <summary><c>Rua****</c>: the first N characters + a fixed 4-star suffix.</summary>
    First = 5,

    /// <summary><c>192.168.10.*</c> / <c>2001:db8::*</c>: the last group of an IP address hidden.</summary>
    IpAddress = 6,
}

/// <summary>
/// Span-only masking helpers called by generated <see cref="IClassifiedData{TSelf}.TryWriteMasked"/> code and by UI/logging code.
/// Every method fails closed (a value that does not fit is written as <see cref="Hidden"/>) and returns false only when the
/// destination is too small. No allocation.
/// </summary>
public static class Masks
{
    /// <summary>The fully hidden form.</summary>
    public const string Hidden = "******";

    private const string Stars = "****";

    /// <summary>The default mask of a sensitivity level (02 §7.3).</summary>
    public static MaskPreset DefaultPreset(DataSensitivity sensitivity, out int count)
    {
        switch (sensitivity)
        {
            case DataSensitivity.Personal: count = 1; return MaskPreset.First;
            case DataSensitivity.PersonalIdentifier: count = 2; return MaskPreset.Last;
            case DataSensitivity.Financial: count = 4; return MaskPreset.Last;
            case DataSensitivity.SensitivePersonal or DataSensitivity.Secret: count = 0; return MaskPreset.Hidden;
            default: count = 0; return MaskPreset.None;
        }
    }

    /// <summary>Upper bound of the output of <see cref="TryWritePreset"/> for a source of <paramref name="sourceLength"/> characters.</summary>
    public static int MaxLength(MaskPreset preset, int count, int sourceLength) => preset switch
    {
        MaskPreset.Hidden => Hidden.Length,
        MaskPreset.Last or MaskPreset.First => Math.Max(Hidden.Length, count + Stars.Length),
        _ => Math.Max(Hidden.Length, sourceLength + Stars.Length),
    };

    /// <summary>Writes <see cref="Hidden"/>.</summary>
    public static bool TryWriteHidden(Span<char> destination, out int written) => TryCopy(Hidden, destination, out written);

    /// <summary>Writes the source unchanged (<see cref="MaskPreset.None"/>).</summary>
    public static bool TryWriteClear(ReadOnlySpan<char> source, Span<char> destination, out int written) => TryCopy(source, destination, out written);

    /// <summary>Applies a preset (<paramref name="count"/> is N for <see cref="MaskPreset.Last"/>/<see cref="MaskPreset.First"/>).</summary>
    public static bool TryWritePreset(ReadOnlySpan<char> source, MaskPreset preset, int count, Span<char> destination, out int written)
        => preset switch
        {
            MaskPreset.None => TryWriteClear(source, destination, out written),
            MaskPreset.Email => TryWriteEmail(source, destination, out written),
            MaskPreset.Initials => TryWriteInitials(source, destination, out written),
            MaskPreset.Last => TryWriteLast(source, count, destination, out written),
            MaskPreset.First => TryWriteFirst(source, count, destination, out written),
            MaskPreset.IpAddress => TryWriteIpAddress(source, destination, out written),
            _ => TryWriteHidden(destination, out written),
        };

    /// <summary>
    /// Positional pattern(s) (02 §7.1): <c>#</c> copies the source character, <c>*</c> writes <c>*</c>, any other character is a
    /// literal that must equal the source character. Alternatives are separated by <c>|</c>; the first whose length equals the
    /// source length and whose literals match is used. No match → <see cref="Hidden"/>.
    /// </summary>
    public static bool TryWritePattern(ReadOnlySpan<char> source, ReadOnlySpan<char> patterns, Span<char> destination, out int written)
    {
        while (true)
        {
            int separator = patterns.IndexOf('|');
            ReadOnlySpan<char> pattern = separator < 0 ? patterns : patterns[..separator];
            if (pattern.Length == source.Length && LiteralsMatch(source, pattern))
            {
                if (destination.Length < pattern.Length)
                {
                    written = 0;
                    return false;
                }
                for (int i = 0; i < pattern.Length; i++)
                    destination[i] = pattern[i] switch { '#' => source[i], '*' => '*', _ => pattern[i] };
                written = pattern.Length;
                return true;
            }
            if (separator < 0) return TryWriteHidden(destination, out written);
            patterns = patterns[(separator + 1)..];
        }
    }

    /// <summary><c>joao.silva@acme.com.br</c> → <c>j***@acme.com.br</c>.</summary>
    public static bool TryWriteEmail(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        int at = source.LastIndexOf('@');
        if (at <= 0 || at == source.Length - 1) return TryWriteHidden(destination, out written);
        ReadOnlySpan<char> domain = source[at..];                     // includes '@'
        int length = 1 + 3 + domain.Length;
        if (destination.Length < length)
        {
            written = 0;
            return false;
        }
        destination[0] = source[0];
        destination[1] = '*';
        destination[2] = '*';
        destination[3] = '*';
        domain.CopyTo(destination[4..]);
        written = length;
        return true;
    }

    /// <summary><c>João da Silva Santos</c> → <c>J. S. S.</c> (words of 3+ characters).</summary>
    public static bool TryWriteInitials(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        int position = 0;
        int index = 0;
        while (index < source.Length)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index])) index++;
            int start = index;
            while (index < source.Length && !char.IsWhiteSpace(source[index])) index++;
            if (index - start < 3) continue;
            int needed = position == 0 ? 2 : 3;
            if (destination.Length < position + needed)
            {
                written = 0;
                return false;
            }
            if (position > 0) destination[position++] = ' ';
            destination[position++] = char.ToUpperInvariant(source[start]);
            destination[position++] = '.';
        }
        if (position == 0) return TryWriteHidden(destination, out written);
        written = position;
        return true;
    }

    /// <summary><c>(11) 98765-4321</c>, N = 4 → <c>****4321</c>. Sources not longer than N are hidden.</summary>
    public static bool TryWriteLast(ReadOnlySpan<char> source, int count, Span<char> destination, out int written)
    {
        if (count <= 0 || source.Length <= count) return TryWriteHidden(destination, out written);
        if (destination.Length < Stars.Length + count)
        {
            written = 0;
            return false;
        }
        Stars.AsSpan().CopyTo(destination);
        source[^count..].CopyTo(destination[Stars.Length..]);
        written = Stars.Length + count;
        return true;
    }

    /// <summary><c>Rua das Flores 123</c>, N = 3 → <c>Rua****</c>. Sources not longer than N are hidden.</summary>
    public static bool TryWriteFirst(ReadOnlySpan<char> source, int count, Span<char> destination, out int written)
    {
        if (count <= 0 || source.Length <= count) return TryWriteHidden(destination, out written);
        if (destination.Length < count + Stars.Length)
        {
            written = 0;
            return false;
        }
        source[..count].CopyTo(destination);
        Stars.AsSpan().CopyTo(destination[count..]);
        written = count + Stars.Length;
        return true;
    }

    /// <summary><c>192.168.10.42</c> → <c>192.168.10.*</c>; <c>2001:db8::1</c> → <c>2001:db8::*</c>. Anything else is hidden.</summary>
    public static bool TryWriteIpAddress(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        int last;
        if (source.Contains(':')) last = source.LastIndexOf(':');
        else if (source.Count('.') == 3) last = source.LastIndexOf('.');
        else return TryWriteHidden(destination, out written);
        if (last <= 0 || last == source.Length - 1) return TryWriteHidden(destination, out written);
        int length = last + 2;
        if (destination.Length < length)
        {
            written = 0;
            return false;
        }
        source[..(last + 1)].CopyTo(destination);
        destination[last + 1] = '*';
        written = length;
        return true;
    }

    /// <summary>
    /// Applies a <see cref="SensitiveAttribute.Mask"/> spec read at run time (forms ⚑FM63: a raw <c>[Sensitive]</c> property
    /// such as a <c>string</c>, which has no generated mask of its own): a preset <c>{hidden}</c>, <c>{email}</c>,
    /// <c>{initials}</c>, <c>{ip}</c>, <c>{last:N}</c>, <c>{first:N}</c>, else positional pattern(s); null or empty =
    /// <see cref="DefaultPreset"/> of <paramref name="sensitivity"/>. A spec that does not parse fails closed (<see cref="Hidden"/>).
    /// </summary>
    public static bool TryWriteSpec(ReadOnlySpan<char> source, string? mask, DataSensitivity sensitivity, Span<char> destination, out int written)
    {
        if (sensitivity == DataSensitivity.Secret) return TryWriteHidden(destination, out written);
        if (string.IsNullOrEmpty(mask))
        {
            var preset = DefaultPreset(sensitivity, out var count);
            return TryWritePreset(source, preset, count, destination, out written);
        }
        ReadOnlySpan<char> spec = mask;
        if (spec[0] != '{') return TryWritePattern(source, spec, destination, out written);
        if (spec[^1] != '}') return TryWriteHidden(destination, out written);
        spec = spec[1..^1];
        var colon = spec.IndexOf(':');
        var name = colon < 0 ? spec : spec[..colon];
        var n = 0;
        if (colon >= 0 && (!int.TryParse(spec[(colon + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out n) || n <= 0))
            return TryWriteHidden(destination, out written);
        return name switch
        {
            "email" => TryWriteEmail(source, destination, out written),
            "initials" => TryWriteInitials(source, destination, out written),
            "ip" => TryWriteIpAddress(source, destination, out written),
            "last" when n > 0 => TryWriteLast(source, n, destination, out written),
            "first" when n > 0 => TryWriteFirst(source, n, destination, out written),
            _ => TryWriteHidden(destination, out written),
        };
    }

    private static bool LiteralsMatch(ReadOnlySpan<char> source, ReadOnlySpan<char> pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] is not ('#' or '*') && pattern[i] != source[i]) return false;
        return true;
    }

    private static bool TryCopy(ReadOnlySpan<char> text, Span<char> destination, out int written)
    {
        if (!text.TryCopyTo(destination))
        {
            written = 0;
            return false;
        }
        written = text.Length;
        return true;
    }
}
