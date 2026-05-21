namespace YFex.Messaging.Rpc;

/// <summary>
/// RPC service interface for cross-process event routing.
/// <para>
/// Mark this interface with <c>[RpcService]</c> (from ActualLab.Rpc) at
/// the registration site, then register as:
/// </para>
/// <list type="bullet">
///   <item><b>Server:</b> <c>services.AddRpc().AddServer&lt;IRemoteEventBus, RemoteEventBusServer&gt;()</c></item>
///   <item><b>Client:</b> <c>services.AddRpc().AddClient&lt;IRemoteEventBus&gt;()</c></item>
/// </list>
/// The <see cref="RpcEventBus"/> uses the injected proxy to forward events to the server.
/// </summary>
public interface IRemoteEventBus
{
    /// <summary>
    /// Publishes an envelope to the server, which routes it to the correct
    /// subscribers on other connected clients.
    /// </summary>
    Task PublishAsync(RpcEventEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Opens a server-push stream. The server calls this implicitly to push
    /// events targeted at the calling client's connection.
    /// Each item is a batch of events; the client deserializes and routes locally.
    /// </summary>
    IAsyncEnumerable<RpcEventEnvelope> SubscribeToServerEventsAsync(
        CancellationToken ct = default);
}
