using System.Diagnostics.CodeAnalysis;

namespace YFex.Cryptography;

/// <summary>
/// Identifies <b>one key version</b>: rotating a key creates a new <see cref="KeyId"/> in the same key ring, so a sealed value or a
/// signature names exactly the key that produced it (cryptography ⚑K6).
/// <para>
/// <b>Type only</b> (cryptography ⚑K2): YFex core names keys; the keys themselves and everything that uses them live in the
/// YFex.Cryptography library.
/// </para>
/// <code>
/// var id = KeyId.New();                  // Guid version 7: sortable by creation time
/// Span&lt;byte&gt; bytes = stackalloc byte[KeyId.Size];
/// id.WriteTo(bytes);                     // 16 bytes, big endian (RFC 9562 order, same as the text form)
/// KeyId.Read(bytes) == id;               // true
/// id.ToString("S", null);                // "01927c4e" (display form)
/// </code>
/// </summary>
public readonly record struct KeyId(Guid Value) : ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<KeyId>,
    global::YFex.Diagnostics.ILogValue<KeyId>
{
    /// <summary>Size of the binary form.</summary>
    public const int Size = 16;

    /// <summary>Length of the short display form (format <c>"S"</c>).</summary>
    public const int ShortLength = 8;

    /// <summary>A new identifier (Guid version 7). Not a secret: it contains a timestamp.</summary>
    public static KeyId New() => new(Guid.CreateVersion7());

    /// <summary>True for <see langword="default"/> (no key).</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Writes the 16 bytes in big-endian (RFC 9562) order.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Size"/>.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (!Value.TryWriteBytes(destination, bigEndian: true, out _))
            throw new ArgumentException($"A key id needs {Size} bytes.", nameof(destination));
    }

    /// <summary>Writes the 16 bytes; false when <paramref name="destination"/> is too short.</summary>
    public bool TryWriteBytes(Span<byte> destination) => Value.TryWriteBytes(destination, bigEndian: true, out _);

    /// <summary>Reads the first <see cref="Size"/> bytes (big endian).</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <see cref="Size"/>.</exception>
    public static KeyId Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) throw new ArgumentException($"A key id has {Size} bytes; got {source.Length}.", nameof(source));
        return new KeyId(new Guid(source[..Size], bigEndian: true));
    }

    /// <summary>The Guid text (<c>"D"</c>).</summary>
    public override string ToString() => Value.ToString("D");

    /// <summary>Format <c>null</c>/<c>""</c>/<c>"D"</c> = Guid text; <c>"S"</c> = the first 8 hex characters (display only).</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => IsShort(format) ? Value.ToString("N")[..ShortLength] : ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!IsShort(format)) return Value.TryFormat(destination, out charsWritten, "D");
        Span<char> full = stackalloc char[32];
        Value.TryFormat(full, out _, "N");
        charsWritten = full[..ShortLength].TryCopyTo(destination) ? ShortLength : 0;
        return charsWritten != 0;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!IsShort(format)) return Value.TryFormat(utf8Destination, out bytesWritten, "D");
        Span<byte> full = stackalloc byte[32];
        Value.TryFormat(full, out _, "N");
        bytesWritten = full[..ShortLength].TryCopyTo(utf8Destination) ? ShortLength : 0;
        return bytesWritten != 0;
    }

    public static KeyId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static KeyId Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out KeyId result)
    {
        bool parsed = Guid.TryParse(s, out Guid value);
        result = new KeyId(value);
        return parsed;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out KeyId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }
        return TryParse(s.AsSpan(), provider, out result);
    }

    /// <summary>Logs as its Guid text (formatted on the stack).</summary>
    static void global::YFex.Diagnostics.ILogValue<KeyId>.Write(in KeyId value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
    {
        Span<char> buffer = stackalloc char[36];
        value.Value.TryFormat(buffer, out int written, "D");
        writer.Write(field, buffer[..written]);
    }

    private static bool IsShort(ReadOnlySpan<char> format) => format.Length == 1 && (format[0] == 'S' || format[0] == 's');
}
