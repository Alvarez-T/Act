using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs;

/// <summary>
/// Failure payload of <see cref="Result"/>, <see cref="QueueableResult"/> and <see cref="CacheableQueryResult{T}"/>.
/// Plain fields — no list per error, no LINQ — and safe on <see langword="default"/>
/// (<see cref="ErrorType.None"/>, empty <see cref="Message"/>, no <see cref="Violations"/>).
/// </summary>
/// <remarks>
/// Validation failures carry structured <see cref="Violations"/> (code + path + args, D6); clients localize them
/// and map paths to fields (<c>ApplyServer</c>). <see cref="Message"/> stays filled for callers that only show a
/// string: for validation errors it is the first error's path + detail (or code).
/// </remarks>
public readonly struct Error
{
    private readonly string? _message;

    public Error(ErrorType errorType, string message = "")
    {
        Type = errorType;
        _message = message;
    }

    public Error(ErrorType errorType, string message, ViolationList violations)
    {
        Type = errorType;
        _message = message;
        Violations = violations;
    }

    /// <summary>Approval payload (<see cref="ErrorType.ApprovalRequired"/>): the approvals the decision needs, bound to its fingerprint.</summary>
    public Error(ErrorType errorType, string message, ViolationList violations, ApprovalRequestList approvals, ulong decisionFingerprint)
    {
        Type = errorType;
        _message = message;
        Violations = violations;
        Approvals = approvals;
        DecisionFingerprint = decisionFingerprint;
    }

    public ErrorType Type { get; }

    public string Message => _message ?? string.Empty;

    /// <summary>Structured violations; empty unless the error came from validation.</summary>
    public ViolationList Violations { get; }

    public bool HasViolations => !Violations.IsEmpty;

    /// <summary>Permissions that must approve (manager override); empty unless <see cref="ErrorType.ApprovalRequired"/>.</summary>
    public ApprovalRequestList Approvals { get; }

    /// <summary>Fingerprint of the server decision the approvals must be bound to (request an <c>OverrideToken</c> for it).</summary>
    public ulong DecisionFingerprint { get; }

    /// <summary>A <see cref="ErrorType.ValidationProblem"/> carrying every violation of <paramref name="result"/>.</summary>
    public static Error Validation(in ValidationResult result)
        => new(ErrorType.ValidationProblem, FirstMessage(result.Violations), result.Violations);

    /// <summary>
    /// <see cref="ErrorType.ApprovalRequired"/>: the client asks the approvers (09 §6), then resends the command with an
    /// <c>OverrideToken</c> per permission bound to <paramref name="fingerprint"/>.
    /// </summary>
    public static Error ApprovalRequired(in ApprovalRequestList approvals, ulong fingerprint, in ViolationList violations = default)
        => new(ErrorType.ApprovalRequired, approvals.IsEmpty ? "Approval required." : "Approval required: " + approvals[0].Permission,
            violations, approvals, fingerprint);

    public static implicit operator Error(string message) => new(ErrorType.None, message);

    public override string ToString() => Violations.Count > 1
        ? $"{Type}: {Message} (+{Violations.Count - 1} more)"
        : $"{Type}: {Message}";

    // Backward-compatible text: the first error (else the first violation) as "Path: detail-or-code".
    private static string FirstMessage(in ViolationList violations)
    {
        if (violations.IsEmpty) return string.Empty;
        ReadOnlySpan<Violation> all = violations.AsSpan();
        ref readonly Violation pick = ref all[0];
        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].IsError) continue;
            pick = ref all[i];
            break;
        }
        string text = pick.Detail ?? pick.Code.ToString();
        return pick.Path.IsEmpty ? text : $"{pick.Path}: {text}";
    }
}
