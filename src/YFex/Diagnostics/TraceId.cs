using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace YFex.Diagnostics;

/// <summary>
/// 16-byte W3C trace id: everything caused by one root action, across threads, processes and machines.
/// Stored as two <see cref="ulong"/> in big-endian order (<see cref="High"/> holds the first 8 bytes), so the hex form is
/// <c>High:x16 + Low:x16</c>. <c>default</c> is the empty (invalid) id.
/// </summary>
public readonly struct TraceId : IEquatable<TraceId>, ISpanFormattable
{
    /// <summary>Number of hex characters of the text form.</summary>
    public const int HexLength = 32;

    public TraceId(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>First 8 bytes (big-endian).</summary>
    public ulong High { get; }

    /// <summary>Last 8 bytes (big-endian).</summary>
    public ulong Low { get; }

    /// <summary>True for the all-zero id, which W3C defines as invalid.</summary>
    public bool IsEmpty => (High | Low) == 0;

    /// <summary>A new random, non-empty id from a per-thread generator (no locks, no allocation).</summary>
    public static TraceId NewRandom()
    {
        ulong high, low;
        do
        {
            high = DiagnosticRandom.Next();
            low = DiagnosticRandom.Next();
        }
        while ((high | low) == 0);
        return new TraceId(high, low);
    }

    /// <summary>Writes 32 lower-case hex characters. Returns the number written (0 when <paramref name="destination"/> is too small).</summary>
    public int FormatHex(Span<char> destination)
    {
        if (destination.Length < HexLength) return 0;
        Hex.Write(High, destination);
        Hex.Write(Low, destination[16..]);
        return HexLength;
    }

    /// <summary>Parses exactly 32 hex characters (either case). Fails for the all-zero id.</summary>
    public static bool TryParseHex(ReadOnlySpan<char> text, out TraceId id)
    {
        id = default;
        if (text.Length != HexLength || !Hex.TryRead(text[..16], false, out ulong high) || !Hex.TryRead(text[16..], false, out ulong low))
            return false;
        id = new TraceId(high, low);
        return !id.IsEmpty;
    }

    /// <summary>Writes the 16 bytes (big-endian, W3C byte order).</summary>
    public void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Low);
    }

    /// <summary>Reads 16 bytes (big-endian, W3C byte order).</summary>
    public static TraceId FromBytes(ReadOnlySpan<byte> source)
        => new(BinaryPrimitives.ReadUInt64BigEndian(source), BinaryPrimitives.ReadUInt64BigEndian(source[8..]));

    /// <summary>
    /// Bridge to <see cref="global::System.Diagnostics.ActivityTraceId"/>. Allocates one 32-char string: the BCL type stores its
    /// hex text, so this direction cannot be allocation-free. Use it only where an <c>Activity</c> is created anyway.
    /// </summary>
    public global::System.Diagnostics.ActivityTraceId ToActivity()
    {
        Span<byte> bytes = stackalloc byte[16];
        WriteBytes(bytes);
        return global::System.Diagnostics.ActivityTraceId.CreateFromBytes(bytes);
    }

    /// <summary>Adopts an <see cref="global::System.Diagnostics.ActivityTraceId"/> without allocating.</summary>
    public static TraceId From(global::System.Diagnostics.ActivityTraceId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.CopyTo(bytes);
        return FromBytes(bytes);
    }

    /// <summary>Support code shown to users: the first 8 hex digits as <c>4BF9-2F35</c> (logging 13 §5).</summary>
    public int FormatSupportCode(Span<char> destination)
    {
        if (destination.Length < 9) return 0;
        Span<char> hex = stackalloc char[16];
        Hex.Write(High, hex);
        for (int i = 0; i < 4; i++) destination[i] = char.ToUpperInvariant(hex[i]);
        destination[4] = '-';
        for (int i = 4; i < 8; i++) destination[i + 1] = char.ToUpperInvariant(hex[i]);
        return 9;
    }

    public bool Equals(TraceId other) => High == other.High && Low == other.Low;
    public override bool Equals(object? obj) => obj is TraceId other && Equals(other);
    public override int GetHashCode() => (High ^ Low).GetHashCode();
    public static bool operator ==(TraceId left, TraceId right) => left.Equals(right);
    public static bool operator !=(TraceId left, TraceId right) => !left.Equals(right);

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = FormatHex(destination);
        return charsWritten != 0;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Allocates; for tests and diagnostics.</summary>
    public override string ToString() => string.Create(HexLength, this, static (span, id) => id.FormatHex(span));
}

