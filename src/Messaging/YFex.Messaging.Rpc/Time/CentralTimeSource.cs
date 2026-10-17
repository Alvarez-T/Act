using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cryptography;
using YFex.Time;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Time source "central anchor" (time T2): one <see cref="ICentralTime"/> call over the client's existing RPC connection, measured
/// with the device clock (t1/t4). Authenticated when the connection is TLS (<c>wss</c>) — the server is ours and the channel cannot
/// be forged; mutual TLS comes with the offline design (Phase 2). No answer while the connection is down (the RPC client would
/// otherwise wait for a reconnect).
/// <para><b>Signed</b> (time T4, with a <see cref="SignatureVerifier"/> over central's public keys): every round sends a fresh nonce
/// and accepts only a reply signed over it by central (<see cref="SignedTimeReply"/>), so the answer is authenticated whatever the
/// transport, and <see cref="LastSignedReply"/> can be kept as evidence. Unsigned, unknown-key or invalid answers are no sample.</para>
/// </summary>
public sealed class CentralTimeSource : ITimeSource
{
    private const int NonceLength = 32;

    private readonly ICentralTime _central;
    private readonly IServerConnection? _server;
    private readonly SignatureVerifier? _verifier;
    private SignedTimeReply? _lastSignedReply;

    /// <summary>
    /// Creates the source over the client proxy of <see cref="ICentralTime"/>. <paramref name="server"/>: no sample unless it is
    /// <see cref="ServerConnectionState.Connected"/> (also not while reconnecting); null = always try.
    /// </summary>
    public CentralTimeSource(ICentralTime central, bool authenticated, IServerConnection? server = null)
    {
        ArgumentNullException.ThrowIfNull(central);
        _central = central;
        _server = server;
        IsAuthenticated = authenticated;
    }

    /// <summary>
    /// Creates a <b>signed</b> source: only replies signed by a key <paramref name="centralKeys"/> verifies count (always
    /// authenticated).
    /// </summary>
    public CentralTimeSource(ICentralTime central, SignatureVerifier centralKeys, IServerConnection? server = null)
        : this(central, authenticated: true, server)
    {
        ArgumentNullException.ThrowIfNull(centralKeys);
        _verifier = centralKeys;
    }

    /// <inheritdoc />
    public string Name => "central";

    /// <inheritdoc />
    public TimeSourceKind Kind => TimeSourceKind.CentralAnchor;

    /// <inheritdoc />
    public bool IsAuthenticated { get; }

    /// <summary>True when only signed replies count.</summary>
    public bool RequiresSignature => _verifier is not null;

    /// <summary>The last reply that passed the signature check (evidence: store its <see cref="SignedTimeReply.Encode"/>).</summary>
    public SignedTimeReply? LastSignedReply => Volatile.Read(ref _lastSignedReply);

    /// <inheritdoc />
    public async ValueTask<TimeSample?> SampleAsync(TimeProvider device, CancellationToken cancellationToken)
    {
        if (_server is { IsConnected: false }) return null;
        if (_verifier is not null) return await SampleSignedAsync(device, _verifier, cancellationToken).ConfigureAwait(false);
        long sent = device.GetTimestamp();
        byte[] answer = await _central.GetTimeAsync(cancellationToken).ConfigureAwait(false);
        long received = device.GetTimestamp();
        return CentralTimeWire.Read(answer, out TimeReply reply) ? new TimeSample(sent, received, reply, TimeSpan.Zero) : null;
    }

    private async ValueTask<TimeSample?> SampleSignedAsync(TimeProvider device, SignatureVerifier verifier, CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        long sent = device.GetTimestamp();
        byte[] answer = await _central.GetSignedTimeAsync(nonce, cancellationToken).ConfigureAwait(false);
        long received = device.GetTimestamp();
        if (!SignedTimeReply.TryDecode(answer, out SignedTimeReply signed)
            || !CryptographicOperations.FixedTimeEquals(signed.Nonce.Span, nonce)
            || signed.Verify(verifier) != SignatureCheck.Valid)
            return null;
        Volatile.Write(ref _lastSignedReply, signed);
        return new TimeSample(sent, received, signed.Reply, TimeSpan.Zero);
    }
}

/// <summary><c>UseCentralAnchor()</c> and <c>UseSignedCentralAnchor()</c> for <c>AddYFexTime</c>.</summary>
public static class CentralTimeBuilderExtensions
{
    /// <summary>
    /// Anchors the trusted clock to the central server over the RPC connection registered by <c>AddYFexMessagingRpcClient</c>.
    /// <paramref name="authenticated"/>: null = true when the endpoint is <c>wss</c>/<c>https</c> (TLS to our own server).
    /// <code>
    /// services.AddYFexMessagingRpcClient(o => o.WebSocketEndpoint = new("wss://api.farma.com.br"));
    /// services.AddYFexTime(time => time.UseCentralAnchor().UseNetworkTime("a.ntp.br", "b.ntp.br").PersistHighWater());
    /// </code>
    /// </summary>
    public static TimeBuilder UseCentralAnchor(this TimeBuilder builder, bool? authenticated = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseSource(sp =>
        {
            bool secure = authenticated ?? sp.GetService<YFexMessagingRpcClientOptions>()?.WebSocketEndpoint is { } endpoint
                && (endpoint.Scheme == Uri.UriSchemeWss || endpoint.Scheme == Uri.UriSchemeHttps);
            return new CentralTimeSource(sp.GetRequiredService<ICentralTime>(), secure, sp.GetService<IServerConnection>());
        });
    }

    /// <summary>
    /// Anchors the trusted clock to <b>signed</b> central replies (time T4): <paramref name="centralKeys"/> returns the directory of
    /// central's time-signing public keys (central: <c>AddCentralTimeSigning()</c>; the app publishes the ring's public keys to its
    /// clients). Use instead of <see cref="UseCentralAnchor"/>, not with it.
    /// <code>
    /// services.AddYFexTime(time => time.UseSignedCentralAnchor(sp => sp.GetRequiredService&lt;TenantKeys&gt;().Directory));
    /// </code>
    /// </summary>
    public static TimeBuilder UseSignedCentralAnchor(this TimeBuilder builder, Func<IServiceProvider, IPublicKeyDirectory> centralKeys)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(centralKeys);
        return builder.UseSource(sp => new CentralTimeSource(sp.GetRequiredService<ICentralTime>(),
            new SignatureVerifier(centralKeys(sp), [SignatureAlgorithm.EcdsaP256Sha256]), sp.GetService<IServerConnection>()));
    }
}
