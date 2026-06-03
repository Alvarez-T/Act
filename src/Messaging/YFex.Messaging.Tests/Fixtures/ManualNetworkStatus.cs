using YFex.Messaging.Rpc;

namespace YFex.Messaging.Tests.Fixtures;

/// <summary>
/// Controllable <see cref="INetworkStatus"/> for tests: toggle online/offline at will.
/// </summary>
public sealed class ManualNetworkStatus : INetworkStatus
{
    private SyncState _state;

    public ManualNetworkStatus(bool connected = true)
    {
        _state = connected ? SyncState.Connected : SyncState.Disconnected;
    }

    public bool IsConnected => _state == SyncState.Connected;
    public SyncState State => _state;

    public event Action<SyncState>? Changed;

    public void GoOnline()
    {
        _state = SyncState.Connected;
        Changed?.Invoke(_state);
    }

    public void GoOffline()
    {
        _state = SyncState.Disconnected;
        Changed?.Invoke(_state);
    }
}
