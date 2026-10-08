namespace YFex.Networking;

// Machine-level network conditions (networking ⚑N10): TYPES ONLY. The monitor lives in the sibling repo C:\Dev\YFex\YFex.Networking.
// "Is our RPC link to the server up?" is a different question: Messaging.Rpc IServerConnection (⚑N4).

/// <summary>What this machine can reach, from no network at all up to the internet.</summary>
public enum InternetReachability : byte
{
    /// <summary>Not measured yet (no monitor registered, or the first check has not finished).</summary>
    Unknown = 0,

    /// <summary>No adapter is up with a route: cable unplugged, Wi-Fi off, airplane mode.</summary>
    NoNetwork = 1,

    /// <summary>The local network works (gateway/LAN reachable) but the internet does not.</summary>
    LocalNetworkOnly = 2,

    /// <summary>A captive portal intercepts web traffic (hotel/guest Wi-Fi asking for a login).</summary>
    CaptivePortal = 3,

    /// <summary>The internet answers.</summary>
    Internet = 4,
}

/// <summary>Physical medium of the primary route.</summary>
public enum NetworkMedium : byte
{
    Unknown = 0,
    Ethernet = 1,
    WiFi = 2,
    /// <summary>Mobile broadband: 4G/5G dongle, tethered phone, built-in modem.</summary>
    Cellular = 3,
    Other = 4,
}

/// <summary>What the link costs. Ordered: <c>cost &gt;= NetworkCost.Metered</c> means "avoid large transfers".</summary>
public enum NetworkCost : byte
{
    Unknown = 0,
    Unrestricted = 1,
    Metered = 2,
    OverDataLimit = 3,
}

/// <summary>Extra facts about the current conditions.</summary>
[Flags]
public enum NetworkConditionFlags : byte
{
    None = 0,
    /// <summary>The primary route goes through a VPN/tunnel adapter (heuristic).</summary>
    VirtualPrivateNetwork = 1,
    /// <summary>Web traffic goes through a proxy.</summary>
    Proxy = 2,
    Roaming = 4,
    ApproachingDataLimit = 8,
    /// <summary>More than one adapter has a default route (e.g. Ethernet + 4G dongle).</summary>
    MultipleRoutes = 16,
    /// <summary>Reachable but slow or lossy (link quality Poor or worse).</summary>
    Degraded = 32,
}

/// <summary>Health of one endpoint (central, site broker, SEFAZ, a printer). Shared with YFex.Offline (replaces its planned PeerHealth).</summary>
public enum EndpointHealth : byte
{
    Unknown = 0,
    Healthy = 1,
    /// <summary>Answers, but slowly or with losses.</summary>
    Degraded = 2,
    Unreachable = 3,
}

/// <summary>Machine-level network conditions at a point in time.</summary>
/// <param name="Internet">What this machine can reach.</param>
/// <param name="Medium">Medium of the primary route.</param>
/// <param name="Cost">Cost of the primary route.</param>
/// <param name="Flags">Extra facts.</param>
/// <param name="Since">When <paramref name="Internet"/> last changed.</param>
public readonly record struct NetworkConditions(
    InternetReachability Internet,
    NetworkMedium Medium,
    NetworkCost Cost,
    NetworkConditionFlags Flags,
    DateTimeOffset Since)
{
    /// <summary>Nothing measured (the <see langword="default"/> value).</summary>
    public static NetworkConditions Unknown => default;

    /// <summary>True when large transfers should wait: metered, over the data limit, roaming or cellular.</summary>
    public bool PrefersSmallTransfers
        => Cost >= NetworkCost.Metered || Medium == NetworkMedium.Cellular || (Flags & NetworkConditionFlags.Roaming) != 0;
}

/// <summary>A change of <see cref="INetworkConditions.Current"/>.</summary>
public readonly record struct NetworkConditionsChange(NetworkConditions Previous, NetworkConditions Current);

/// <summary>
/// Machine-level network conditions: is there a network, the internet, a captive portal; which medium; is it metered. Registered by
/// <c>AddYFexNetworking</c> (repo YFex.Networking). Consumers (Time, Logging, Offline, jobs, UI) take it optionally from DI and keep
/// today's behaviour when it is absent.
/// </summary>
public interface INetworkConditions
{
    /// <summary>The latest conditions (cached; a cheap read).</summary>
    NetworkConditions Current { get; }

    /// <summary>Raised after hysteresis (a single lost probe does not flip the state), on a thread-pool thread.</summary>
    event Action<NetworkConditionsChange>? Changed;
}
