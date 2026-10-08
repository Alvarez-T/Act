using YFex.Validation;
using YFex.Authorization.Public;

namespace YFex.Rules;

/// <summary>A permission needed for the decision to go through, why, and which rule asked.</summary>
public readonly struct ApprovalRequest(Permission permission, in Violation reason, RuleRef rule)
{
    /// <summary>The permission the approver must hold (typed, authorization-consolidation ⚑Z2).</summary>
    public Permission Permission { get; } = permission;
    public Violation Reason { get; } = reason;
    public RuleRef Rule { get; } = rule;
    public override string ToString() => $"{Permission} ({Reason.Code} by {Rule.Code})";
}

/// <summary>Exact-size list of approval requests.</summary>
public readonly struct ApprovalRequestList
{
    private readonly ApprovalRequest[]? _items;

    internal ApprovalRequestList(ApprovalRequest[]? items) => _items = items is { Length: > 0 } ? items : null;

    public static ApprovalRequestList Empty => default;
    public static ApprovalRequestList From(ReadOnlySpan<ApprovalRequest> items) => new(items.ToArray());
    public int Count => _items?.Length ?? 0;
    public bool IsEmpty => _items is null;
    public ref readonly ApprovalRequest this[int index] => ref _items![index];
    public ReadOnlySpan<ApprovalRequest> AsSpan() => _items;
    public ReadOnlySpan<ApprovalRequest>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    public bool Requires(in Permission permission)
    {
        foreach (ref readonly var a in AsSpan())
            if (a.Permission == permission) return true;
        return false;
    }
}

/// <summary>Verdict case: nothing blocks, nothing to warn.</summary>
public readonly record struct Allowed;
/// <summary>Verdict case: allowed, with warnings.</summary>
public readonly record struct Warned(ViolationList Violations);
/// <summary>Verdict case: allowed only after the listed approvals.</summary>
public readonly record struct NeedsApproval(ApprovalRequestList Approvals);
/// <summary>Verdict case: blocked.</summary>
public readonly record struct Denied(ViolationList Violations);

/// <summary>Kind of a <see cref="Verdict"/>.</summary>
public enum VerdictKind : byte { None = 0, Allowed = 1, Warned = 2, NeedsApproval = 3, Denied = 4 }

/// <summary>Resolved verdict of a decision: Denied &gt; NeedsApproval &gt; Warned &gt; Allowed (04 §7 step 7).</summary>
[Union]
public readonly struct Verdict : IUnion
{
    private readonly ViolationList _violations;
    private readonly ApprovalRequestList _approvals;
    private readonly VerdictKind _tag;

    public Verdict(Allowed _) : this() => _tag = VerdictKind.Allowed;
    public Verdict(Warned v) : this() { _violations = v.Violations; _tag = VerdictKind.Warned; }
    public Verdict(NeedsApproval v) : this() { _approvals = v.Approvals; _tag = VerdictKind.NeedsApproval; }
    public Verdict(Denied v) : this() { _violations = v.Violations; _tag = VerdictKind.Denied; }

    public VerdictKind Kind => _tag;
    public bool HasValue => _tag != VerdictKind.None;
    /// <summary>Allowed or Warned.</summary>
    public bool IsAllowed => _tag is VerdictKind.Allowed or VerdictKind.Warned;
    public bool IsNeedsApproval => _tag == VerdictKind.NeedsApproval;
    public bool IsDenied => _tag == VerdictKind.Denied;

    /// <summary>Boxes (IUnion) — not for library code.</summary>
    public object? Value => _tag switch
    {
        VerdictKind.Allowed => new Allowed(),
        VerdictKind.Warned => new Warned(_violations),
        VerdictKind.NeedsApproval => new NeedsApproval(_approvals),
        VerdictKind.Denied => new Denied(_violations),
        _ => null,
    };

    public bool TryGetValue(out Allowed v) { v = default; return _tag == VerdictKind.Allowed; }
    public bool TryGetValue(out Warned v) { v = _tag == VerdictKind.Warned ? new(_violations) : default; return _tag == VerdictKind.Warned; }
    public bool TryGetValue(out NeedsApproval v) { v = _tag == VerdictKind.NeedsApproval ? new(_approvals) : default; return _tag == VerdictKind.NeedsApproval; }
    public bool TryGetValue(out Denied v) { v = _tag == VerdictKind.Denied ? new(_violations) : default; return _tag == VerdictKind.Denied; }

    public static implicit operator Verdict(Allowed v) => new(v);
    public static implicit operator Verdict(Warned v) => new(v);
    public static implicit operator Verdict(NeedsApproval v) => new(v);
    public static implicit operator Verdict(Denied v) => new(v);

    public override string ToString() => _tag.ToString();
}
