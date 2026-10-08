namespace YFex.Messaging.Rpc;

/// <summary>State of our RPC connection to a server (networking ⚑N4).</summary>
public enum ServerConnectionState : byte
{
    /// <summary>Not connected, and the last attempt failed.</summary>
    Disconnected = 0,

    /// <summary>Not connected yet, or connecting again after a drop.</summary>
    Reconnecting = 1,

    /// <summary>Connected: calls go to the server.</summary>
    Connected = 2,
}

/// <summary>A transition of <see cref="IServerConnection.State"/>, with the real previous state.</summary>
public readonly record struct ServerConnectionChange(ServerConnectionState Previous, ServerConnectionState Current);

/// <summary>
/// Is our RPC connection to the server up? (networking ⚑N4: formerly <c>INetworkStatus</c>, removed; the name read as "is there a network".)
/// <para>
/// This says nothing about the machine's network: "no cable", "LAN only", "captive portal" and "internet" are
/// <c>YFex.Networking.INetworkConditions</c> (YFex core). The offline design's <c>IConnectivity</c> composes both. One instance per server
/// peer: the default registration follows the default Fusion client peer (central); another peer (e.g. the site broker) gets its own
/// <see cref="FusionServerConnection"/>.
/// </para>
/// </summary>
public interface IServerConnection
{
    /// <summary>Current state.</summary>
    ServerConnectionState State { get; }

    /// <summary>True when <see cref="State"/> is <see cref="ServerConnectionState.Connected"/>.</summary>
    bool IsConnected => State == ServerConnectionState.Connected;

    /// <summary>Raised on every transition.</summary>
    event Action<ServerConnectionChange>? Changed;
}

/// <summary>Always connected: the server itself and in-process hosts.</summary>
public sealed class AlwaysConnectedServer : IServerConnection
{
    /// <summary>The single instance.</summary>
    public static AlwaysConnectedServer Instance { get; } = new();

    private AlwaysConnectedServer() { }

    /// <inheritdoc />
    public ServerConnectionState State => ServerConnectionState.Connected;

    /// <inheritdoc />
    public bool IsConnected => true;

    /// <inheritdoc />
    public event Action<ServerConnectionChange>? Changed { add { } remove { } }   // never changes
}

/// <summary>Published to <see cref="YFex.Messaging.IEventBus"/> when the connection to the server changes state.</summary>
public sealed record ServerConnectionChangedEvent(ServerConnectionState Previous, ServerConnectionState Current) : YFex.Cqrs.IEvent;
