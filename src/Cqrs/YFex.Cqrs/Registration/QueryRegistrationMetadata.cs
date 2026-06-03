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

    public ValidatorDescriptor[]?              Validators   { get; init; }
    public AuthorizerDescriptor[]?             Authorizers  { get; init; }

    public CachePolicy?                        Cache        { get; init; }
    public CacheScope                          Scope        { get; init; } = CacheScope.Global;
    public Func<ICacheScopeContext, string>?   ScopeKey     { get; init; }
    public TimeSpan?                           StaleAfter   { get; init; }
    public TimeSpan?                           Timeout      { get; init; }
    public bool                                NotCacheable { get; init; }

    public InvalidationRuleDescriptor[]? InvalidatedBy { get; init; }
}
