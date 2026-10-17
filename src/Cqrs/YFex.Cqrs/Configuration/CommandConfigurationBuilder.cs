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

// â”€â”€ No-result command builder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Fluent builder for <see cref="ICommand"/> (no typed result).</summary>
public sealed class CommandConfigurationBuilder<TCommand>
    : ICommandConfigurationSource
    where TCommand : ICommand
{
    private List<ContextRuleFactory>?           _validators;
    private RulesConfig?                        _rules;
    private RulesStage?                         _approval;
    private List<Requirement>?         _authorization;
    private List<InvalidationRuleDescriptor>?   _invalidates;
    private Func<object, string>?               _idempotencyKey;
    private ConflictPolicy                      _conflict = ConflictPolicy.Escalate;
    private Type?                               _conflictResolverType;
    private OfflineHandlerDescriptor?           _offlineHandler;
    private RetryPolicy?                        _retry;
    private TimeSpan?                           _timeout;
    private string?                             _resourceName;
    private YFex.Resilience.FallbackPolicy?     _fallback;
    private YFex.Resilience.HedgingPolicy?      _hedging;
    private OptimisticRuleDescriptor?           _optimistic;
    private ReverseRuleDescriptor?              _reverse;
    private string?                             _description;
    private string[]?                          _keywords;
    private bool                                _irreversible;
    private List<KeyValuePair<Type, object>>?   _features;

    // â”€â”€ Validation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Shape rules for the message (same semantics as attributes, D14/D23). Constant rules are compiled into the
    /// message by YFex.Validation.Generator; the rest (non-constant arguments, lambdas) run from
    /// <see cref="RuntimeRules{T}"/>. Declare in the BASELINE configuration (shared by client and server) or clients
    /// and server drift apart.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> Validation(Action<ValidationBuilder<TCommand>> rules)
    {
        ValidationBuilder<TCommand>.Apply(rules);
        return this;
    }

    /// <summary>Inline context rule (stage 5, server-side async I/O) returning a result.</summary>
    public CommandConfigurationBuilder<TCommand> Validate(
        Func<TCommand, CancellationToken, ValueTask<ValidationResult>> validator)
        => AddContextRule(ContextRuleFactory<TCommand>.Shared(new InlineResultRule<TCommand>(validator)));

    /// <summary>Inline context rule (stage 5) writing into the collector.</summary>
    public CommandConfigurationBuilder<TCommand> Validate(
        Func<TCommand, ViolationCollector, CancellationToken, ValueTask> rule)
        => AddContextRule(ContextRuleFactory<TCommand>.Shared(new InlineCollectorRule<TCommand>(rule)));

    /// <summary>
    /// Context rule type (stage 5): resolved from DI when registered, otherwise constructed with DI arguments.
    /// Declare in the SERVER overrides (I/O the client can't do — 09 §3).
    /// </summary>
    public CommandConfigurationBuilder<TCommand> Validate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>()
        where TRule : class, IContextRule<TCommand>
        => AddContextRule(ContextRuleFactory<TCommand>.FromServices<TRule>());

    [Obsolete("Implement YFex.Validation.IContextRule<T> and register it with .Validate<TRule>().")]
    public CommandConfigurationBuilder<TCommand> UseValidator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>()
        where TValidator : class, ICommandValidator<TCommand>
        => AddContextRule(new ContextRuleFactory<TCommand>(static sp => new LegacyValidatorRule<TCommand>(
            sp.GetService<TValidator>() ?? ActivatorUtilities.CreateInstance<TValidator>(sp))));

    // ── Rules enforcement (stages 3–4, validation 09 §6, D33) ──────────────────

    /// <summary>
    /// Stage 3: loads the <typeparamref name="TSituation"/> the rules evaluate (all I/O — Principle A). The loader
    /// comes from DI when registered, else it is constructed with DI-resolved constructor arguments.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> UseSituation<TSituation, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLoader>()
        where TSituation : global::YFex.Rules.ISituation<TSituation>
        where TLoader : class, global::YFex.Rules.ISituationLoader<TCommand, TSituation>
    {
        _rules = new RulesConfig<TCommand, TSituation>(static sp => sp.GetService<TLoader>() ?? ActivatorUtilities.CreateInstance<TLoader>(sp))
        {
            Options = _rules?.Options ?? global::YFex.Rules.EnforceOptions.Default,
        };
        return this;
    }

    /// <summary>
    /// Stage 4: evaluates <paramref name="ruleSet"/> (through a registered <c>RuleSetHost&lt;TSituation&gt;</c> when present):
    /// Denied → validation problem; NeedsApproval → approval tokens required; fingerprint mismatch → policy.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> Enforce<TSituation>(global::YFex.Rules.RuleSetDefinition<TSituation> ruleSet, global::YFex.Rules.EnforceOptions? options = null)
        where TSituation : global::YFex.Rules.ISituation<TSituation>
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var rules = RulesConfig<TCommand, TSituation>.Expect(_rules, typeof(TCommand));
        rules.Set = ruleSet;
        if (options is not null) rules.Options = options;
        return this;
    }

    /// <summary>
    /// Renames situation paths to this command's paths in the violations a rule rejection returns (04 §14), e.g.
    /// <c>m => m.Path("Total", "Amount").Path("Lines[]", "Items[]")</c> — so they land on the right field.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> MapSituationPaths(Func<global::YFex.Rules.SituationPathMap, global::YFex.Rules.SituationPathMap> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var rules = _rules ?? throw new InvalidOperationException($"'{typeof(TCommand).Name}': .MapSituationPaths requires .UseSituation().Enforce().");
        rules.Options = rules.Options with { Paths = map(rules.Options.Paths) };
        return this;
    }

    /// <summary>What to do when an <see cref="IDecisionBound"/> command's fingerprint differs from the server's (09 §8).</summary>
    public CommandConfigurationBuilder<TCommand> OnDecisionMismatch(DecisionMismatchPolicy policy)
    {
        var rules = _rules ?? throw new InvalidOperationException($"'{typeof(TCommand).Name}': .OnDecisionMismatch requires .UseSituation().Enforce().");
        rules.Options = rules.Options with { OnMismatch = policy };
        return this;
    }

    private CommandConfigurationBuilder<TCommand> AddContextRule(ContextRuleFactory factory)
    {
        (_validators ??= []).Add(factory);
        return this;
    }

    // â”€â”€ Authorization â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // ── Authorization: the verbs record Requirements; CqrsAuthorizationConfiguration exports them to the ONE message policy
    //    every dispatcher enforces through IMessageAuthorization (authorization-consolidation 04 §2) ──

    /// <summary>The caller must be authenticated.</summary>
    public CommandConfigurationBuilder<TCommand> RequireAuthenticated() => AddRequirement(Requirement.RequireAuthenticated());

    /// <summary>The caller must be in at least one of <paramref name="roles"/> (case-insensitive).</summary>
    public CommandConfigurationBuilder<TCommand> RequireRoles(params Role[] roles) => AddRequirement(Requirement.RequireRoles(roles));

    /// <summary>The caller must hold <paramref name="permission"/> (a catalog property, e.g. <c>SalesPermissions.SaleVoid</c>).</summary>
    public CommandConfigurationBuilder<TCommand> RequirePermission(Permission permission) => AddRequirement(Requirement.Require(permission));

    /// <summary>
    /// The command also needs an approval of <paramref name="permission"/> bound to the message type + <paramref name="bind"/>'s value
    /// (authorization-consolidation 06 §1.3, decision A5): without a matching token the dispatcher answers
    /// <see cref="ErrorKind.ApprovalRequired"/> with the digest; the approver runs <c>Approvals.Commands.Approve(permission, digest)</c> and
    /// the client resends the command with the token. The command must implement <see cref="IApprovalBound"/>.
    /// <paramref name="separationOfDuties"/> (default on, decision A7): the approver may not be the requester. An
    /// <see cref="IApprovalPolicySource"/> may change the policy at run time.
    /// <code>
    /// b.Command&lt;VoidSale, Receipt&gt;().RequirePermission(SalesPermissions.SaleVoid)
    ///     .RequireApproval(SalesPermissions.SaleVoidApprove, bind: command =&gt; command.SaleId);
    /// </code>
    /// </summary>
    public CommandConfigurationBuilder<TCommand> RequireApproval(Permission permission, Func<TCommand, string> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Text(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand> RequireApproval(Permission permission, Func<TCommand, Guid> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Guid(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand> RequireApproval(Permission permission, Func<TCommand, long> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Integer(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand> RequireApproval<TValue>(Permission permission, Func<TCommand, TValue> bind, bool separationOfDuties = true)
        where TValue : global::YFex.Canonical.ICanonicalValue<TValue>
        => SetApproval(permission, ApprovalBindings.Canonical(bind), separationOfDuties);

    private CommandConfigurationBuilder<TCommand> SetApproval(Permission permission, Func<TCommand, global::YFex.Cryptography.Hash256> digest, bool separationOfDuties)
    {
        _approval = ApprovalStages.Create(permission, digest, separationOfDuties);
        return this;
    }

    /// <summary>The caller must hold at least one of <paramref name="permissions"/>.</summary>
    public CommandConfigurationBuilder<TCommand> RequireAnyPermission(params Permission[] permissions) => AddRequirement(Requirement.RequireAny(permissions));

    /// <summary>
    /// A named policy decided on the server by an <c>INamedPolicyHandler</c>. Hidden on the quick (client/UI) check, so declare it
    /// in the server overrides when clients must still send the message.
    /// </summary>
    public CommandConfigurationBuilder<TCommand> RequirePolicy(PolicyName policy) => AddRequirement(Requirement.RequirePolicy(policy));

    /// <summary>An inline predicate over (caller, message). Does not affect visibility (menus, search, AI).</summary>
    public CommandConfigurationBuilder<TCommand> Authorize(Func<IAuthorizationContext, TCommand, bool> predicate)
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromDelegate(predicate)));

    /// <summary>
    /// A requirement class — <see cref="IAuthorizationRequirement{TResource}"/> or <see cref="IAsyncAuthorizationRequirement{TResource}"/>
    /// of <typeparamref name="TCommand"/> — registered as a DI singleton (its <c>VisibleWith</c> refines visibility).
    /// </summary>
    public CommandConfigurationBuilder<TCommand> Authorize<TRequirement>() where TRequirement : class
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromService<TRequirement, TCommand>()));

    private CommandConfigurationBuilder<TCommand> AddRequirement(in Requirement requirement)
    {
        (_authorization ??= []).Add(requirement);
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

    /// <summary>Precise invalidation: drops only the <typeparamref name="TQuery"/> variant whose entity
    /// key (declared on the query via <c>TaggedBy</c>) equals <paramref name="key"/> for this command.</summary>
    public CommandConfigurationBuilder<TCommand> Invalidates<TQuery, TResult>(Func<TCommand, object> key)
        where TQuery : IQuery<TResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(
            typeof(TQuery), c => key((TCommand)c)?.ToString() ?? string.Empty));
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
        // Capture the typed resolve + invoke here, where THandler/TCommand are generic parameters, so the
        // offline path needs no reflection (no GetMethod/Invoke). Resolve by the interface first (typical
        // DI registration), fall back to the concrete type — mirrors the LocalDispatcher resolution.
        _offlineHandler = OfflineHandlerDescriptor.FromType(typeof(THandler),
            static (sp, cmd, ct) =>
            {
                var handler = (IOfflineHandler<TCommand>)(sp.GetService(typeof(IOfflineHandler<TCommand>))
                    ?? sp.GetService(typeof(THandler))
                    ?? throw new InvalidOperationException(
                        $"YFex.Cqrs: offline handler '{typeof(THandler).FullName}' is not registered in the service provider."));
                return handler.HandleAsync((TCommand)cmd, ct);
            });
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

    // ── Reversibility (compensation) ──────────────────────────────────────────
    //
    // One reverse rule per command (single value, last-write-wins — mirrors Optimistic).
    // DECLARATION only: the journal + reverse dispatch live in YFex.Cqrs.Compensation.
    // The reverse of a command is itself a (registered) command; the map is compiled once
    // into ReversePolicy at Build time (see CompiledMessagingRegistry.CompileReverse).

    /// <summary>Declares that this command is reversed by <typeparamref name="TReverse"/>, produced
    /// from the original command via <paramref name="toReverse"/>. <typeparamref name="TReverse"/>
    /// must itself be a registered command (validated fail-fast at Build).
    /// NOTE: use explicit constructors — <c>with</c> expressions are unsupported in expression trees.</summary>
    public CommandConfigurationBuilder<TCommand> Reversible<TReverse>(
        Expression<Func<TCommand, TReverse>> toReverse)
        where TReverse : ICommand
    {
        // Capture the typed dispatch here, where TReverse is a compile-time generic parameter, so the
        // undo path dispatches the already-typed reverse command with no MakeGenericMethod.
        _reverse = ReverseRuleDescriptor.FromMap(typeof(TReverse), toReverse,
            static async (disp, rev, ct) => { await disp.CommandAsync((TReverse)rev, ct).ConfigureAwait(false); });
        return this;
    }

    /// <summary>Declares a custom reverse handler for this command.</summary>
    public CommandConfigurationBuilder<TCommand> ReversibleWith<THandler>()
        where THandler : IReverseHandler<TCommand>
    {
        // Capture the typed handler resolve + invoke here, where THandler/TCommand are generic
        // parameters, so the undo path needs no reflection (no Activator, no GetMethod/Invoke).
        _reverse = ReverseRuleDescriptor.FromHandler(typeof(THandler),
            static (sp, original, _, ct) =>
            {
                var handler = (IReverseHandler<TCommand>)(sp.GetService(typeof(THandler))
                    ?? throw new InvalidOperationException(
                        $"YFex.Cqrs: reverse handler '{typeof(THandler).FullName}' is not registered in the service provider."));
                return handler.CompensateAsync((TCommand)original, ct);
            });
        return this;
    }

    // ── Capability metadata (surfaced via CqrsCapabilityInfo) ─────────────────
    //
    // Authoring-only: consumed by the capabilities bridge, never on the dispatch hot path.
    // Single-value, last-write-wins (mirrors _optimistic / _reverse).

    /// <summary>Authors an AI/human-facing description for this command (surfaced to the capabilities bridge).</summary>
    public CommandConfigurationBuilder<TCommand> Describe(string ai)
    {
        _description = ai;
        return this;
    }

    /// <summary>Authors search keywords for this command (surfaced to the capabilities bridge).</summary>
    public CommandConfigurationBuilder<TCommand> Keywords(params string[] keywords)
    {
        _keywords = keywords;
        return this;
    }

    /// <summary>Marks this command irreversible — raises the capabilities bridge's default Mutating side-effect to Irreversible.</summary>
    public CommandConfigurationBuilder<TCommand> Irreversible()
    {
        _irreversible = true;
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Retry(Action<RetryConfigurationBuilder> configure)
    {
        var b = new RetryConfigurationBuilder();
        configure(b);
        _retry = b.Build();
        return this;
    }

    public CommandConfigurationBuilder<TCommand> Timeout(TimeSpan timeout) { _timeout = timeout; return this; }

    /// <summary>References a named, stateful resilience resource (circuit breaker / rate limiter / bulkhead).</summary>
    public CommandConfigurationBuilder<TCommand> UseResource(string resourceName) { _resourceName = resourceName; return this; }

    /// <summary>Sets a fallback policy handle (a token into the resilience fallback table).</summary>
    public CommandConfigurationBuilder<TCommand> Fallback(YFex.Resilience.FallbackPolicy fallback) { _fallback = fallback; return this; }

    /// <summary>Fires parallel hedged attempts after a delay; takes the first success (latency-sensitive).</summary>
    public CommandConfigurationBuilder<TCommand> Hedging(YFex.Resilience.HedgingPolicy hedging) { _hedging = hedging; return this; }

    public CommandConfigurationBuilder<TCommand> Telemetry(Action<TelemetryConfigurationBuilder> configure)
        => this;

    // â”€â”€ Metadata production â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    CommandRegistrationMetadata ICommandConfigurationSource.BuildMetadata() => new()
    {
        CommandType          = typeof(TCommand),
        ResultType           = null,
        IsQueueable          = typeof(IQueueable).IsAssignableFrom(typeof(TCommand)),
        Validators           = _validators?.ToArray(),
        Rules                = ApprovalStages.Combine(_rules?.Build(typeof(TCommand), isQuery: false), _approval),
        Authorization          = _authorization?.ToArray(),
        Invalidates          = _invalidates?.ToArray(),
        IdempotencyKey       = _idempotencyKey,
        ConflictPolicy       = _conflict,
        ConflictResolverType = _conflictResolverType,
        OfflineHandler       = _offlineHandler,
        RetryPolicy          = _retry,
        Timeout              = _timeout,
        Resilience           = ResilienceMapping.Compose(_retry, _timeout, _resourceName, _fallback, _hedging),
        Optimistic           = _optimistic,
        Reverse              = _reverse,
        Description          = _description,
        Keywords             = _keywords,
        Irreversible         = _irreversible,
        Features             = _features?.ToArray(),
    };
}

// â”€â”€ Result-bearing command builder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// <summary>Fluent builder for <see cref="ICommand{TResult}"/>.</summary>
public sealed class CommandConfigurationBuilder<TCommand, TResult>
    : ICommandConfigurationSource
    where TCommand : ICommand<TResult>
{
    private List<ContextRuleFactory>?           _validators;
    private RulesConfig?                        _rules;
    private RulesStage?                         _approval;
    private List<Requirement>?         _authorization;
    private List<InvalidationRuleDescriptor>?   _invalidates;
    private Func<object, string>?               _idempotencyKey;
    private ConflictPolicy                      _conflict = ConflictPolicy.Escalate;
    private Type?                               _conflictResolverType;
    private OfflineHandlerDescriptor?           _offlineHandler;
    private RetryPolicy?                        _retry;
    private TimeSpan?                           _timeout;
    private string?                             _resourceName;
    private YFex.Resilience.FallbackPolicy?     _fallback;
    private YFex.Resilience.HedgingPolicy?      _hedging;
    private OptimisticRuleDescriptor?           _optimistic;
    private ReverseRuleDescriptor?              _reverse;
    private string?                             _description;
    private string[]?                          _keywords;
    private bool                                _irreversible;
    private List<KeyValuePair<Type, object>>?   _features;

    /// <summary>
    /// Shape rules for the message (same semantics as attributes, D14/D23). Constant rules are compiled into the
    /// message by YFex.Validation.Generator; the rest (non-constant arguments, lambdas) run from
    /// <see cref="RuntimeRules{T}"/>. Declare in the BASELINE configuration (shared by client and server) or clients
    /// and server drift apart.
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> Validation(Action<ValidationBuilder<TCommand>> rules)
    {
        ValidationBuilder<TCommand>.Apply(rules);
        return this;
    }

    /// <summary>Inline context rule (stage 5, server-side async I/O) returning a result.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Validate(
        Func<TCommand, CancellationToken, ValueTask<ValidationResult>> validator)
        => AddContextRule(ContextRuleFactory<TCommand>.Shared(new InlineResultRule<TCommand>(validator)));

    /// <summary>Inline context rule (stage 5) writing into the collector.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Validate(
        Func<TCommand, ViolationCollector, CancellationToken, ValueTask> rule)
        => AddContextRule(ContextRuleFactory<TCommand>.Shared(new InlineCollectorRule<TCommand>(rule)));

    /// <summary>
    /// Context rule type (stage 5): resolved from DI when registered, otherwise constructed with DI arguments.
    /// Declare in the SERVER overrides (I/O the client can't do — 09 §3).
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> Validate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>()
        where TRule : class, IContextRule<TCommand>
        => AddContextRule(ContextRuleFactory<TCommand>.FromServices<TRule>());

    [Obsolete("Implement YFex.Validation.IContextRule<T> and register it with .Validate<TRule>().")]
    public CommandConfigurationBuilder<TCommand, TResult> UseValidator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>()
        where TValidator : class, ICommandValidator<TCommand>
        => AddContextRule(new ContextRuleFactory<TCommand>(static sp => new LegacyValidatorRule<TCommand>(
            sp.GetService<TValidator>() ?? ActivatorUtilities.CreateInstance<TValidator>(sp))));

    // ── Rules enforcement (stages 3–4, validation 09 §6, D33) ──────────────────

    /// <summary>
    /// Stage 3: loads the <typeparamref name="TSituation"/> the rules evaluate (all I/O — Principle A). The loader
    /// comes from DI when registered, else it is constructed with DI-resolved constructor arguments.
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> UseSituation<TSituation, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLoader>()
        where TSituation : global::YFex.Rules.ISituation<TSituation>
        where TLoader : class, global::YFex.Rules.ISituationLoader<TCommand, TSituation>
    {
        _rules = new RulesConfig<TCommand, TSituation>(static sp => sp.GetService<TLoader>() ?? ActivatorUtilities.CreateInstance<TLoader>(sp))
        {
            Options = _rules?.Options ?? global::YFex.Rules.EnforceOptions.Default,
        };
        return this;
    }

    /// <summary>
    /// Stage 4: evaluates <paramref name="ruleSet"/> (through a registered <c>RuleSetHost&lt;TSituation&gt;</c> when present):
    /// Denied → validation problem; NeedsApproval → approval tokens required; fingerprint mismatch → policy.
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> Enforce<TSituation>(global::YFex.Rules.RuleSetDefinition<TSituation> ruleSet, global::YFex.Rules.EnforceOptions? options = null)
        where TSituation : global::YFex.Rules.ISituation<TSituation>
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var rules = RulesConfig<TCommand, TSituation>.Expect(_rules, typeof(TCommand));
        rules.Set = ruleSet;
        if (options is not null) rules.Options = options;
        return this;
    }

    /// <summary>
    /// Renames situation paths to this command's paths in the violations a rule rejection returns (04 §14), e.g.
    /// <c>m => m.Path("Total", "Amount").Path("Lines[]", "Items[]")</c> — so they land on the right field.
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> MapSituationPaths(Func<global::YFex.Rules.SituationPathMap, global::YFex.Rules.SituationPathMap> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var rules = _rules ?? throw new InvalidOperationException($"'{typeof(TCommand).Name}': .MapSituationPaths requires .UseSituation().Enforce().");
        rules.Options = rules.Options with { Paths = map(rules.Options.Paths) };
        return this;
    }

    /// <summary>What to do when an <see cref="IDecisionBound"/> command's fingerprint differs from the server's (09 §8).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> OnDecisionMismatch(DecisionMismatchPolicy policy)
    {
        var rules = _rules ?? throw new InvalidOperationException($"'{typeof(TCommand).Name}': .OnDecisionMismatch requires .UseSituation().Enforce().");
        rules.Options = rules.Options with { OnMismatch = policy };
        return this;
    }

    private CommandConfigurationBuilder<TCommand, TResult> AddContextRule(ContextRuleFactory factory)
    {
        (_validators ??= []).Add(factory);
        return this;
    }

    // ── Authorization: the verbs record Requirements; CqrsAuthorizationConfiguration exports them to the ONE message policy
    //    every dispatcher enforces through IMessageAuthorization (authorization-consolidation 04 §2) ──

    /// <summary>The caller must be authenticated.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequireAuthenticated() => AddRequirement(Requirement.RequireAuthenticated());

    /// <summary>The caller must be in at least one of <paramref name="roles"/> (case-insensitive).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequireRoles(params Role[] roles) => AddRequirement(Requirement.RequireRoles(roles));

    /// <summary>The caller must hold <paramref name="permission"/> (a catalog property, e.g. <c>SalesPermissions.SaleVoid</c>).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequirePermission(Permission permission) => AddRequirement(Requirement.Require(permission));

    /// <summary>
    /// The command also needs an approval of <paramref name="permission"/> bound to the message type + <paramref name="bind"/>'s value
    /// (authorization-consolidation 06 §1.3, decision A5): without a matching token the dispatcher answers
    /// <see cref="ErrorKind.ApprovalRequired"/> with the digest; the approver runs <c>Approvals.Commands.Approve(permission, digest)</c> and
    /// the client resends the command with the token. The command must implement <see cref="IApprovalBound"/>.
    /// <paramref name="separationOfDuties"/> (default on, decision A7): the approver may not be the requester. An
    /// <see cref="IApprovalPolicySource"/> may change the policy at run time.
    /// <code>
    /// b.Command&lt;VoidSale, Receipt&gt;().RequirePermission(SalesPermissions.SaleVoid)
    ///     .RequireApproval(SalesPermissions.SaleVoidApprove, bind: command =&gt; command.SaleId);
    /// </code>
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequireApproval(Permission permission, Func<TCommand, string> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Text(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand, TResult> RequireApproval(Permission permission, Func<TCommand, Guid> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Guid(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand, TResult> RequireApproval(Permission permission, Func<TCommand, long> bind, bool separationOfDuties = true)
        => SetApproval(permission, ApprovalBindings.Integer(bind), separationOfDuties);

    /// <inheritdoc cref="RequireApproval(Permission, Func{TCommand, string}, bool)"/>
    public CommandConfigurationBuilder<TCommand, TResult> RequireApproval<TValue>(Permission permission, Func<TCommand, TValue> bind, bool separationOfDuties = true)
        where TValue : global::YFex.Canonical.ICanonicalValue<TValue>
        => SetApproval(permission, ApprovalBindings.Canonical(bind), separationOfDuties);

    private CommandConfigurationBuilder<TCommand, TResult> SetApproval(Permission permission, Func<TCommand, global::YFex.Cryptography.Hash256> digest, bool separationOfDuties)
    {
        _approval = ApprovalStages.Create(permission, digest, separationOfDuties);
        return this;
    }

    /// <summary>The caller must hold at least one of <paramref name="permissions"/>.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequireAnyPermission(params Permission[] permissions) => AddRequirement(Requirement.RequireAny(permissions));

    /// <summary>
    /// A named policy decided on the server by an <c>INamedPolicyHandler</c>. Hidden on the quick (client/UI) check, so declare it
    /// in the server overrides when clients must still send the message.
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> RequirePolicy(PolicyName policy) => AddRequirement(Requirement.RequirePolicy(policy));

    /// <summary>An inline predicate over (caller, message). Does not affect visibility (menus, search, AI).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Authorize(Func<IAuthorizationContext, TCommand, bool> predicate)
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromDelegate(predicate)));

    /// <summary>
    /// A requirement class — <see cref="IAuthorizationRequirement{TResource}"/> or <see cref="IAsyncAuthorizationRequirement{TResource}"/>
    /// of <typeparamref name="TCommand"/> — registered as a DI singleton (its <c>VisibleWith</c> refines visibility).
    /// </summary>
    public CommandConfigurationBuilder<TCommand, TResult> Authorize<TRequirement>() where TRequirement : class
        => AddRequirement(Requirement.RequireResource(ResourceRequirement.FromService<TRequirement, TCommand>()));

    private CommandConfigurationBuilder<TCommand, TResult> AddRequirement(in Requirement requirement)
    {
        (_authorization ??= []).Add(requirement);
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

    /// <summary>Precise invalidation: drops only the <typeparamref name="TQuery"/> variant whose entity
    /// key (declared on the query via <c>TaggedBy</c>) equals <paramref name="key"/> for this command.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Invalidates<TQuery, TQueryResult>(Func<TCommand, object> key)
        where TQuery : IQuery<TQueryResult>
    {
        (_invalidates ??= []).Add(new InvalidationRuleDescriptor(
            typeof(TQuery), c => key((TCommand)c)?.ToString() ?? string.Empty));
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
        // Capture the typed resolve + invoke here, where THandler/TCommand/TResult are generic parameters,
        // so the offline path needs no reflection. Resolve by the interface first (typical DI registration),
        // fall back to the concrete type — mirrors the LocalDispatcher resolution. The result-bearing
        // HandleAsync is awaited and its value discarded (the offline path returns Queue/Fail, not the result).
        _offlineHandler = OfflineHandlerDescriptor.FromType(typeof(THandler),
            static async (sp, cmd, ct) =>
            {
                var handler = (IOfflineHandler<TCommand, TResult>)(sp.GetService(typeof(IOfflineHandler<TCommand, TResult>))
                    ?? sp.GetService(typeof(THandler))
                    ?? throw new InvalidOperationException(
                        $"YFex.Cqrs: offline handler '{typeof(THandler).FullName}' is not registered in the service provider."));
                await handler.HandleAsync((TCommand)cmd, ct).ConfigureAwait(false);
            });
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

    // ── Reversibility (compensation) ──────────────────────────────────────────
    //
    // One reverse rule per command (single value, last-write-wins — mirrors Optimistic).
    // DECLARATION only: the journal + reverse dispatch live in YFex.Cqrs.Compensation.

    /// <summary>Reverses this command via <typeparamref name="TReverse"/>, mapped from the original
    /// command only. <typeparamref name="TReverse"/> must itself be a registered command.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Reversible<TReverse>(
        Expression<Func<TCommand, TReverse>> toReverse)
        where TReverse : ICommand
    {
        _reverse = ReverseRuleDescriptor.FromMap(typeof(TReverse), toReverse,
            static async (disp, rev, ct) => { await disp.CommandAsync((TReverse)rev, ct).ConfigureAwait(false); },
            typeof(TResult));
        return this;
    }

    /// <summary>Reverses this command via <typeparamref name="TReverse"/>, mapped from the original
    /// command and the result it produced. <typeparamref name="TReverse"/> must itself be a registered
    /// command. NOTE: use explicit constructors — <c>with</c> expressions are unsupported in expression trees.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Reversible<TReverse>(
        Expression<Func<TCommand, TResult, TReverse>> toReverse)
        where TReverse : ICommand
    {
        _reverse = ReverseRuleDescriptor.FromMap(typeof(TReverse), toReverse,
            static async (disp, rev, ct) => { await disp.CommandAsync((TReverse)rev, ct).ConfigureAwait(false); },
            typeof(TResult));
        return this;
    }

    /// <summary>Declares a custom reverse handler for this command.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> ReversibleWith<THandler>()
        where THandler : IReverseHandler<TCommand, TResult>
    {
        // Typed resolve + invoke captured here; the undo path needs no reflection.
        _reverse = ReverseRuleDescriptor.FromHandler(typeof(THandler),
            static (sp, original, result, ct) =>
            {
                var handler = (IReverseHandler<TCommand, TResult>)(sp.GetService(typeof(THandler))
                    ?? throw new InvalidOperationException(
                        $"YFex.Cqrs: reverse handler '{typeof(THandler).FullName}' is not registered in the service provider."));
                return handler.CompensateAsync((TCommand)original, (TResult?)result, ct);
            },
            typeof(TResult));
        return this;
    }

    // ── Capability metadata (surfaced via CqrsCapabilityInfo) ─────────────────
    //
    // Authoring-only: consumed by the capabilities bridge, never on the dispatch hot path.
    // Single-value, last-write-wins (mirrors _optimistic / _reverse).

    /// <summary>Authors an AI/human-facing description for this command (surfaced to the capabilities bridge).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Describe(string ai)
    {
        _description = ai;
        return this;
    }

    /// <summary>Authors search keywords for this command (surfaced to the capabilities bridge).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Keywords(params string[] keywords)
    {
        _keywords = keywords;
        return this;
    }

    /// <summary>Marks this command irreversible — raises the capabilities bridge's default Mutating side-effect to Irreversible.</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Irreversible()
    {
        _irreversible = true;
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

    /// <summary>References a named, stateful resilience resource (circuit breaker / rate limiter / bulkhead).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> UseResource(string resourceName) { _resourceName = resourceName; return this; }

    /// <summary>Sets a fallback policy handle (a token into the resilience fallback table).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Fallback(YFex.Resilience.FallbackPolicy fallback) { _fallback = fallback; return this; }

    /// <summary>Fires parallel hedged attempts after a delay; takes the first success (latency-sensitive).</summary>
    public CommandConfigurationBuilder<TCommand, TResult> Hedging(YFex.Resilience.HedgingPolicy hedging) { _hedging = hedging; return this; }

    public CommandConfigurationBuilder<TCommand, TResult> Telemetry(Action<TelemetryConfigurationBuilder> configure)
        => this;

    CommandRegistrationMetadata ICommandConfigurationSource.BuildMetadata() => new()
    {
        CommandType          = typeof(TCommand),
        ResultType           = typeof(TResult),
        IsQueueable          = typeof(IQueueable).IsAssignableFrom(typeof(TCommand)),
        Validators           = _validators?.ToArray(),
        Rules                = ApprovalStages.Combine(_rules?.Build(typeof(TCommand), isQuery: false), _approval),
        Authorization          = _authorization?.ToArray(),
        Invalidates          = _invalidates?.ToArray(),
        IdempotencyKey       = _idempotencyKey,
        ConflictPolicy       = _conflict,
        ConflictResolverType = _conflictResolverType,
        OfflineHandler       = _offlineHandler,
        RetryPolicy          = _retry,
        Timeout              = _timeout,
        Resilience           = ResilienceMapping.Compose(_retry, _timeout, _resourceName, _fallback, _hedging),
        Optimistic           = _optimistic,
        Reverse              = _reverse,
        Description          = _description,
        Keywords             = _keywords,
        Irreversible         = _irreversible,
        Features             = _features?.ToArray(),
    };
}
