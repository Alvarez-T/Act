namespace YFex.Cqrs;

public enum ErrorType
{
    None,
    NotFound,
    Fail,
    Unauthorized,
    ValidationProblem,
    Conflict,
    /// <summary>The decision needs approvals (YFex.Rules, 09 §6) — see <see cref="Error.Approvals"/>.</summary>
    ApprovalRequired,
}
