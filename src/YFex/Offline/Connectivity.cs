using YFex.Installations;
using YFex.Networking;
using YFex.Time;

namespace YFex.Offline;

/// <summary>
/// Connectivity as a mode, not a boolean (offline 04 §1, fixes F9): composes the RPC link to central, the machine's network
/// conditions and the site's coordinator as seen through the installation's friends. Implemented by YFex.Offline.
/// <code>
/// if (connectivity.Mode == ConnectivityMode.Standalone) banner.Show("Modo isolado");
/// connectivity.Changed += change => log.Write(change.Previous, change.Current, change.Reason);
/// </code>
/// </summary>
public interface IConnectivity
{
    /// <summary>The current mode (stable: changes pass the hysteresis of offline 04 §4).</summary>
    ConnectivityMode Mode { get; }

    /// <summary>What this installation sees right now, behind <see cref="Mode"/>.</summary>
    Reachability Reachability { get; }

    /// <summary>Raised after every change of <see cref="Mode"/>.</summary>
    event Action<ConnectivityChange>? Changed;
}

/// <summary>
/// What an installation sees (offline 04 §1): central's health from its RPC link, the site's coordinator (the roster's active site
/// broker, when the site has one) and whether any friend reaches central, which keeps signed items moving through the relay even
/// when this installation's own RPC link is down.
/// </summary>
/// <param name="Central">Health of the RPC link to central.</param>
/// <param name="Coordinator">The site's coordinator; null when the site has none (<see cref="SiteTopology.Direct"/>).</param>
/// <param name="CoordinatorHealth">Health of the coordinator as a friend; <see cref="EndpointHealth.Unknown"/> without one.</param>
/// <param name="AnyFriendReachesCentral">A friend advertises that it reaches central (relay channel, ⚑O26).</param>
public readonly record struct Reachability(EndpointHealth Central, InstallationId? Coordinator, EndpointHealth CoordinatorHealth,
    bool AnyFriendReachesCentral)
{
    /// <summary>Nothing observed yet.</summary>
    public static Reachability Unknown => default;

    /// <summary>True when central's link is usable (healthy or degraded).</summary>
    public bool CentralUsable => Central is EndpointHealth.Healthy or EndpointHealth.Degraded;

    /// <summary>True when there is a coordinator and it is usable (healthy or degraded).</summary>
    public bool CoordinatorUsable => Coordinator is not null && CoordinatorHealth is EndpointHealth.Healthy or EndpointHealth.Degraded;
}

/// <summary>Why the mode changed.</summary>
public enum ConnectivityChangeReason : byte
{
    /// <summary>The first evaluation after start-up.</summary>
    Started = 1,

    /// <summary>Central became reachable.</summary>
    CentralReachable = 2,

    /// <summary>Central became unreachable.</summary>
    CentralUnreachable = 3,

    /// <summary>The coordinator became reachable while central is not.</summary>
    CoordinatorReachable = 4,

    /// <summary>The coordinator became unreachable while central is not.</summary>
    CoordinatorUnreachable = 5,

    /// <summary>The machine has no network at all (cable, Wi-Fi, airplane mode): applied at once.</summary>
    NoNetwork = 6,

    /// <summary>The site's topology or coordinator changed (a new manifest or roster).</summary>
    SiteChanged = 7,
}

/// <summary>A change of <see cref="IConnectivity.Mode"/>, with the trusted time it was decided at.</summary>
public readonly record struct ConnectivityChange(ConnectivityMode Previous, ConnectivityMode Current, ConnectivityChangeReason Reason,
    TrustedInstant At);
