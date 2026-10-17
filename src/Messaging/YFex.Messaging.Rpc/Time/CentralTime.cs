using System.Buffers;
using ActualLab.Rpc;
using Microsoft.Extensions.DependencyInjection;
using YFex.Canonical;
using YFex.Cryptography;
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

    /// <summary>
    /// The server's <see cref="TimeReply"/> signed over the caller's <paramref name="nonce"/> (time T4, <see cref="SignedTimeReply"/>
    /// form), or an empty array when the server has no signing key (<c>AddCentralTimeSigning()</c>).
    /// </summary>
    Task<byte[]> GetSignedTimeAsync(byte[] nonce, CancellationToken cancellationToken = default);
}

/// <summary>Server implementation of <see cref="ICentralTime"/> (registered by <c>UseYFexMessagingRpcServer</c>).</summary>
public sealed class CentralTimeService(TimeProvider? time = null, CentralTimeSigner? signer = null) : ICentralTime
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<byte[]> GetTimeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CentralTimeWire.Write(_time.CreateTimeReply(_time.GetTimestamp())));

    /// <inheritdoc />
    public async Task<byte[]> GetSignedTimeAsync(byte[] nonce, CancellationToken cancellationToken = default)
    {
        long received = _time.GetTimestamp();
        if (signer is null || !SignedTimeReply.IsValidNonce(nonce)) return [];
        ISigningKey key = await signer.GetKeyAsync(cancellationToken).ConfigureAwait(false);
        return SignedTimeReply.Sign(key, nonce, _time.CreateTimeReply(received)).Encode();
    }
}

/// <summary>
/// The key central signs time replies with (time T4, signed central anchor). Default: the current key of a YFex.Cryptography signing
/// ring (<see cref="DefaultRing"/>); clients verify with the ring's public keys, which the app publishes to them (the tenant key
/// directory of YFex.Installations, a manifest…).
/// <code>
/// services.AddYFexCryptography(c => c.UseRootKey(...).StoreKeyRingsIn(...));
/// services.UseYFexMessagingRpcServer().AddCentralTimeSigning();
/// </code>
/// </summary>
public sealed class CentralTimeSigner
{
    /// <summary>The default signing ring.</summary>
    public const string DefaultRing = "time/central-signing";

    private readonly Func<CancellationToken, ValueTask<ISigningKey>> _key;

    /// <summary>Signs with the key <paramref name="key"/> returns (a CNG/TPM key, a ring's current key…).</summary>
    public CentralTimeSigner(Func<CancellationToken, ValueTask<ISigningKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    /// <summary>Signs with the current key of <paramref name="ring"/> (rotation is the ring's).</summary>
    public static CentralTimeSigner FromKeyRing(KeyRingProvider rings, string ring = DefaultRing)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var name = new KeyRingName(ring);
        return new CentralTimeSigner(async cancellationToken => (await rings.GetSigningKeysAsync(name, cancellationToken).ConfigureAwait(false)).Current);
    }

    /// <summary>The key to sign the next reply with.</summary>
    public ValueTask<ISigningKey> GetKeyAsync(CancellationToken cancellationToken) => _key(cancellationToken);
}

/// <summary>
/// Central's <see cref="TimeReply"/> signed over the client's nonce (time T4, signed central anchor). Proves the time came from central
/// whatever the transport (TLS terminated by a proxy, relayed by the site broker) and can be kept as evidence (offline envelopes,
/// audit): it carries its nonce, so anyone with central's public keys can check it later.
/// <para><b>Wire form</b> (canonical encoding, cryptography 05): tag <see cref="FormatVersion"/> · bytes nonce · bytes
/// <see cref="TimeReply"/> (27-byte form) · signature. The signature covers <c>SHA-256(</c><see cref="Domain"/><c> ‖ 0x00 ‖ tag ·
/// nonce · reply)</c> and is checked as of <see cref="TimeReply.Sent"/>.</para>
/// </summary>
public sealed record SignedTimeReply(ReadOnlyMemory<byte> Nonce, TimeReply Reply, DetachedSignature Signature)
{
    /// <summary>Domain separation of the signature.</summary>
    public static MessageDomain Domain { get; } = new("yfex.time.central-reply.v1");

    /// <summary>Wire form version (the unsigned <see cref="CentralTimeWire"/> is version 1).</summary>
    public const byte FormatVersion = 2;

