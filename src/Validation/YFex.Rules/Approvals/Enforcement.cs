namespace YFex.Rules;

/// <summary>What the server does when its recomputed fingerprint differs from the client's (09 §8, 11 §2.3).</summary>
public enum DecisionMismatchPolicy : byte
{
    /// <summary>Keep going with the server decision; write an audit/sync-failure entry (retail default: honour what the customer saw is recorded).</summary>
    AcceptAndFlag = 0,
    /// <summary>Treat as a sync failure (→ <c>ISyncFailureLog</c>, like <c>ConflictPolicy.Escalate</c>).</summary>
    Escalate = 1,
    /// <summary>Return a validation problem with the server's violations.</summary>
    Reject = 2,
    /// <summary>Silently apply the server decision (internal, non-customer-facing flows only).</summary>
    UseServer = 3,
}

/// <summary>Options of the Cqrs rules stage (stage 4).</summary>
public sealed record EnforceOptions
{
    public static EnforceOptions Default { get; } = new();

    /// <summary><see cref="TraceLevel.Applied"/>/<see cref="TraceLevel.Full"/> when auditing.</summary>
    public TraceLevel Trace { get; init; } = TraceLevel.Off;
    /// <summary>Decision-bound messages (<c>YFex.Cqrs.IDecisionBound</c>) must carry a fingerprint (non-zero).</summary>
    public bool RequireFingerprint { get; init; }
    public DecisionMismatchPolicy OnMismatch { get; init; } = DecisionMismatchPolicy.AcceptAndFlag;
    /// <summary>Situation → message path renames applied to the violations a rejection returns (04 §14).</summary>
    public SituationPathMap Paths { get; init; } = SituationPathMap.Empty;
    /// <summary>
    /// Amount caps of offline (device-signed) approvals, per permission. A device token for a permission without a cap, or above it, is
    /// refused (an approval that cannot be capped is never accepted offline).
    /// </summary>
    public IReadOnlyList<OfflineApprovalCap> OfflineCaps { get; init; } = [];
}
