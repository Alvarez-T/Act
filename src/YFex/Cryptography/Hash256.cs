using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace YFex.Cryptography;

/// <summary>
/// A SHA-256 digest as a value: 32 bytes stored inline (no array), safe to persist, compare and use as a dictionary key.
/// <para>
/// <b>Type only.</b> YFex core holds the types other contracts must name, never the cryptographic logic (cryptography ⚑K2): this
/// struct does not hash anything. Code that may reference <c>YFex.Cryptography</c> computes digests there; leaf contracts that may
/// not (YFex.Rules, YFex.Audit.Public, YFex.Authorization.Public) call the BCL one-shot directly and wrap the result:
/// </para>
/// <code>
/// Span&lt;byte&gt; bytes = stackalloc byte[Hash256.Size];
/// SHA256.HashData(content, bytes);
/// var digest = new Hash256(bytes);
/// digest.ToString();          // "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
/// digest.ToString("S", null); // "9f86d081884c7d65" (display form)
/// </code>
/// <para>Text form = 64 lower-case hex characters (parsing accepts either case). <see langword="default"/> = 32 zero bytes
/// (<see cref="Zero"/>, e.g. the "no previous hash" of a chain genesis). <c>==</c> is ordinary equality; use
/// <see cref="FixedTimeEquals"/> when one side is secret or attacker-controlled and the comparison decides access.</para>
/// </summary>
public readonly struct Hash256 :
    IEquatable<Hash256>,
    ISpanFormattable,
    IUtf8SpanFormattable,
    ISpanParsable<Hash256>,
    global::YFex.Diagnostics.ILogValue<Hash256>
{
    /// <summary>Digest size in bytes.</summary>
    public const int Size = 32;

    /// <summary>Length of the text form (lower-case hex).</summary>
    public const int HexLength = Size * 2;

    /// <summary>Length of the short display form (format <c>"S"</c>): the first 8 bytes in hex.</summary>
    public const int ShortHexLength = 16;

    [InlineArray(Size)]
    private struct Storage
    {
        private byte _element0;
    }

    private readonly Storage _bytes;

    /// <summary>Copies a 32-byte digest.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly <see cref="Size"/> bytes.</exception>
    public Hash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A SHA-256 digest has {Size} bytes; got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(_bytes);
    }

    /// <summary>32 zero bytes (the <see langword="default"/> value).</summary>
    public static Hash256 Zero => default;

    /// <summary>True for 32 zero bytes.</summary>
    public bool IsZero => AsSpan().IndexOfAnyExcept((byte)0) < 0;

    /// <summary>Reads exactly <see cref="Size"/> bytes.</summary>
    public static Hash256 Read(ReadOnlySpan<byte> source) => new(source);

    /// <summary>Reads the first <see cref="Size"/> bytes of <paramref name="source"/>; false when it is shorter.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out Hash256 value)
    {
        if (source.Length < Size)
        {
            value = default;
            return false;
        }
        value = new Hash256(source[..Size]);
        return true;
    }

    /// <summary>The 32 bytes (a view over this value; no copy).</summary>
    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>Writes the 32 bytes.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Size"/>.</exception>
    public void WriteTo(Span<byte> destination) => AsSpan().CopyTo(destination);

    /// <summary>Writes the 32 bytes; false when <paramref name="destination"/> is too short.</summary>
    public bool TryWriteBytes(Span<byte> destination) => AsSpan().TryCopyTo(destination);

    /// <summary>Constant-time comparison (for digests that decide access: tokens, verifiers, bindings).</summary>
    public bool FixedTimeEquals(in Hash256 other) => CryptographicOperations.FixedTimeEquals(AsSpan(), other.AsSpan());

    // ── Equality ────────────────────────────────────────────────────────────────

    public bool Equals(Hash256 other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Hash256 other && Equals(other);

    /// <summary>The first 4 bytes: digests are uniformly distributed, so no further mixing is needed.</summary>
    public override int GetHashCode() => MemoryMarshal.Read<int>(AsSpan());

    public static bool operator ==(Hash256 left, Hash256 right) => left.Equals(right);

    public static bool operator !=(Hash256 left, Hash256 right) => !left.Equals(right);

    // ── Text ────────────────────────────────────────────────────────────────────

    /// <summary>64 lower-case hex characters.</summary>
    public override string ToString() => Convert.ToHexStringLower(AsSpan());

    /// <summary>Format <c>null</c>/<c>""</c>/<c>"F"</c> = 64 hex characters; <c>"S"</c> = the first 16 (display only).</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => IsShort(format) ? Convert.ToHexStringLower(AsSpan()[..(ShortHexLength / 2)]) : ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => Convert.TryToHexStringLower(IsShort(format) ? AsSpan()[..(ShortHexLength / 2)] : AsSpan(), destination, out charsWritten);

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => Convert.TryToHexStringLower(IsShort(format) ? AsSpan()[..(ShortHexLength / 2)] : AsSpan(), utf8Destination, out bytesWritten);

    /// <summary>Parses 64 hex characters (either case).</summary>
    public static Hash256 Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, provider, out Hash256 value) ? value : throw new FormatException("A SHA-256 digest is 64 hex characters.");

    public static Hash256 Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Hash256 result)
    {
        result = default;
        if (s.Length != HexLength) return false;
        Span<byte> bytes = stackalloc byte[Size];
        if (Convert.FromHexString(s, bytes, out _, out int written) != OperationStatus.Done || written != Size) return false;
        result = new Hash256(bytes);
        return true;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Hash256 result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }
        return TryParse(s.AsSpan(), provider, out result);
    }

    /// <summary>Logs as its 64-character hex text (formatted on the stack).</summary>
    static void global::YFex.Diagnostics.ILogValue<Hash256>.Write(in Hash256 value, ref global::YFex.Diagnostics.LogWriter writer, ushort field)
    {
        Span<char> buffer = stackalloc char[HexLength];
        Convert.TryToHexStringLower(value.AsSpan(), buffer, out int written);
        writer.Write(field, buffer[..written]);
    }

    private static bool IsShort(ReadOnlySpan<char> format) => format.Length == 1 && (format[0] == 'S' || format[0] == 's');
}
