using System.Linq.Expressions;
using System.Security.Claims;
using YFex.Cqrs.Registration;

namespace YFex.Cqrs.Configuration;

// â”€â”€ No-result command builder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Fluent builder for <see cref="ICommand"/> (no typed result).</summary>
public sealed class CommandConfigurationBuilder<TCommand>
    : ICommandConfigurationSource
    where TCommand : ICommand
{
    private List<ValidatorDescriptor>?          _validators;
    private List<AuthorizerDescriptor>?         _authorizers;
    private List<InvalidationRuleDescriptor>?   _invalidates;
    private Func<object, string>?               _idempotencyKey;
    private ConflictPolicy                      _conflict = ConflictPolicy.Escalate;
    private Type?                               _conflictResolverType;
    private OfflineHandlerDescriptor?           _offlineHandler;
    private RetryPolicy?                        _retry;
    private TimeSpan?                           _timeout;
    private OptimisticRuleDescriptor?           _optimistic;

    // â”€â”€ Validation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> Validate(
        Func<TCommand, CancellationToken, ValueTask<ValidationResult>> validator)
    {
        (_validators ??= []).Add(ValidatorDescriptor.Inline((c, ct) => validator((TCommand)c, ct)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Validate<TValidator>()
        where TValidator : ICommandValidator<TCommand>
    {
        (_validators ??= []).Add(ValidatorDescriptor.FromType(typeof(TValidator)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> UseFluentValidator<TValidator>()
        where TValidator : FluentValidation.AbstractValidator<TCommand>, new()
    {
        var v = new TValidator();
        (_validators ??= []).Add(ValidatorDescriptor.Inline(async (c, ct) =>
        {
            var r = await v.ValidateAsync((TCommand)c, ct).ConfigureAwait(false);
            if (r.IsValid) return ValidationResult.Success();
            return ValidationResult.Failure(
                r.Errors.Select(e => new ValidationError(e.PropertyName, e.ErrorMessage, e.ErrorCode)).ToArray());
        }));
        return this;
    }

    // â”€â”€ Authorization â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> RequireAuthenticated()
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustBeAuthenticated());
        return this;
    }

    public CommandConfigurationBuilder<TCommand> RequireRoles(params string[] roles)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustHaveRoles(roles));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> RequireAuthorization(string policyName)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustSatisfyPolicy(policyName));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Authorize(Func<ClaimsPrincipal, TCommand, bool> predicate)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.Inline((u, c) => predicate(u, (TCommand)c)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Authorize<TAuthorizer>()
        where TAuthorizer : ICommandAuthorizer<TCommand>
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.FromType(typeof(TAuthorizer)));
        return this;
    }

    // â”€â”€ Invalidation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> Invalidates<TQuery, TResult>(
        Expression<Func<TQuery, TCommand, bool>> match)
        where TQuery : IQuery<TResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TQuery), match));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Invalidates<TQuery, TResult>()
        where TQuery : IQuery<TResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TQuery), null));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> InvalidatesGroup<TGroup>()
        where TGroup : IInvalidationGroup
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TGroup), null, isGroup: true));
        return this;
    }

    // â”€â”€ Idempotency & Conflict â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> IdempotencyKey(Func<TCommand, string> factory)
    {
        _idempotencyKey = c => factory((TCommand)c);
        return this;
    }

    public CommandConfigurationBuilder<TCommand> OnConflict(ConflictPolicy policy)
    {
        _conflict = policy;
        return this;
    }

    public CommandConfigurationBuilder<TCommand> OnConflict<TResolver>()
        where TResolver : IConflictResolver<TCommand>
    {
        _conflictResolverType = typeof(TResolver);
        return this;
    }

    // â”€â”€ Offline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> OnOffline(
        Func<TCommand, CancellationToken, ValueTask> handler)
    {
        _offlineHandler = OfflineHandlerDescriptor.Inline((c, ct) => handler((TCommand)c, ct));
        return this;
    }

    public CommandConfigurationBuilder<TCommand> OnOffline<THandler>()
        where THandler : IOfflineHandler<TCommand>
    {
        _offlineHandler = OfflineHandlerDescriptor.FromType(typeof(THandler));
        return this;
    }

    // â”€â”€ Optimistic updates â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// One optimistic rule per command (per plan spec). Multiple calls replace the previous entry.
    /// NOTE: <paramref name="apply"/> must use explicit constructors â€” C# <c>with</c> expressions
    /// are not supported in expression trees.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> Optimistic<TQuery, TQueryResult>(
        Expression<Func<TQueryResult, TCommand, bool>> match,
        Expression<Func<TQueryResult, TCommand, TQueryResult>> apply)
        where TQuery : IQuery<TQueryResult>
    {
        _optimistic = new OptimisticRuleDescriptor(typeof(TQuery), match, apply);
        return this;
    }

    // â”€â”€ Retry / Timeout â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public CommandConfigurationBuilder<TCommand> Retry(Action<RetryConfigurationBuilder> configure)
    {
        var b = new RetryConfigurationBuilder();
        configure(b);
        _retry = b.Build();
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Timeout(TimeSpan timeout) { _timeout = timeout; return this; }

    public CommandConfigurationBuilder<TCommand> Telemetry(Action<TelemetryConfigurationBuilder> configure)
        => this;

    // â”€â”€ Metadata production â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    CommandRegistrationMetadata ICommandConfigurationSource.BuildMetadata() => new()
    {
        CommandType          = typeof(TCommand),
        ResultType           = null,
        IsQueueable          = typeof(IQueueable).IsAssignableFrom(typeof(TCommand)),
        Validators           = _validators?.ToArray(),
        Authorizers          = _authorizers?.ToArray(),
        Invalidates          = _invalidates?.ToArray(),
        IdempotencyKey       = _idempotencyKey,
        ConflictPolicy       = _conflict,
        ConflictResolverType = _conflictResolverType,
        OfflineHandler       = _offlineHandler,
        RetryPolicy          = _retry,
        Timeout              = _timeout,
        Optimistic           = _optimistic,
    };
}

