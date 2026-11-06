using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;
using YFex.Canonical;
using YFex.Diagnostics;

namespace YFex.Tenancy;

/// <summary>
/// Where something belongs in the app's tenancy hierarchy, as its one canonical text: <c>{rank}={value}/…</c>, least specific
/// first, for example <c>0=farmaviva/1=loja7</c>. The framework fixes no level names: ranks are the app's own levels (company,
/// branch, store, terminal, or a single tenant), the same ranks YFex.Tenancy's <c>ScopeIdentity</c> uses. <c>default</c> is
/// <see cref="None"/> (no tenancy).
/// <para>
/// This is the value to store and send (actor home, installation home, claims, tokens, envelopes); YFex.Tenancy converts it to and
/// from <c>ScopeIdentity</c> for resolution. Core owns the grammar, so every library accepts exactly the same texts.
/// </para>
/// <para>
/// Grammar (strict: one value has exactly one text): 1 to <see cref="MaxLevels"/> levels separated by <c>/</c>; each level is a
/// rank (0–255, decimal, no leading zeros), <c>=</c> and a non-empty value; ranks strictly increase; in values <c>%</c> and
/// <c>/</c> are written <c>%25</c> and <c>%2F</c> (no other escape is valid); no control characters, well-formed UTF-16; at most
/// <see cref="MaxLength"/> UTF-8 bytes. Equality is ordinal on the text.
/// </para>
/// <para>Canonical bytes: <c>length (2, little endian, 0 = none) · UTF-8 text</c>. Logged as its text.</para>
/// </summary>
public readonly record struct TenancyPath :
    ICanonicalValue<TenancyPath>, ILogValue<TenancyPath>, ISpanFormattable, ISpanParsable<TenancyPath>
{
    /// <summary>Maximum length of the text in UTF-8 bytes.</summary>
    public const int MaxLength = 512;

    /// <summary>Maximum number of levels (same as YFex.Tenancy's <c>ScopeIdentity.MaxLevels</c>).</summary>
    public const int MaxLevels = 8;

    /// <summary>The level separator.</summary>
    public const char Separator = '/';

    private readonly string? _text;

    private TenancyPath(string text) => _text = text;

    /// <summary>No tenancy (<c>default</c>); its text is empty.</summary>
    public static TenancyPath None => default;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => _text is null;

    /// <summary>The number of levels; 0 for <see cref="None"/>.</summary>
    public int Depth => _text is null ? 0 : _text.AsSpan().Count(Separator) + 1;

    /// <summary>The path one level up; <see cref="None"/> for a one-level path or <see cref="None"/>.</summary>
    public TenancyPath Parent
    {
        get
        {
            if (_text is null) return default;
            int last = _text.LastIndexOf(Separator);
            return last < 0 ? default : new TenancyPath(_text[..last]);
        }
    }

    /// <summary>
    /// True when this path is <paramref name="ancestor"/> or lies beneath it (<c>0=acme/1=loja7</c> is within <c>0=acme</c>, not
    /// within <c>0=acme/1=loja</c>). Everything is within <see cref="None"/>. Same rule as YFex.Tenancy's <c>ScopeIdentity.IsWithin</c>.
    /// </summary>
    public bool IsWithin(in TenancyPath ancestor)
    {
        if (ancestor._text is null) return true;
        if (_text is null) return false;
        if (_text.Length == ancestor._text.Length) return string.Equals(_text, ancestor._text, StringComparison.Ordinal);
        return _text.Length > ancestor._text.Length
            && _text[ancestor._text.Length] == Separator
            && _text.AsSpan().StartsWith(ancestor._text, StringComparison.Ordinal);
    }

    /// <summary>The canonical text; empty for <see cref="None"/>.</summary>
    public override string ToString() => _text ?? string.Empty;

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Copies the text without allocating; false when the destination is too short.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        string text = _text ?? string.Empty;
        if (!text.AsSpan().TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }
        charsWritten = text.Length;
        return true;
    }

    /// <summary>Parses the canonical text; null or empty → <see cref="None"/>.</summary>
    public static TenancyPath Parse(string? text, IFormatProvider? provider = null)
        => TryParse(text, out TenancyPath path) ? path : throw new FormatException($"Not a tenancy path: '{text}'.");

    /// <inheritdoc />
    public static TenancyPath Parse(ReadOnlySpan<char> text, IFormatProvider? provider = null)
        => TryParse(text, out TenancyPath path) ? path : throw new FormatException($"Not a tenancy path: '{text.ToString()}'.");

    /// <summary>Parses the canonical text; null or empty → <see cref="None"/>. Reuses <paramref name="text"/> when valid.</summary>
    public static bool TryParse(string? text, out TenancyPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(text)) return true;
        if (!IsValid(text)) return false;
        path = new TenancyPath(text);
        return true;
    }

    /// <summary>Parses the canonical text; empty → <see cref="None"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out TenancyPath path)
    {
        path = default;
        if (text.IsEmpty) return true;
        if (!IsValid(text)) return false;
        path = new TenancyPath(text.ToString());
        return true;
    }

    static bool IParsable<TenancyPath>.TryParse([NotNullWhen(true)] string? text, IFormatProvider? provider, out TenancyPath result)
    {
        result = default;
        return text is not null && TryParse(text, out result);
    }

    static bool ISpanParsable<TenancyPath>.TryParse(ReadOnlySpan<char> text, IFormatProvider? provider, out TenancyPath result)
        => TryParse(text, out result);

    /// <summary>
    /// True when <paramref name="text"/> is a non-empty canonical tenancy path (see the type's grammar). Checks and measures in one
    /// pass, without allocating.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        int bytes = 0, levels = 0, previousRank = -1;
        int i = 0;
        while (true)
        {
            // rank: decimal digits, no leading zeros, 0-255, strictly increasing
            int rankStart = i, rank = 0;
            while (i < text.Length && (uint)(text[i] - '0') <= 9)
            {
                rank = rank * 10 + (text[i] - '0');
                if (rank > 255) return false;
                i++;
            }
            int rankDigits = i - rankStart;
            if (rankDigits == 0 || (rankDigits > 1 && text[rankStart] == '0') || rank <= previousRank) return false;
            if (i >= text.Length || text[i] != '=') return false;
            previousRank = rank;
            bytes += rankDigits + 1;
            i++;

            // value: non-empty, escapes %25/%2F only, no controls, well-formed UTF-16
            int valueStart = i;
            while (i < text.Length && text[i] != Separator)
            {
                char c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= text.Length || text[i + 1] != '2' || (text[i + 2] != '5' && text[i + 2] != 'F')) return false;
                    bytes += 3;
                    i += 3;
                    continue;
                }
                if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)) return false;
                if (c < 0x80) bytes += 1;
                else if (c < 0x800) bytes += 2;
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    bytes += 4;
                    i++;
                }
                else if (char.IsLowSurrogate(c)) return false;
                else bytes += 3;
                i++;
            }
            if (i == valueStart) return false;
            if (++levels > MaxLevels || bytes > MaxLength) return false;
            if (i == text.Length) return true;
            bytes += 1;   // separator
            i++;
            if (i == text.Length) return false;   // trailing '/'
        }
    }

    /// <summary>Upper bound of <see cref="TryWriteCanonical"/>'s output: 2 + <see cref="MaxLength"/> bytes.</summary>
    public static int MaxCanonicalLength => 2 + MaxLength;

    /// <summary>Writes <c>length (LE16) · UTF-8 text</c> without allocating; false when the destination is too short.</summary>
    public static bool TryWriteCanonical(in TenancyPath value, Span<byte> destination, out int written)
    {
        string? text = value._text;
        int length = text is null ? 0 : Encoding.UTF8.GetByteCount(text);
        if (destination.Length < 2 + length)
        {
            written = 0;
            return false;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)length);
        if (text is not null) Encoding.UTF8.GetBytes(text, destination.Slice(2, length));
        written = 2 + length;
        return true;
    }

    /// <summary>
    /// Reads canonical bytes from the start of <paramref name="source"/>; false when they are truncated, not UTF-8 or not a canonical
    /// tenancy path. Allocates the text only.
    /// </summary>
    public static bool TryReadCanonical(ReadOnlySpan<byte> source, out TenancyPath value, out int read)
    {
        value = default;
        read = 0;
        if (source.Length < 2) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(source);
        if (length > MaxLength || source.Length < 2 + length) return false;
        if (length != 0)
        {
            ReadOnlySpan<byte> utf8 = source.Slice(2, length);
            if (!Utf8.IsValid(utf8)) return false;
            string text = Encoding.UTF8.GetString(utf8);
            if (!IsValid(text)) return false;
            value = new TenancyPath(text);
        }
        read = 2 + length;
        return true;
    }

    /// <summary>Logs the text.</summary>
    static void ILogValue<TenancyPath>.Write(in TenancyPath value, ref LogWriter writer, ushort field)
        => writer.Write(field, value._text);
}
