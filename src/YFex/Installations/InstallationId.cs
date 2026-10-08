using YFex.Canonical;
using YFex.Diagnostics;

namespace YFex.Installations;

/// <summary>One installed copy of an application; creation and persistence belong to the Installations runtime.</summary>
/// <remarks>Canonical bytes use big-endian Guid order. Text uses the Guid D format.</remarks>
public readonly record struct InstallationId(Guid Value) :
    ICanonicalValue<InstallationId>, ILogValue<InstallationId>,
    ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<InstallationId>, IUtf8SpanParsable<InstallationId>
{
    public const int Size = 16;
    public static int MaxCanonicalLength => Size;
    public static bool IsSearchKey => true;
    public bool IsEmpty => Value == Guid.Empty;

    public static bool TryWriteCanonical(in InstallationId value, Span<byte> destination, out int written)
        => value.Value.TryWriteBytes(destination, bigEndian: true, out written);

    public override string ToString() => Value.ToString("D");
    public string ToString(string? format, IFormatProvider? formatProvider) => Value.ToString(format ?? "D", formatProvider);
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null) => Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "D" : format);
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null) => Value.TryFormat(utf8Destination, out bytesWritten, format.IsEmpty ? "D" : format);

    public static InstallationId Parse(string text, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan(), provider);
    }
    public static InstallationId Parse(ReadOnlySpan<char> text, IFormatProvider? provider = null)
        => TryParse(text, provider, out var value) ? value : throw new FormatException("Expected an installation id in Guid D format.");
    public static InstallationId Parse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider = null)
        => TryParse(utf8Text, provider, out var value) ? value : throw new FormatException("Expected an installation id in Guid D format.");

    public static bool TryParse(string? text, IFormatProvider? provider, out InstallationId result)
        => TryParse(text.AsSpan(), provider, out result);
    public static bool TryParse(ReadOnlySpan<char> text, IFormatProvider? provider, out InstallationId result)
    {
        Guid value = default;
        bool parsed = text.Length == 36 && Guid.TryParseExact(text, "D", out value);
        result = parsed ? new(value) : default;
        return parsed;
    }
    public static bool TryParse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider, out InstallationId result)
    {
        // Fixed canonical text length bounds stack use and rejects non-ASCII input without transcoding allocations.
        result = default;
        if (utf8Text.Length != 36) return false;
        Span<char> text = stackalloc char[36];
        for (int index = 0; index < text.Length; index++)
        {
            if (utf8Text[index] > 127) return false;
            text[index] = (char)utf8Text[index];
        }
        return TryParse(text, provider, out result);
    }

    static void ILogValue<InstallationId>.Write(in InstallationId value, ref LogWriter writer, ushort field)
        => writer.Write(field, value.Value);
}