/// <summary>
/// 8-byte W3C span id: one operation inside a trace. <c>default</c> is the empty (invalid) id, which also means
/// "no parent" in <see cref="TraceContext.ParentOperationId"/>.
/// </summary>
public readonly struct OperationId : IEquatable<OperationId>, ISpanFormattable
{
    /// <summary>Number of hex characters of the text form.</summary>
    public const int HexLength = 16;

    public OperationId(ulong value) => Value = value;

    /// <summary>The 8 bytes as a big-endian number.</summary>
    public ulong Value { get; }

    /// <summary>True for the all-zero id.</summary>
    public bool IsEmpty => Value == 0;

    /// <summary>A new random, non-empty id from a per-thread generator.</summary>
    public static OperationId NewRandom()
    {
        ulong value;
        do value = DiagnosticRandom.Next();
        while (value == 0);
        return new OperationId(value);
    }

    /// <summary>Writes 16 lower-case hex characters. Returns the number written (0 when too small).</summary>
    public int FormatHex(Span<char> destination)
    {
        if (destination.Length < HexLength) return 0;
        Hex.Write(Value, destination);
        return HexLength;
    }

    /// <summary>Parses exactly 16 hex characters (either case). Fails for the all-zero id.</summary>
    public static bool TryParseHex(ReadOnlySpan<char> text, out OperationId id)
    {
        id = default;
        if (text.Length != HexLength || !Hex.TryRead(text, false, out ulong value)) return false;
        id = new OperationId(value);
        return value != 0;
    }

    public void WriteBytes(Span<byte> destination) => BinaryPrimitives.WriteUInt64BigEndian(destination, Value);
    public static OperationId FromBytes(ReadOnlySpan<byte> source) => new(BinaryPrimitives.ReadUInt64BigEndian(source));

    /// <summary>Bridge to <see cref="global::System.Diagnostics.ActivitySpanId"/> (allocates its 16-char text, see <see cref="TraceId.ToActivity"/>).</summary>
    public global::System.Diagnostics.ActivitySpanId ToActivity()
    {
        Span<byte> bytes = stackalloc byte[8];
        WriteBytes(bytes);
        return global::System.Diagnostics.ActivitySpanId.CreateFromBytes(bytes);
    }

    /// <summary>Adopts an <see cref="global::System.Diagnostics.ActivitySpanId"/> without allocating.</summary>
    public static OperationId From(global::System.Diagnostics.ActivitySpanId id)
    {
        Span<byte> bytes = stackalloc byte[8];
        id.CopyTo(bytes);
        return FromBytes(bytes);
    }

    public bool Equals(OperationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is OperationId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(OperationId left, OperationId right) => left.Value == right.Value;
    public static bool operator !=(OperationId left, OperationId right) => left.Value != right.Value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = FormatHex(destination);
        return charsWritten != 0;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Allocates; for tests and diagnostics.</summary>
    public override string ToString() => string.Create(HexLength, this, static (span, id) => id.FormatHex(span));
}

/// <summary>Lower-case hex helpers for ids (W3C requires lower case on the wire).</summary>
internal static class Hex
{
    private const string Digits = "0123456789abcdef";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(ulong value, Span<char> destination)
    {
        for (int i = 15; i >= 0; i--)
        {
            destination[i] = Digits[(int)(value & 0xF)];
            value >>= 4;
        }
    }

    public static void Write(byte value, Span<char> destination)
    {
        destination[0] = Digits[value >> 4];
        destination[1] = Digits[value & 0xF];
    }

    /// <summary>Reads up to 16 hex digits. <paramref name="lowerCaseOnly"/> enforces the W3C <c>traceparent</c> rule.</summary>
    public static bool TryRead(ReadOnlySpan<char> text, bool lowerCaseOnly, out ulong value)
    {
        value = 0;
        if (text.Length is 0 or > 16) return false;
        foreach (char c in text)
        {
            int digit;
            if (c is >= '0' and <= '9') digit = c - '0';
            else if (c is >= 'a' and <= 'f') digit = c - 'a' + 10;
            else if (!lowerCaseOnly && c is >= 'A' and <= 'F') digit = c - 'A' + 10;
            else return false;
            value = (value << 4) | (uint)digit;
        }
        return true;
    }
}

/// <summary>
/// Per-thread xoshiro256** generator for trace and operation ids: no locks, one thread-local access per number, no allocation
/// after the first call on a thread. Not cryptographic (ids only need to be unique and unpredictable enough not to collide).
/// </summary>
internal static class DiagnosticRandom
{
    private sealed class State
    {
        public ulong S0, S1, S2, S3;
    }

    [ThreadStatic] private static State? t_state;

    public static ulong Next()
    {
        State state = t_state ?? CreateState();
        ulong s0 = state.S0, s1 = state.S1, s2 = state.S2, s3 = state.S3;

        ulong result = ulong.RotateLeft(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = ulong.RotateLeft(s3, 45);

        state.S0 = s0;
        state.S1 = s1;
        state.S2 = s2;
        state.S3 = s3;
        return result;
    }

    private static State CreateState()
    {
        // One cryptographic seed per thread (cold path), expanded with SplitMix64.
        Span<byte> seed = stackalloc byte[8];
        global::System.Security.Cryptography.RandomNumberGenerator.Fill(seed);
        ulong x = BinaryPrimitives.ReadUInt64LittleEndian(seed) ^ (ulong)Environment.CurrentManagedThreadId;
        var state = new State { S0 = SplitMix(ref x), S1 = SplitMix(ref x), S2 = SplitMix(ref x), S3 = SplitMix(ref x) };
        if ((state.S0 | state.S1 | state.S2 | state.S3) == 0) state.S0 = 1;
        t_state = state;
        return state;
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
