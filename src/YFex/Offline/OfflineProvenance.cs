using YFex.Installations;
using YFex.Time;

namespace YFex.Offline;

/// <summary>
/// Conditions an offline decision was made under (offline 05 §6). Shared by Audit, Logging, Rules and the reconciliation at central,
/// so none of them invents its own "was offline" flag. Values are combined.
/// </summary>
[Flags]
public enum OfflineFlags : uint
{
    None = 0,

    /// <summary>The trusted clock detected that the device clock was set back (YFex.Time <c>RollbackDetected</c>).</summary>
    ClockRollback = 1 << 0,

    /// <summary>The trusted clock had no anchor: the time is the device's own (YFex.Time <c>Unanchored</c>).</summary>
    Unanchored = 1 << 1,

    /// <summary>The time came only from unauthenticated sources, such as peers (YFex.Time <c>Unauthenticated</c>, ⚑T13).</summary>
    UnauthenticatedTime = 1 << 2,

    /// <summary>The installation was paired offline and not yet confirmed by central (installations I5, ⚑O7).</summary>
    ProvisionalInstallation = 1 << 3,

    /// <summary>Rule data older than its <c>MaxOfflineAge</c> was used (offline 05 §7).</summary>
    StaleRuleData = 1 << 4,

    /// <summary>The installation certificate was in its grace period (offline 05 §7).</summary>
    CertificateGrace = 1 << 5,

    /// <summary>An approval signed by this device was used (capped, offline 06 §6).</summary>
    DeviceApproval = 1 << 6,

    /// <summary>An approval signed by the site broker was used (offline 06 §6).</summary>
    SiteApproval = 1 << 7,

    /// <summary>The actor signed in with the app's minimal offline profile (offline 05 §5).</summary>
    OfflineProfile = 1 << 8,

    /// <summary>The operation used more than the terminal's allowance (<c>.WhenExhausted(AllowAndFlag | RequireApproval)</c>, offline 06 §4.1).</summary>
    OverAllowance = 1 << 9,

    /// <summary>A store-wide unique value was claimed provisionally on a lone terminal (offline 06 §4.3): central is the final authority.</summary>
    ProvisionalClaim = 1 << 10,
}

/// <summary>
/// Where, when and under which conditions an offline-capable operation was decided (offline 05 §6). Carried by every
/// <c>OfflineEnvelope</c> (YFex.Offline) and exposed to handlers while they run.
/// </summary>
/// <param name="Mode">The mode at decision time (<see cref="ConnectivityMode.Central"/> for an envelope created online).</param>
/// <param name="Installation">The installation that decided.</param>
/// <param name="CoordinatedBy">The coordinator that granted sessions, budgets or claims, in <see cref="ConnectivityMode.Site"/>.</param>
/// <param name="SnapshotVersion">Version of the permission snapshot the decision used; 0 when none.</param>
/// <param name="DecidedAt">Trusted time of the decision; central corrects it with the installation's clock offset.</param>
/// <param name="Flags">Conditions to review at central.</param>
public sealed record OfflineProvenance(ConnectivityMode Mode, InstallationId Installation, InstallationId? CoordinatedBy,
    long SnapshotVersion, TrustedInstant DecidedAt, OfflineFlags Flags);