    /// <summary>Shortest nonce accepted (128 bits).</summary>
    public const int MinimumNonceLength = 16;

    /// <summary>Longest nonce accepted.</summary>
    public const int MaximumNonceLength = 64;

    /// <summary>True when <paramref name="nonce"/> has an acceptable length.</summary>
    public static bool IsValidNonce(ReadOnlySpan<byte> nonce) => nonce.Length is >= MinimumNonceLength and <= MaximumNonceLength;

    /// <summary>Signs <paramref name="reply"/> for <paramref name="nonce"/> with <paramref name="key"/>.</summary>
    public static SignedTimeReply Sign(ISigningKey key, ReadOnlySpan<byte> nonce, in TimeReply reply)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsValidNonce(nonce)) throw new ArgumentException("Expected a nonce of 16 to 64 bytes.", nameof(nonce));
        byte[] nonceCopy = nonce.ToArray();
        DetachedSignature signature = SignedPayload.Sign(key, Domain, Payload(nonceCopy, reply));
        return new SignedTimeReply(nonceCopy, reply, signature);
    }

    /// <summary>The wire form.</summary>
    public byte[] Encode()
    {
        var buffer = new ArrayBufferWriter<byte>(160);
        var writer = new CanonicalWriter(buffer);
        WriteSigned(ref writer, Nonce.Span, Reply);
        writer.WriteSignature(Signature);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the wire form; false for anything malformed or non-canonical (the signature is NOT checked here).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out SignedTimeReply reply)
    {
        reply = null!;
        try
        {
            var reader = new CanonicalReader(bytes);
            if (reader.ReadTag() != FormatVersion) return false;
            byte[] nonce = reader.ReadBytes().ToArray();
            ReadOnlySpan<byte> time = reader.ReadBytes();
            DetachedSignature signature = reader.ReadSignature();
            if (!reader.End || !IsValidNonce(nonce) || time.Length != TimeReply.BinarySize) return false;
            TimeReply value = TimeReply.Read(time);
            if (value.Uncertainty < TimeSpan.Zero) return false;
            reply = new SignedTimeReply(nonce, value, signature);
            return true;
        }
        catch (Exception exception) when (exception is CanonicalFormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Checks the signature with central's public keys, as of the time the reply claims.</summary>
    public SignatureCheck Verify(SignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return SignedPayload.Verify(verifier, Domain, Payload(Nonce.Span, Reply), Signature, Reply.Sent);
    }

    private static byte[] Payload(ReadOnlySpan<byte> nonce, in TimeReply reply)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        var writer = new CanonicalWriter(buffer);
        WriteSigned(ref writer, nonce, reply);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSigned(ref CanonicalWriter writer, ReadOnlySpan<byte> nonce, in TimeReply reply)
    {
        Span<byte> time = stackalloc byte[TimeReply.BinarySize];
        reply.TryWriteBytes(time);
        writer.WriteTag(FormatVersion);
        writer.WriteBytes(nonce);
        writer.WriteBytes(time);
    }
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

/// <summary><c>AddCentralTimeSigning()</c>: central signs time replies (time T4).</summary>
public static class CentralTimeSigningExtensions
{
    /// <summary>
    /// Central signs <see cref="ICentralTime.GetSignedTimeAsync"/> replies with the current key of <paramref name="ring"/> (P-256
    /// signing keys, yearly rotation, new keys propagated to every node for 10 minutes before use). Clients verify with the ring's
    /// public keys (<c>UseSignedCentralAnchor(...)</c>). Call next to <c>UseYFexMessagingRpcServer</c>; the app's
    /// <c>AddYFexCryptography</c> chooses the root key and ring store.
    /// </summary>
    public static IServiceCollection AddCentralTimeSigning(this IServiceCollection services, string ring = CentralTimeSigner.DefaultRing)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddYFexCryptography(cryptography => cryptography.KeyRing(ring, keys => keys
            .For(KeyPurpose.Signing).RotateEvery(TimeSpan.FromDays(365)).PropagateFor(TimeSpan.FromMinutes(10))));
        services.AddSingleton(sp => CentralTimeSigner.FromKeyRing(sp.GetRequiredService<KeyRingProvider>(), ring));
        return services;
    }
}
