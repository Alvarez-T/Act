namespace YFex.Cqrs;

public enum ConflictPolicy
{
    /// <summary>Treat the conflict as a sync failure; move to ISyncFailureLog.</summary>
    Escalate,
    /// <summary>Re-schedule the command for later replay with exponential backoff.</summary>
    RetryLater,
    /// <summary>Silently discard the conflicting command.</summary>
    Discard
}
