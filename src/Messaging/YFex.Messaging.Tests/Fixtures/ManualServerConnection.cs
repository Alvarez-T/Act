using YFex.Messaging.Rpc;

namespace YFex.Messaging.Tests.Fixtures;

/// <summary>
/// Controllable <see cref="IServerConnection"/> for tests: toggle online/offline at will (events carry the real previous state).
/// </summary>
public sealed class ManualServerConnection : IServerConnection
{
    private ServerConnectionState _state;

    public ManualServerConnection(bool connected = true)
    {
        _state = connected ? ServerConnectionState.Connected : ServerConnectionState.Disconnected;
    }

    public ServerConnectionState State => _state;
    public bool IsConnected => _state == ServerConnectionState.Connected;

    public event Action<ServerConnectionChange>? Changed;

    public void GoOnline() => MoveTo(ServerConnectionState.Connected);

    public void GoOffline() => MoveTo(ServerConnectionState.Disconnected);

    public void StartReconnecting() => MoveTo(ServerConnectionState.Reconnecting);

    private void MoveTo(ServerConnectionState next)
    {
        var previous = _state;
        _state = next;
        if (previous != next) Changed?.Invoke(new ServerConnectionChange(previous, next));
    }
}
