using System.Linq.Expressions;
using System.Security.Claims;
using YFex.Authorization.Public;
using YFex.Cqrs.Registration;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs.Runtime;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Cqrs.Configuration;

/// <summary>
/// Fluent builder for one query type.  Accumulates typed <see cref="ContextRuleFactory"/>,
/// authorization <see cref="YFex.Authorization.Public.Requirement"/>s, and <see cref="InvalidationRuleDescriptor"/> entries
/// during the registration phase, then produces an immutable
/// <see cref="QueryRegistrationMetadata"/> record via <see cref="BuildMetadata"/>.
/// </summary>
public sealed class QueryConfigurationBuilder<TQuery, TResult>
    : IQueryConfigurationSource
    where TQuery : IQuery<TResult>
{
    // Mutable accumulation lists — used only during registration, never on the hot path.
    private List<ContextRuleFactory>?          _validators;
    private RulesConfig?                       _rules;
    private List<Requirement>?        _authorization;
    private List<InvalidationRuleDescriptor>?  _invalidatedBy;
    private CachePolicy?                       _cache;
    private CacheScope                         _scope      = CacheScope.Global;
    private Func<ICacheScopeContext, string>?  _scopeKey;
    private Func<object, string>?              _tagKey;
    private TimeSpan?                          _staleAfter;
    private TimeSpan?                          _timeout;
    private bool                               _notCacheable;
    private string?                            _description;
    private string[]?                          _keywords;

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shape rules for the message (same semantics as attributes, D14/D23). Constant rules are compiled into the
    /// message by YFex.Validation.Generator; the rest (non-constant arguments, lambdas) run from
    /// <see cref="RuntimeRules{T}"/>. Declare in the BASELINE configuration (shared by client and server) or clients
    /// and server drift apart.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> Validation(Action<ValidationBuilder<TQuery>> rules)
    {
        ValidationBuilder<TQuery>.Apply(rules);
        return this;
    }

    /// <summary>Inline context rule (stage 5, server-side async I/O) returning a result.</summary>
    public QueryConfigurationBuilder<TQuery, TResult> Validate(
        Func<TQuery, CancellationToken, ValueTask<ValidationResult>> validator)
        => AddContextRule(ContextRuleFactory<TQuery>.Shared(new InlineResultRule<TQuery>(validator)));

    /// <summary>Inline context rule (stage 5) writing into the collector.</summary>
    public QueryConfigurationBuilder<TQuery, TResult> Validate(
        Func<TQuery, ViolationCollector, CancellationToken, ValueTask> rule)
        => AddContextRule(ContextRuleFactory<TQuery>.Shared(new InlineCollectorRule<TQuery>(rule)));

    /// <summary>
    /// Context rule type (stage 5): resolved from DI when registered, otherwise constructed with DI arguments.
    /// Declare in the SERVER overrides (I/O the client can't do — 09 §3).
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> Validate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>()
        where TRule : class, IContextRule<TQuery>
        => AddContextRule(ContextRuleFactory<TQuery>.FromServices<TRule>());

    [Obsolete("Implement YFex.Validation.IContextRule<T> and register it with .Validate<TRule>().")]
    public QueryConfigurationBuilder<TQuery, TResult> UseValidator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>()
        where TValidator : class, IQueryValidator<TQuery>
        => AddContextRule(new ContextRuleFactory<TQuery>(static sp => new LegacyValidatorRule<TQuery>(
            sp.GetService<TValidator>() ?? ActivatorUtilities.CreateInstance<TValidator>(sp))));

    // ── Rules enforcement (stages 3–4, validation 09 §6, D33) ──────────────────

    /// <summary>
    /// Stage 3: loads the <typeparamref name="TSituation"/> the rules evaluate (all I/O — Principle A). The loader
    /// comes from DI when registered, else it is constructed with DI-resolved constructor arguments.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> UseSituation<TSituation, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLoader>()
        where TSituation : global::YFex.Rules.ISituation<TSituation>
        where TLoader : class, global::YFex.Rules.ISituationLoader<TQuery, TSituation>
    {
        _rules = new RulesConfig<TQuery, TSituation>(static sp => sp.GetService<TLoader>() ?? ActivatorUtilities.CreateInstance<TLoader>(sp))
        {
            Options = _rules?.Options ?? global::YFex.Rules.EnforceOptions.Default,
        };
        return this;
    }

    /// <summary>
    /// Stage 4: evaluates <paramref name="ruleSet"/> (through a registered <c>RuleSetHost&lt;TSituation&gt;</c> when present):
    /// Denied → validation problem; NeedsApproval → approval tokens required; fingerprint mismatch → policy. Queries never reject on the verdict: the handler reads the decision (<see cref="IDecisionAccessor{TSituation}"/>).
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> Enforce<TSituation>(global::YFex.Rules.RuleSetDefinition<TSituation> ruleSet, global::YFex.Rules.EnforceOptions? options = null)
        where TSituation : global::YFex.Rules.ISituation<TSituation>
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var rules = RulesConfig<TQuery, TSituation>.Expect(_rules, typeof(TQuery));
        rules.Set = ruleSet;
        if (options is not null) rules.Options = options;
        return this;
    }

    private QueryConfigurationBuilder<TQuery, TResult> AddContextRule(ContextRuleFactory factory)
    {
        (_validators ??= []).Add(factory);
        return this;
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    // ── Authorization: the verbs record Requirements; CqrsAuthorizationConfiguration exports them to the ONE message policy
    //    every dispatcher enforces through IMessageAuthorization (authorization-consolidation 04 §2) ──

    /// <summary>The caller must be authenticated.</summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequireAuthenticated() => AddRequirement(Requirement.RequireAuthenticated());

    /// <summary>The caller must be in at least one of <paramref name="roles"/> (case-insensitive).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequireRoles(params Role[] roles) => AddRequirement(Requirement.RequireRoles(roles));

    /// <summary>
    /// The caller proved who they are within <paramref name="maxAge"/>, at <paramref name="minimum"/> or stronger (step-up,
    /// authentication 09 §4.1); unmet → <see cref="ErrorKind.ReauthenticationRequired"/>: the client steps up and resends.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequireRecentAuthentication(TimeSpan maxAge, AssuranceLevel minimum = AssuranceLevel.SingleFactor)
        => AddRequirement(Requirement.RequireRecentAuthentication(maxAge, minimum));

    /// <summary>
    /// The caller was verified present at the site within <paramref name="maxAge"/> (⚑Z20, <c>presence_at</c>); unmet, missing or
    /// unparseable → <see cref="ErrorKind.SitePresenceRequired"/>. Optional per command; does not hide it.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequireSitePresence(TimeSpan maxAge) => AddRequirement(Requirement.RequireSitePresence(maxAge));

    /// <summary>The caller must hold <paramref name="permission"/> (a catalog property, e.g. <c>SalesPermissions.SaleVoid</c>).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequirePermission(Permission permission) => AddRequirement(Requirement.Require(permission));

    /// <summary>The caller must hold at least one of <paramref name="permissions"/>.</summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequireAnyPermission(params Permission[] permissions) => AddRequirement(Requirement.RequireAny(permissions));

    /// <summary>
    /// A named policy decided on the server by an <c>INamedPolicyHandler</c>. Hidden on the quick (client/UI) check, so declare it
    /// in the server overrides when clients must still send the message.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> RequirePolicy(PolicyName policy) => AddRequirement(Requirement.RequirePolicy(policy));

    /// <summary>An inline predicate over (caller, message). Does not affect visibility (menus, search, AI).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> Authorize(Func<IAuthorizationContext, TQuery, bool> predicate)
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromDelegate(predicate)));

    /// <summary>
    /// A requirement class — <see cref="IAuthorizationRequirement{TResource}"/> or <see cref="IAsyncAuthorizationRequirement{TResource}"/>
    /// of <typeparamref name="TQuery"/> — registered as a DI singleton (its <c>VisibleWith</c> refines visibility).
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> Authorize<TRequirement>() where TRequirement : class
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromService<TRequirement, TQuery>()));

    private QueryConfigurationBuilder<TQuery, TResult> AddRequirement(in Requirement requirement)
    {
        (_authorization ??= []).Add(requirement);
        return this;
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> Cacheable()
    {
        _cache = new CachePolicy(null, null, null);
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Cacheable(Action<CacheConfigurationBuilder> configure)
    {
        var b = new CacheConfigurationBuilder();
        configure(b);
        _cache = b.Build();
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> NotCacheable()
    {
        _notCacheable = true;
        _cache = null;
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> StaleAfter(TimeSpan threshold)
    {
        _staleAfter = threshold;
        return this;
    }

    // ── Cache scope ───────────────────────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> ScopedByUser()           { _scope = CacheScope.User;    return this; }
    public QueryConfigurationBuilder<TQuery, TResult> ScopedBySession()        { _scope = CacheScope.Session; return this; }
    public QueryConfigurationBuilder<TQuery, TResult> Global()                 { _scope = CacheScope.Global;  return this; }

    /// <summary>Scopes the cache key by <see cref="ICacheScopeContext.TenantId"/> (e.g. supplied by YFex.Cqrs.Tenancy).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> ScopedByTenant()          { _scope = CacheScope.Tenant; _scopeKey = null; return this; }

    public QueryConfigurationBuilder<TQuery, TResult> ScopedByTenant(Func<ICacheScopeContext, string> key)
    {
        _scope = CacheScope.Tenant;
        _scopeKey = key;
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> ScopedBy<TKey>(Func<ICacheScopeContext, TKey> key)
    {
        _scope = CacheScope.Custom;
        _scopeKey = ctx => key(ctx)?.ToString() ?? string.Empty;
        return this;
    }

    // ── Invalidation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Declares the entity key that scopes this query's cache entries. The entry is tagged
    /// <c>en:{QueryType}:{key}</c>, so a command declaring <c>Invalidates&lt;TQuery,TResult&gt;(cmd =&gt; cmd.Id)</c>
    /// with the same key drops exactly that variant instead of every cached variant of the type.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> TaggedBy(Func<TQuery, object> key)
    {
        _tagKey = q => key((TQuery)q)?.ToString() ?? string.Empty;
        return this;
    }

    /// <summary>Declares that dispatching <typeparamref name="TCommand"/> invalidates this query
    /// when the compiled <paramref name="match"/> predicate is satisfied.</summary>
    public QueryConfigurationBuilder<TQuery, TResult> InvalidatedBy<TCommand>(
        Expression<Func<TQuery, TCommand, bool>> match)
        where TCommand : ICommand
    {
        (_invalidatedBy ??= []).Add(new InvalidationRuleDescriptor(typeof(TCommand), match));
        return this;
    }

    /// <summary>
    /// Wildcard / group invalidation: <typeparamref name="TSource"/> may be any ICommand,
    /// ICommand&lt;T&gt;, union type, or a marker interface extending IInvalidationGroup.
    /// Resolved at Build() time.
    /// </summary>
    public QueryConfigurationBuilder<TQuery, TResult> InvalidatedBy<TSource>() where TSource : class
    {
        bool isGroup = typeof(IInvalidationGroup).IsAssignableFrom(typeof(TSource));
        (_invalidatedBy ??= []).Add(new InvalidationRuleDescriptor(typeof(TSource), null, isGroup));
        return this;
    }

    // ── Capability metadata (surfaced via CqrsCapabilityInfo) ─────────────────
    //
    // Authoring-only: consumed by the capabilities bridge, never on the dispatch hot path.
    // Single-value, last-write-wins (mirrors _optimistic / _reverse).

    /// <summary>Authors an AI/human-facing description for this query (surfaced to the capabilities bridge).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> Describe(string ai)
    {
        _description = ai;
        return this;
    }

    /// <summary>Authors search keywords for this query (surfaced to the capabilities bridge).</summary>
    public QueryConfigurationBuilder<TQuery, TResult> Keywords(params string[] keywords)
    {
        _keywords = keywords;
        return this;
    }

    // ── Retry / Timeout / Telemetry ───────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> Retry(Action<RetryConfigurationBuilder> configure)
    {
        // RetryPolicy is stored in metadata; no-op here until Plan 3 wires it
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Timeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Telemetry(Action<TelemetryConfigurationBuilder> configure)
        => this;

    // ── Metadata production ───────────────────────────────────────────────────

    QueryRegistrationMetadata IQueryConfigurationSource.BuildMetadata() => new()
    {
        QueryType    = typeof(TQuery),
        ResultType   = typeof(TResult),
        Validators   = _validators?.ToArray(),
        Rules        = _rules?.Build(typeof(TQuery), isQuery: true),
        Authorization  = _authorization?.ToArray(),
        Cache        = _cache,
        Scope        = _scope,
        ScopeKey     = _scopeKey,
        TagKey       = _tagKey,
        StaleAfter   = _staleAfter,
        Timeout      = _timeout,
        NotCacheable = _notCacheable,
        InvalidatedBy = _invalidatedBy?.ToArray(),
        Description  = _description,
        Keywords     = _keywords,
    };
}
