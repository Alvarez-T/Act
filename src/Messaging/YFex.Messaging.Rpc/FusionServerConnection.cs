using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="IServerConnection"/> of one Fusion client peer (default: <see cref="RpcPeerRef.Default"/>, the central server). Follows
/// <c>RpcPeer.ConnectionState</c> with <c>AsyncState.WhenNext()</c> on a background task. Offline's site broker is another peer, so it
/// gets its own instance: <c>new FusionServerConnection(hub, brokerPeerRef)</c>.
/// </summary>
public sealed class FusionServerConnection : IServerConnection, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _stop = new();
    private int _state;   // ServerConnectionState, read with Volatile.Read, written with Interlocked

    /// <summary>Follows the default client peer (the central server).</summary>
    public FusionServerConnection(RpcHub hub) : this(hub, RpcPeerRef.Default) { }

    /// <summary>Follows the client peer <paramref name="peer"/>.</summary>
    public FusionServerConnection(RpcHub hub, RpcPeerRef peer)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(peer);
        RpcClientPeer client = hub.GetClientPeer(peer);
        _state = (int)ToState(client.ConnectionState.Value);
        _ = FollowAsync(client, _stop.Token);
    }

    /// <inheritdoc />
    public ServerConnectionState State => (ServerConnectionState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public bool IsConnected => State == ServerConnectionState.Connected;

    /// <inheritdoc />
    public event Action<ServerConnectionChange>? Changed;

    /// <summary>Stops following the peer.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task FollowAsync(RpcClientPeer peer, CancellationToken cancellationToken)
    {
        // AsyncState<T> is a linked list: WhenNext() waits for the next value.
        var current = peer.ConnectionState;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                current = await current.WhenNext(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;   // hub/peer disposed: nothing more to follow
            }
            catch (Exception)
            {
                // D4 (networking 01 §3): never spin on a repeating error; wait, then follow again from the same state.
                try { await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var next = ToState(current.Value);
            var previous = (ServerConnectionState)Interlocked.Exchange(ref _state, (int)next);
            if (next != previous) Changed?.Invoke(new ServerConnectionChange(previous, next));   // D1: the real previous state
        }
    }

    private static ServerConnectionState ToState(RpcPeerConnectionState state)
    {
        if (state.IsConnected()) return ServerConnectionState.Connected;
        // No error yet = still (re)connecting; an error = the last attempt failed.
        return state.Error is null ? ServerConnectionState.Reconnecting : ServerConnectionState.Disconnected;
    }
}
