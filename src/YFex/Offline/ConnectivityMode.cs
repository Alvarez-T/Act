namespace YFex.Offline;

/// <summary>
/// How a site is set up, chosen by an administrator at central and carried by the signed site manifest (offline 02 §2, §5).
/// Static per site; <see cref="ConnectivityMode"/> is the dynamic state of one installation.
/// </summary>
public enum SiteTopology : byte
{
    /// <summary>No site broker: terminals talk to central; offline, each terminal is on its own.</summary>
    Direct = 1,

    /// <summary>One site broker on the LAN coordinates the terminals while central is unreachable.</summary>
    SiteBroker = 2,
}

/// <summary>Who terminals talk to while central is up (offline 02 §4, ⚑O3).</summary>
public enum UpstreamRoute : byte
{
    /// <summary>Terminals call central directly; the broker is a warm standby (default).</summary>
    CentralFirst = 1,

    /// <summary>Terminals call central through the broker (a per-site option for a poor uplink).</summary>
    ThroughSiteBroker = 2,
}

/// <summary>
/// The highest authority an installation can reach right now (offline 02 §2, 04 §1). Read by Messaging (routing), Jobs and the UI.
/// </summary>
public enum ConnectivityMode : byte
{
    /// <summary>Central is reachable: calls go to central (directly or relayed by the site broker).</summary>
    Central = 1,

    /// <summary>Central is unreachable and the site's coordinator is reachable (only with <see cref="SiteTopology.SiteBroker"/>).</summary>
    Site = 2,

    /// <summary>Nothing is reachable: the terminal decides alone within its offline limits.</summary>
    Standalone = 3,
}
