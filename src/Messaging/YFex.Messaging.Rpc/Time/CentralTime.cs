using ActualLab.Rpc;
using YFex.Time;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The central server's clock over the existing RPC connection (time T2, README §3 "Central anchor"). A plain RPC call — never a
/// compute method: a cached answer would be a wrong time. Clients use it through <see cref="CentralTimeSource"/>
/// (<c>AddYFexTime(time => time.UseCentralAnchor())</c>); the server answers with its trusted clock
/// (<c>AddYFexTime(time => time.TrustDeviceClock())</c> on central).
/// </summary>
/// <remarks>
/// <para>The answer is the explicit wire form of <see cref="TimeReply"/> (<see cref="CentralTimeWire"/>), not the struct itself:
/// MemoryPack copies unmanaged structs as raw memory, whose layout (<see cref="DateTimeOffset"/> internals) is not a contract between a
/// client and a server on different runtimes.</para>
/// <para>The proxy (<c>ActualLabProxies/ICentralTimeProxy.cs</c>) is ActualLab.Generators 13.0.12 output checked in verbatim;
/// <c>ProxyGoldenTests</c> regenerates it from this file.</para>
/// </remarks>
public interface ICentralTime : IRpcService
{
    /// <summary>The server's <see cref="TimeReply"/> in <see cref="CentralTimeWire"/> form (read it with <see cref="CentralTimeWire.Read"/>).</summary>
    Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Server implementation of <see cref="ICentralTime"/> (registered by <c>UseYFexMessagingRpcServer</c>).</summary>
public sealed class CentralTimeService(TimeProvider? time = null) : ICentralTime
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CentralTimeWire.Write(_time.CreateTimeReply(_time.GetTimestamp())));
}

/// <summary>
/// Wire form of <see cref="ICentralTime"/>'s answer: version byte <c>1</c> + the 27-byte <see cref="TimeReply"/> binary form
/// (little-endian ticks received, sent, uncertainty; source; flags; stratum).
/// </summary>
public static class CentralTimeWire
{
    /// <summary>Current layout version.</summary>
    public const byte Version = 1;

    /// <summary>Total size of the wire form.</summary>
    public const int Size = 1 + TimeReply.BinarySize;

    /// <summary>Encodes <paramref name="reply"/>.</summary>
    public static byte[] Write(in TimeReply reply)
    {
        var bytes = new byte[Size];
        bytes[0] = Version;
        reply.TryWriteBytes(bytes.AsSpan(1));
        return bytes;
    }

    /// <summary>Decodes an answer; false for an unknown version, a wrong size or out-of-range values.</summary>
    public static bool Read(ReadOnlySpan<byte> bytes, out TimeReply reply)
    {
        reply = default;
        if (bytes.Length != Size || bytes[0] != Version) return false;
        try
        {
            reply = TimeReply.Read(bytes[1..]);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;                                                     // ticks outside DateTimeOffset's range
        }
        return reply.Uncertainty >= TimeSpan.Zero;
    }
}
