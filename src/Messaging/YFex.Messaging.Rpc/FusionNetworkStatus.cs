using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="INetworkStatus"/> backed by ActualLab.Fusion's <see cref="RpcHub"/> peer
/// connection state. Monitors connectivity via <c>AsyncState.WhenNext()</c> on a
/// background task and fires <see cref="Changed"/> on every transition.
/// </summary>
internal sealed class FusionNetworkStatus : INetworkStatus, IDisposable
{
    private volatile SyncState _state = SyncState.Disconnected;
    private readonly CancellationTokenSource _cts = new();

    public FusionNetworkStatus(RpcHub hub)
    {
        var peer = hub.GetClientPeer(RpcPeerRef.Default);
        _state = ToSyncState(peer.ConnectionState.Value);
        // Kick off the background monitor (fire-and-forget; lifetime tied to _cts).
        _ = MonitorAsync(peer, _cts.Token);
    }

    public bool IsConnected => _state == SyncState.Connected;
    public SyncState State  => _state;
    public event Action<SyncState>? Changed;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task MonitorAsync(RpcClientPeer peer, CancellationToken ct)
    {
        // AsyncState<T> forms a linked list — call WhenNext() to await the next value.
        var asyncState = peer.ConnectionState;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                asyncState = await asyncState.WhenNext(ct).ConfigureAwait(false);
                var previous = _state;
                var next = ToSyncState(asyncState.Value);
                _state = next;
                if (next != previous) Changed?.Invoke(next);
            }
            catch (OperationCanceledException) { return; }
            catch { /* transient errors are expected during reconnect */ }
        }
    }

    private static SyncState ToSyncState(RpcPeerConnectionState s)
    {
        // IsConnected is a method in ActualLab.Rpc.Infrastructure.RpcPeerConnectionState
        if (s.IsConnected()) return SyncState.Connected;
        // If there is a last error and it looks like a reconnect attempt, signal Reconnecting.
        // Fall back to Disconnected — the monitor will fire again on the next transition.
        return s.Error is null ? SyncState.Reconnecting : SyncState.Disconnected;
    }
}
