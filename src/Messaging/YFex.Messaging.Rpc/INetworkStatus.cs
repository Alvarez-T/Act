namespace YFex.Messaging.Rpc;

public enum SyncState { Connected, Disconnected, Reconnecting, Syncing }

/// <summary>
/// Represents the current connectivity state. Default implementation is
/// <see cref="AlwaysConnectedNetworkStatus"/> (for in-process or server scenarios).
/// The Fusion-backed implementation projects from <c>RpcPeer.ConnectionState</c>.
/// </summary>
public interface INetworkStatus
{
    bool IsConnected { get; }
    SyncState State { get; }
    event Action<SyncState>? Changed;
}

/// <summary>Constant-connected sentinel used on the server or in in-process scenarios.</summary>
public sealed class AlwaysConnectedNetworkStatus : INetworkStatus
{
    public static readonly AlwaysConnectedNetworkStatus Instance = new();

    private AlwaysConnectedNetworkStatus() { }

    public bool IsConnected => true;
    public SyncState State => SyncState.Connected;

    // Never fires — server-side is always "connected" from its own perspective.
    public event Action<SyncState>? Changed { add { } remove { } }
}

/// <summary>
/// Ambient accessor for the registered <see cref="INetworkStatus"/>.
/// Falls back to <see cref="AlwaysConnectedNetworkStatus"/> before DI is configured.
/// </summary>
public static class NetworkStatusProvider
{
    private static INetworkStatus? _current;

    public static INetworkStatus Current => _current ?? AlwaysConnectedNetworkStatus.Instance;

    public static void Configure(INetworkStatus status) => _current = status;

    public static void Reset() => _current = null;
}
