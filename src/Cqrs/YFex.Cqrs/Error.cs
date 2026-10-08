using System.Runtime.CompilerServices;
using YFex.Authorization.Public;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs;

/// <summary>Kind (union tag) of an <see cref="Error"/>. <c>default</c> is <see cref="None"/> (no error).</summary>
public enum ErrorKind : byte
{
    None = 0,
    NotFound = 1,
    Failed = 2,
    /// <summary>No identity, and the operation needs one (re-login; HTTP 401).</summary>
    Unauthenticated = 3,
    /// <summary>Identity present, requirement unmet — carries the <see cref="Denial"/> (HTTP 403, "Requer: …").</summary>
    Forbidden = 4,
    ValidationProblem = 5,
    Conflict = 6,
    /// <summary>The decision needs approvals (YFex.Rules, validation 09 §6; approvals of authorization-consolidation 06 §1).</summary>
    ApprovalRequired = 7,
}

/// <summary>Error case: the thing does not exist.</summary>
public readonly record struct NotFound(string Message);
/// <summary>Error case: the operation failed (handler exception, infrastructure).</summary>
public readonly record struct Failed(string Message);
/// <summary>Error case: authentication is required.</summary>
public readonly record struct Unauthenticated(string Message);
/// <summary>Error case: the caller is authenticated but not authorized — <see cref="Denial"/> says why (missing permission…).</summary>
public readonly record struct Forbidden(Denial Denial);
/// <summary>Error case: structured violations (code + path + args).</summary>
public readonly record struct ValidationProblem(ViolationList Violations);
/// <summary>Error case: a conflict (stale version, changed decision); may carry violations explaining it.</summary>
public readonly record struct Conflict(string Message, ViolationList Violations = default);
/// <summary>Error case: approvals needed, bound to the decision fingerprint (request an approval token for it).</summary>
public readonly record struct ApprovalRequired(ApprovalRequestList Approvals, ulong DecisionFingerprint, ViolationList Violations = default);

