// Types from the root YFex.Cqrs namespace are visible automatically within the same assembly.
namespace YFex.Cqrs.Registration;

/// <summary>
/// Immutable registration-phase metadata for one query type.
/// Produced by <see cref="YFex.Cqrs.Configuration.QueryConfigurationBuilder{TQuery,TResult}"/>
/// at the end of the fluent chain; consumed by <see cref="CompiledMessagingRegistry.Build"/>.
/// Plain data carrier — no methods, no behaviour.
/// </summary>
internal sealed record QueryRegistrationMetadata
{
    public required Type QueryType  { get; init; }
    public required Type ResultType { get; init; }

    // ── Capability metadata (authored on the builder, surfaced via CqrsCapabilityInfo) ──
    /// <summary>AI/human-facing description authored via <c>.Describe(...)</c>. Null when unset.</summary>
    public string?   Description { get; init; }
    /// <summary>Search keywords authored via <c>.Keywords(...)</c>. Null when unset.</summary>
    public string[]? Keywords    { get; init; }

    /// <summary>Stages 3–4 (situation + rules), last configuration wins.</summary>
    public YFex.Cqrs.Runtime.RulesStage? Rules { get; init; }

    public YFex.Cqrs.Runtime.ContextRuleFactory[]?              Validators   { get; init; }
    public AuthorizerDescriptor[]?             Authorizers  { get; init; }

    public CachePolicy?                        Cache        { get; init; }
    public CacheScope                          Scope        { get; init; } = CacheScope.Global;
    public Func<ICacheScopeContext, string>?   ScopeKey     { get; init; }
    public Func<object, string>?               TagKey       { get; init; }
    public TimeSpan?                           StaleAfter   { get; init; }
    public TimeSpan?                           Timeout      { get; init; }
    public bool                                NotCacheable { get; init; }

    public InvalidationRuleDescriptor[]? InvalidatedBy { get; init; }
}
