using Microsoft.Extensions.DependencyInjection;
using YFex.Time;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Time source "central anchor" (time T2): one <see cref="ICentralTime"/> call over the client's existing RPC connection, measured
/// with the device clock (t1/t4). Authenticated when the connection is TLS (<c>wss</c>) — the server is ours and the channel cannot
/// be forged; mutual TLS comes with the offline design (Phase 2). No answer while the connection is down (the RPC client would
/// otherwise wait for a reconnect).
/// </summary>
public sealed class CentralTimeSource : ITimeSource
{
    private readonly ICentralTime _central;
    private readonly IServerConnection? _server;

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

    /// <inheritdoc />
    public string Name => "central";

    /// <inheritdoc />
    public TimeSourceKind Kind => TimeSourceKind.CentralAnchor;

    /// <inheritdoc />
    public bool IsAuthenticated { get; }

    /// <inheritdoc />
    public async ValueTask<TimeSample?> SampleAsync(TimeProvider device, CancellationToken cancellationToken)
    {
        if (_server is { IsConnected: false }) return null;
        long sent = device.GetTimestamp();
        byte[] answer = await _central.GetTimeAsync(cancellationToken).ConfigureAwait(false);
        long received = device.GetTimestamp();
        return CentralTimeWire.Read(answer, out TimeReply reply) ? new TimeSample(sent, received, reply, TimeSpan.Zero) : null;
    }
}

/// <summary><c>UseCentralAnchor()</c> for <c>AddYFexTime</c>.</summary>
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
}
