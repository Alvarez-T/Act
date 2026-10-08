using System.Buffers.Binary;

namespace YFex.Time;

/// <summary>
/// What a time server answers: central in its health reply, the site broker on the LAN, a peer in its beacon (time README §2.1, §8).
/// Wire-neutral so RPC and offline contracts can carry it without the YFex.Time library; servers build it with the library's
/// <c>TrustedTimeProvider.CreateReply</c>.
/// <para><b>Binary form</b> (<see cref="BinarySize"/> bytes, little-endian): UTC ticks of <see cref="Received"/>, of
/// <see cref="Sent"/>, ticks of <see cref="Uncertainty"/>, <see cref="Source"/>, <see cref="Flags"/>, <see cref="Stratum"/>.</para>
/// </summary>
/// <param name="Received">The server's trusted time when the request arrived (NTP t2).</param>
/// <param name="Sent">The server's trusted time when the reply left (NTP t3).</param>
/// <param name="Uncertainty">The server's own ± at that moment (its anchor's round trip / 2 + drift); added to the client's.</param>
/// <param name="Source">What anchors the server's clock.</param>
/// <param name="Flags">The server's quality flags; the client inherits them (an unanchored broker cannot anchor a terminal).</param>
/// <param name="Stratum">NTP-style distance from a reference: 1 = reference, each hop + 1, <see cref="Unsynchronized"/> = none.</param>
public readonly record struct TimeReply(DateTimeOffset Received, DateTimeOffset Sent, TimeSpan Uncertainty, TimeSourceKind Source,
    TimeFlags Flags, byte Stratum)
{
    /// <summary>Size of the binary form.</summary>
    public const int BinarySize = 27;

    /// <summary>Stratum of a server that has no anchor (NTP's "unsynchronized").</summary>
    public const byte Unsynchronized = 16;

    /// <summary>Writes the binary form; false when <paramref name="destination"/> is shorter than <see cref="BinarySize"/>.</summary>
    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < BinarySize) return false;
        BinaryPrimitives.WriteInt64LittleEndian(destination, Received.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Sent.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], Uncertainty.Ticks);
        destination[24] = (byte)Source;
        destination[25] = (byte)Flags;
        destination[26] = Stratum;
        return true;
    }

    /// <summary>Reads the binary form (times come back in UTC).</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <see cref="BinarySize"/>.</exception>
    public static TimeReply Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < BinarySize)
            throw new ArgumentException($"A time reply has {BinarySize} bytes; got {source.Length}.", nameof(source));
        return new TimeReply(
            new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(source), TimeSpan.Zero),
            new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(source[8..]), TimeSpan.Zero),
            TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source[16..])),
            (TimeSourceKind)source[24],
            (TimeFlags)source[25],
            source[26]);
    }
}
