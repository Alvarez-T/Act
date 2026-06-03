using System.Linq.Expressions;
using System.Security.Claims;
using YFex.Cqrs.Registration;

namespace YFex.Cqrs.Configuration;

/// <summary>
/// Fluent builder for one query type.  Accumulates typed <see cref="ValidatorDescriptor"/>,
/// <see cref="AuthorizerDescriptor"/>, and <see cref="InvalidationRuleDescriptor"/> entries
/// during the registration phase, then produces an immutable
/// <see cref="QueryRegistrationMetadata"/> record via <see cref="BuildMetadata"/>.
/// </summary>
public sealed class QueryConfigurationBuilder<TQuery, TResult>
    : IQueryConfigurationSource
    where TQuery : IQuery<TResult>
{
    // Mutable accumulation lists — used only during registration, never on the hot path.
    private List<ValidatorDescriptor>?         _validators;
    private List<AuthorizerDescriptor>?        _authorizers;
    private List<InvalidationRuleDescriptor>?  _invalidatedBy;
    private CachePolicy?                       _cache;
    private CacheScope                         _scope      = CacheScope.Global;
    private Func<ICacheScopeContext, string>?  _scopeKey;
    private TimeSpan?                          _staleAfter;
    private TimeSpan?                          _timeout;
    private bool                               _notCacheable;

    // ── Validation ────────────────────────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> Validate(
        Func<TQuery, CancellationToken, ValueTask<ValidationResult>> validator)
    {
        // Wrap to erased delegate at registration time (one allocation total per builder)
        (_validators ??= []).Add(ValidatorDescriptor.Inline((q, ct) => validator((TQuery)q, ct)));
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Validate<TValidator>()
        where TValidator : IQueryValidator<TQuery>
    {
        (_validators ??= []).Add(ValidatorDescriptor.FromType(typeof(TValidator)));
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> UseFluentValidator<TValidator>()
        where TValidator : FluentValidation.AbstractValidator<TQuery>, new()
    {
        var v = new TValidator();
        // Capture instance at registration time; delegate created once.
        (_validators ??= []).Add(ValidatorDescriptor.Inline(async (q, ct) =>
        {
            var r = await v.ValidateAsync((TQuery)q, ct).ConfigureAwait(false);
            if (r.IsValid) return ValidationResult.Success();
            return ValidationResult.Failure(
                r.Errors.Select(e => new ValidationError(e.PropertyName, e.ErrorMessage, e.ErrorCode)).ToArray());
        }));
        return this;
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    public QueryConfigurationBuilder<TQuery, TResult> RequireAuthenticated()
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustBeAuthenticated());
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> RequireRoles(params string[] roles)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustHaveRoles(roles));
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> RequireAuthorization(string policyName)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustSatisfyPolicy(policyName));
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Authorize(Func<ClaimsPrincipal, TQuery, bool> predicate)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.Inline((user, q) => predicate(user, (TQuery)q)));
        return this;
    }

    public QueryConfigurationBuilder<TQuery, TResult> Authorize<TAuthorizer>()
        where TAuthorizer : IQueryAuthorizer<TQuery>
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.FromType(typeof(TAuthorizer)));
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
        Authorizers  = _authorizers?.ToArray(),
        Cache        = _cache,
        Scope        = _scope,
        ScopeKey     = _scopeKey,
        StaleAfter   = _staleAfter,
        Timeout      = _timeout,
        NotCacheable = _notCacheable,
        InvalidatedBy = _invalidatedBy?.ToArray(),
    };
}