// â”€â”€ Result-bearing command builder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Fluent builder for <see cref="ICommand{TResult}"/>.</summary>
public sealed class CommandConfigurationBuilder<TCommand, TResult>
    : ICommandConfigurationSource
    where TCommand : ICommand<TResult>
{
    private List<ValidatorDescriptor>?          _validators;
    private List<AuthorizerDescriptor>?         _authorizers;
    private List<InvalidationRuleDescriptor>?   _invalidates;
    private Func<object, string>?               _idempotencyKey;
    private ConflictPolicy                      _conflict = ConflictPolicy.Escalate;
    private Type?                               _conflictResolverType;
    private OfflineHandlerDescriptor?           _offlineHandler;
    private RetryPolicy?                        _retry;
    private TimeSpan?                           _timeout;
    private OptimisticRuleDescriptor?           _optimistic;

    public CommandConfigurationBuilder<TCommand, TResult> Validate(
        Func<TCommand, CancellationToken, ValueTask<ValidationResult>> validator)
    {
        (_validators ??= []).Add(ValidatorDescriptor.Inline((c, ct) => validator((TCommand)c, ct)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Validate<TValidator>()
        where TValidator : ICommandValidator<TCommand>
    {
        (_validators ??= []).Add(ValidatorDescriptor.FromType(typeof(TValidator)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> UseFluentValidator<TValidator>()
        where TValidator : FluentValidation.AbstractValidator<TCommand>, new()
    {
        var v = new TValidator();
        (_validators ??= []).Add(ValidatorDescriptor.Inline(async (c, ct) =>
        {
            var r = await v.ValidateAsync((TCommand)c, ct).ConfigureAwait(false);
            if (r.IsValid) return ValidationResult.Success();
            return ValidationResult.Failure(
                r.Errors.Select(e => new ValidationError(e.PropertyName, e.ErrorMessage, e.ErrorCode)).ToArray());
        }));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> RequireAuthenticated()
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustBeAuthenticated());
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> RequireRoles(params string[] roles)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustHaveRoles(roles));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> RequireAuthorization(string policyName)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.MustSatisfyPolicy(policyName));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Authorize(
        Func<ClaimsPrincipal, TCommand, bool> predicate)
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.Inline((u, c) => predicate(u, (TCommand)c)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Authorize<TAuthorizer>()
        where TAuthorizer : ICommandAuthorizer<TCommand>
    {
        (_authorizers ??= []).Add(AuthorizerDescriptor.FromType(typeof(TAuthorizer)));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Invalidates<TQuery, TQueryResult>(
        Expression<Func<TQuery, TCommand, bool>> match)
        where TQuery : IQuery<TQueryResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TQuery), match));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Invalidates<TQuery, TQueryResult>()
        where TQuery : IQuery<TQueryResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TQuery), null));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> InvalidatesGroup<TGroup>()
        where TGroup : IInvalidationGroup
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(typeof(TGroup), null, isGroup: true));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> IdempotencyKey(Func<TCommand, string> factory)
    {
        _idempotencyKey = c => factory((TCommand)c);
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> OnConflict(ConflictPolicy policy)
    {
        _conflict = policy;
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> OnConflict<TResolver>() where TResolver : class
    {
        _conflictResolverType = typeof(TResolver);
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> OnOffline(
        Func<TCommand, CancellationToken, ValueTask> handler)
    {
        _offlineHandler = OfflineHandlerDescriptor.Inline((c, ct) => handler((TCommand)c, ct));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> OnOffline<THandler>()
        where THandler : IOfflineHandler<TCommand, TResult>
    {
        _offlineHandler = OfflineHandlerDescriptor.FromType(typeof(THandler));
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Optimistic<TQuery, TQueryResult>(
        Expression<Func<TQueryResult, TCommand, bool>> match,
        Expression<Func<TQueryResult, TCommand, TQueryResult>> apply)
        where TQuery : IQuery<TQueryResult>
    {
        _optimistic = new OptimisticRuleDescriptor(typeof(TQuery), match, apply);
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Retry(Action<RetryConfigurationBuilder> configure)
    {
        var b = new RetryConfigurationBuilder();
        configure(b);
        _retry = b.Build();
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Timeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    public CommandConfigurationBuilder<TCommand, TResult> Telemetry(Action<TelemetryConfigurationBuilder> configure)
        => this;

    CommandRegistrationMetadata ICommandConfigurationSource.BuildMetadata() => new()
    {
        CommandType          = typeof(TCommand),
        ResultType           = typeof(TResult),
        IsQueueable          = typeof(IQueueable).IsAssignableFrom(typeof(TCommand)),
        Validators           = _validators?.ToArray(),
        Authorizers          = _authorizers?.ToArray(),
        Invalidates          = _invalidates?.ToArray(),
        IdempotencyKey       = _idempotencyKey,
        ConflictPolicy       = _conflict,
        ConflictResolverType = _conflictResolverType,
        OfflineHandler       = _offlineHandler,
        RetryPolicy          = _retry,
        Timeout              = _timeout,
        Optimistic           = _optimistic,
    };
}
