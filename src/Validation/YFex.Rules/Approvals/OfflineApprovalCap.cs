using YFex.Authorization.Public;

namespace YFex.Rules;

/// <summary>
/// The amount cap of OFFLINE (device-signed) approvals of one permission (D34): the highest requested value an offline manager may
/// approve. Rules owns it because it reads the limit violation's arguments; how old an offline approval may be is Authorization's
/// <c>OfflineApprovalPolicy</c> (authorization-consolidation 06 §1.1).
/// </summary>
/// <param name="Permission">The approval permission, e.g. <c>SalesPermissions.DiscountOverride</c>.</param>
/// <param name="MaxRequested">Highest requested value (the limit check's arg {1}) approvable offline; None = never offline.</param>
public readonly record struct OfflineApprovalCap(Permission Permission, RawValue MaxRequested)
{
    /// <summary>
    /// True when <paramref name="request"/> may be approved offline under this cap: same permission, a limit violation that carries the
    /// requested value (an explicit <c>RequireApproval</c> directive has none) and that value at most <see cref="MaxRequested"/>.
    /// </summary>
    public bool Allows(in ApprovalRequest request)
    {
        if (MaxRequested.IsNone || request.Permission != Permission || request.Reason.Args.Count < 2) return false;
        var requested = request.Reason.Args[1];
        return requested.TryCompareTo(MaxRequested, out int c) && c <= 0;
    }

    /// <summary>The cap of <paramref name="request"/>'s permission in <paramref name="caps"/> allows it.</summary>
    public static bool AllowedBy(IReadOnlyList<OfflineApprovalCap> caps, in ApprovalRequest request)
    {
        for (int i = 0; i < caps.Count; i++)
            if (caps[i].Permission == request.Permission) return caps[i].Allows(in request);
        return false;
    }
}
