namespace YFex.Cqrs.Registration;

/// <summary>
/// Immutable registration-phase metadata for one command type.
/// Plain data — no methods, no behaviour.
/// </summary>
internal sealed record CommandRegistrationMetadata
{
    public required Type CommandType { get; init; }
    /// <summary>Null for ICommand (void); non-null for ICommand&lt;TResult&gt;.</summary>
    public Type? ResultType { get; init; }
    public bool  IsQueueable { get; init; }

    public ValidatorDescriptor[]?  Validators  { get; init; }
    public AuthorizerDescriptor[]? Authorizers { get; init; }

    public InvalidationRuleDescriptor[]? Invalidates { get; init; }

    public Func<object, string>? IdempotencyKey  { get; init; }
    public ConflictPolicy        ConflictPolicy   { get; init; } = ConflictPolicy.Escalate;
    public Type?                 ConflictResolverType { get; init; }

    public OfflineHandlerDescriptor? OfflineHandler { get; init; }

    public RetryPolicy? RetryPolicy { get; init; }
    public TimeSpan?    Timeout     { get; init; }

    /// <summary>
    /// At most one optimistic rule per command (per plan spec).
    /// Multiple calls to .Optimistic() on the fluent builder replace the previous entry.
    /// </summary>
    public OptimisticRuleDescriptor? Optimistic { get; init; }
}