/// <summary>
/// Failure payload of <see cref="Result"/>, <see cref="QueueableResult"/> and <see cref="CacheableQueryResult{T}"/> — a
/// <c>[Union]</c> tagged struct (authorization-consolidation ⚑Z11): an <see cref="ErrorKind"/> tag plus only the data of its case
/// (<see cref="TryGetValue(out Forbidden)"/> …). Safe on <see langword="default"/> (<see cref="ErrorKind.None"/>, empty
/// <see cref="Message"/>, no <see cref="Violations"/>).
/// </summary>
/// <remarks>
/// <see cref="Message"/> is always filled for callers that only show a string (validation: the first error's path + detail;
/// forbidden: the denial text). Validation-like cases expose <see cref="Violations"/> directly (clients localize them and map
/// paths to fields with <c>ApplyServer</c>).
/// <code>
/// switch (result.Error?.Kind)
/// {
///     case ErrorKind.Unauthenticated: await session.LoginAgainAsync(); break;
///     case ErrorKind.Forbidden when error.TryGetValue(out Forbidden f): toast($"Requer: {catalogs.DisplayText(f.Denial.Missing)}"); break;
///     case ErrorKind.ApprovalRequired when error.TryGetValue(out ApprovalRequired a): await approvals.ShowAsync(a.Approvals, a.DecisionFingerprint); break;
/// }
/// </code>
/// </remarks>
[Union]
public readonly struct Error : IUnion
{
    private readonly string? _message;
    private readonly ViolationList _violations;          // ValidationProblem, Conflict, ApprovalRequired
    private readonly object? _payload;                    // Forbidden: boxed Denial; ApprovalRequired: ApprovalPayload
    private readonly ErrorKind _kind;

    private sealed record ApprovalPayload(ApprovalRequestList Approvals, ulong DecisionFingerprint);

    private Error(ErrorKind kind, string? message, in ViolationList violations, object? payload)
    {
        _kind = kind;
        _message = message;
        _violations = violations;
        _payload = payload;
    }

    public Error(NotFound value) : this(ErrorKind.NotFound, value.Message, default, null) { }
    public Error(Failed value) : this(ErrorKind.Failed, value.Message, default, null) { }
    public Error(Unauthenticated value) : this(ErrorKind.Unauthenticated, value.Message, default, null) { }
    public Error(Forbidden value) : this(ErrorKind.Forbidden, value.Denial.Describe(), default, value.Denial) { }
    public Error(ValidationProblem value) : this(ErrorKind.ValidationProblem, FirstMessage(value.Violations), value.Violations, null) { }
    public Error(Conflict value) : this(ErrorKind.Conflict, value.Message, value.Violations, null) { }
    public Error(global::YFex.Cqrs.ApprovalRequired value)
        : this(ErrorKind.ApprovalRequired,
            value.Approvals.IsEmpty ? "Approval required." : "Approval required: " + value.Approvals[0].Permission,
            value.Violations, new ApprovalPayload(value.Approvals, value.DecisionFingerprint)) { }

    public ErrorKind Kind => _kind;
    public bool HasValue => _kind != ErrorKind.None;

    /// <summary>Display fallback, always filled for a non-default error.</summary>
    public string Message => _message ?? string.Empty;

    /// <summary>Structured violations of <see cref="ErrorKind.ValidationProblem"/>, <see cref="ErrorKind.Conflict"/> and <see cref="ErrorKind.ApprovalRequired"/>; empty otherwise.</summary>
    public ViolationList Violations => _violations;

    public bool HasViolations => !_violations.IsEmpty;

    /// <summary>Boxes (IUnion) — not for library code.</summary>
    public object? Value => _kind switch
    {
        ErrorKind.NotFound => new NotFound(Message),
        ErrorKind.Failed => new Failed(Message),
        ErrorKind.Unauthenticated => new Unauthenticated(Message),
        ErrorKind.Forbidden => new Forbidden((Denial)_payload!),
        ErrorKind.ValidationProblem => new ValidationProblem(_violations),
        ErrorKind.Conflict => new Conflict(Message, _violations),
        ErrorKind.ApprovalRequired => ApprovalCase(),
        _ => null,
    };

    public bool TryGetValue(out NotFound value) { value = _kind == ErrorKind.NotFound ? new(Message) : default; return _kind == ErrorKind.NotFound; }
    public bool TryGetValue(out Failed value) { value = _kind == ErrorKind.Failed ? new(Message) : default; return _kind == ErrorKind.Failed; }
    public bool TryGetValue(out Unauthenticated value) { value = _kind == ErrorKind.Unauthenticated ? new(Message) : default; return _kind == ErrorKind.Unauthenticated; }
    public bool TryGetValue(out Forbidden value) { value = _kind == ErrorKind.Forbidden ? new((Denial)_payload!) : default; return _kind == ErrorKind.Forbidden; }
    public bool TryGetValue(out ValidationProblem value) { value = _kind == ErrorKind.ValidationProblem ? new(_violations) : default; return _kind == ErrorKind.ValidationProblem; }
    public bool TryGetValue(out Conflict value) { value = _kind == ErrorKind.Conflict ? new(Message, _violations) : default; return _kind == ErrorKind.Conflict; }
    public bool TryGetValue(out global::YFex.Cqrs.ApprovalRequired value)
    {
        value = _kind == ErrorKind.ApprovalRequired ? ApprovalCase() : default;
        return _kind == ErrorKind.ApprovalRequired;
    }

    private global::YFex.Cqrs.ApprovalRequired ApprovalCase()
    {
        var payload = (ApprovalPayload)_payload!;
        return new(payload.Approvals, payload.DecisionFingerprint, _violations);
    }

    public static implicit operator Error(NotFound value) => new(value);
    public static implicit operator Error(Failed value) => new(value);
    public static implicit operator Error(Unauthenticated value) => new(value);
    public static implicit operator Error(Forbidden value) => new(value);
    public static implicit operator Error(ValidationProblem value) => new(value);
    public static implicit operator Error(Conflict value) => new(value);
    public static implicit operator Error(global::YFex.Cqrs.ApprovalRequired value) => new(value);

    /// <summary>A plain message is a <see cref="ErrorKind.Failed"/> error.</summary>
    public static implicit operator Error(string message) => new(new Failed(message));

    /// <summary>This error with another display <see cref="Message"/> (same case and data).</summary>
    public Error WithMessage(string message) => new(_kind, message, _violations, _payload);

    // ── Factories ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A <see cref="ErrorKind.ValidationProblem"/> carrying every violation of <paramref name="result"/>.</summary>
    public static Error Validation(in ValidationResult result) => new(new ValidationProblem(result.Violations));

    /// <summary>
    /// <see cref="ErrorKind.ApprovalRequired"/>: the client asks the approvers (validation 09 §6), then resends the command with an
    /// approval token per permission bound to <paramref name="fingerprint"/>.
    /// </summary>
    public static Error ApprovalRequired(in ApprovalRequestList approvals, ulong fingerprint, in ViolationList violations = default)
        => new(new global::YFex.Cqrs.ApprovalRequired(approvals, fingerprint, violations));

    /// <summary>
    /// The error of a denied authorization verdict: <see cref="ErrorKind.Unauthenticated"/> or
    /// <see cref="ErrorKind.Forbidden"/> with the <see cref="Denial"/> (authorization-consolidation 03 §5.1).
    /// </summary>
    public static Error From(in AuthorizationVerdict verdict) => verdict.Kind switch
    {
        AuthorizationVerdictKind.Unauthenticated => new Unauthenticated(verdict.Describe()),
        AuthorizationVerdictKind.Forbidden => new Forbidden(verdict.Denial),
        AuthorizationVerdictKind.Allowed => throw new ArgumentException("An allowed verdict is not an error.", nameof(verdict)),
        _ => new Forbidden(new Denial(DenialKind.None)),
    };

    public override string ToString() => _violations.Count > 1
        ? $"{_kind}: {Message} (+{_violations.Count - 1} more)"
        : $"{_kind}: {Message}";

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
