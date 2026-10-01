using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using YFex.Authorization.Public;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.Registration;
using YFex.Cqrs.Runtime;
using YFex.Validation;

namespace YFex.Cqrs;

public enum ConfigurationValidationLevel { Off, Loose, Strict }

/// <summary>
/// Builder-authored capability facts for a single message type, compiled once at startup into
/// <see cref="CompiledMessagingRegistry.Capabilities"/>. Everything a capability/search/AI consumer
/// needs without reflecting over the message type or cracking open the opaque authorizer delegate.
/// </summary>
/// <param name="RequiredPermission">The permission from the first builder <c>.RequirePermission(...)</c>, or null.</param>
/// <param name="Description">Authored via <c>.Describe(...)</c>, or null.</param>
/// <param name="Keywords">Authored via <c>.Keywords(...)</c>, or null.</param>
/// <param name="Irreversible">Authored via command <c>.Irreversible()</c> (always false for queries/events).</param>
/// <param name="Reversible">True when a reverse rule (<c>.Reversible(...)</c>/<c>.ReversibleWith(...)</c>) is declared.</param>
/// <param name="ReverseCommandType">The reverse command's CLR type for the map form; null for the handler form or when not reversible.</param>
/// <param name="IsQueueable">True when the message type implements <see cref="IQueueable"/>.</param>
/// <param name="IsCacheable">True when the message type implements <see cref="ICacheable"/>.</param>
public sealed record CqrsCapabilityInfo(
    YFex.Authorization.Public.Permission? RequiredPermission,
    string? Description,
    string[]? Keywords,
    bool Irreversible,
    bool Reversible,
    Type? ReverseCommandType,
    bool IsQueueable,
    bool IsCacheable);

/// <summary>
/// Immutable registry produced once at startup by <see cref="Build"/>.
/// Runtime lookup: single <see cref="FrozenDictionary{TKey,TValue}"/> access (~5 ns)
/// followed by direct field reads on sealed records — zero reflection, zero LINQ.
/// </summary>
public sealed class CompiledMessagingRegistry
{
    public FrozenDictionary<Type, CommandPolicy> Commands { get; }
    public FrozenDictionary<Type, QueryPolicy>   Queries  { get; }
    public FrozenDictionary<Type, EventPolicy>   Events   { get; }

    /// <summary>
    /// Capability metadata for every registered command / query / event, keyed by message type.
    /// Separate from the hot-path <see cref="Commands"/>/<see cref="Queries"/> tables: this table
    /// carries builder-authored enrichment (description, keywords, irreversibility), the
    /// builder-declared required permission (otherwise compiled into an opaque authorizer delegate),
    /// and reversibility — so consumers such as the capabilities bridge stop guessing/reflecting.
    /// </summary>
    public FrozenDictionary<Type, CqrsCapabilityInfo> Capabilities { get; }

    private CompiledMessagingRegistry(
        FrozenDictionary<Type, CommandPolicy> commands,
        FrozenDictionary<Type, QueryPolicy>   queries,
        FrozenDictionary<Type, EventPolicy>   events,
        FrozenDictionary<Type, CqrsCapabilityInfo> capabilities)
    { Commands = commands; Queries = queries; Events = events; Capabilities = capabilities; }

    // ─────────────────────────────────────────────────────────────────────────
    // Build — three-phase: Collect → Expand → Compile
    // ─────────────────────────────────────────────────────────────────────────

    public static CompiledMessagingRegistry Build(
        IEnumerable<IAggregateConfiguration>  baseline,
        IEnumerable<IAggregateConfiguration>? serverOverrides     = null,
        IEnumerable<IAggregateConfiguration>? clientOverrides     = null,
        Assembly[]?                           scanForImplementers = null,
        IPolicyEvaluator?                     policyEvaluator     = null,
        Func<ClaimsPrincipal, IAuthContext>?  principalFactory    = null)
    {
        var queryTable   = new Dictionary<Type, QueryAccumulator>();
        var commandTable = new Dictionary<Type, CommandAccumulator>();
        var eventTable   = new Dictionary<Type, EventPolicy>();

        // ── Phase 1: Collect ─────────────────────────────────────────────────
        // Uses MakeGenericMethod so CollectFromTyped<TAggregate> can call
        // cfg.Configure(builder) with full static typing — no per-request reflection.
        Collect(baseline,        queryTable, commandTable, eventTable);
        Collect(serverOverrides, queryTable, commandTable, eventTable);
        Collect(clientOverrides, queryTable, commandTable, eventTable);

        // ── Phase 2: Expand groups / unions ──────────────────────────────────
        // One-time startup scan — never runs on the hot path.
        var assemblies = scanForImplementers ?? [];
        foreach (var acc in commandTable.Values)
            if (acc.Invalidates is not null) ExpandRules(acc.Invalidates, assemblies);
        foreach (var acc in queryTable.Values)
            if (acc.InvalidatedBy is not null) ExpandRules(acc.InvalidatedBy, assemblies);

        // ── Phase 2.5: Fail-fast — a reverse command target must itself be registered ─
        // Mirrors the intent behind invalidation-target resolution: a reverse rule that names a
        // command with no configuration would materialize an undispatchable message at undo time.
        foreach (var (type, acc) in commandTable)
        {
            if (acc.Reverse is { ReverseCommandType: { } reverseType }
                && !commandTable.ContainsKey(reverseType))
            {
                throw new InvalidOperationException(
                    $"YFex.Cqrs: command '{type.FullName}' declares reverse command "
                  + $"'{reverseType.FullName}', which is not a registered command. "
                  + "Register it via an IAggregateConfiguration before using it as a reverse target.");
            }
        }

        // ── Phase 3: Compile ─────────────────────────────────────────────────
        var compiledCommands = new Dictionary<Type, CommandPolicy>(commandTable.Count);
        foreach (var (type, acc) in commandTable)
            compiledCommands[type] = CompileCommandPolicy(acc, policyEvaluator, principalFactory);

        var compiledQueries = new Dictionary<Type, QueryPolicy>(queryTable.Count);
        foreach (var (type, acc) in queryTable)
            compiledQueries[type] = CompileQueryPolicy(acc, policyEvaluator, principalFactory);

        // ── Phase 3b: capability table (separate from hot-path policy tables) ─────
        var capabilities =
            new Dictionary<Type, CqrsCapabilityInfo>(commandTable.Count + queryTable.Count + eventTable.Count);
        foreach (var (type, acc) in commandTable)
            capabilities[type] = BuildCommandCapability(type, acc);
        foreach (var (type, acc) in queryTable)
            capabilities[type] = BuildQueryCapability(type, acc);
        foreach (var type in eventTable.Keys)
            capabilities[type] = BuildMarkerOnlyCapability(type);

        return new CompiledMessagingRegistry(
            compiledCommands.ToFrozenDictionary(),
            compiledQueries.ToFrozenDictionary(),
            eventTable.ToFrozenDictionary(),
            capabilities.ToFrozenDictionary());
    }

    // ── Capability projection (authoring metadata → compiled table) ─────────────

    private static CqrsCapabilityInfo BuildCommandCapability(Type type, CommandAccumulator acc)
        => new(
            FirstPermission(acc.Authorizers),
            acc.Description,
            acc.Keywords,
            acc.Irreversible,
            acc.Reverse.HasValue,
            acc.Reverse?.ReverseCommandType,
            typeof(IQueueable).IsAssignableFrom(type),
            typeof(ICacheable).IsAssignableFrom(type));

    private static CqrsCapabilityInfo BuildQueryCapability(Type type, QueryAccumulator acc)
        => new(
            FirstPermission(acc.Authorizers),
            acc.Description,
            acc.Keywords,
            Irreversible: false,
            Reversible: false,
            ReverseCommandType: null,
            typeof(IQueueable).IsAssignableFrom(type),
            typeof(ICacheable).IsAssignableFrom(type));

    // Events carry no builder-authored capability metadata (only a group/union type); surface the
    // marker facts so consumers can index them without a missing-key fault.
    private static CqrsCapabilityInfo BuildMarkerOnlyCapability(Type type)
        => new(
            RequiredPermission: null,
            Description: null,
            Keywords: null,
            Irreversible: false,
            Reversible: false,
            ReverseCommandType: null,
            typeof(IQueueable).IsAssignableFrom(type),
            typeof(ICacheable).IsAssignableFrom(type));

    // The permission is otherwise invisible downstream — it is compiled into an opaque authorizer
    // delegate. Read it here from the first Permission-kind authorizer descriptor.
    private static YFex.Authorization.Public.Permission? FirstPermission(List<AuthorizerDescriptor>? authorizers)
    {
        if (authorizers is null) return null;
        for (int i = 0; i < authorizers.Count; i++)
            if (authorizers[i].Kind == AuthorizerKind.Permission)
                return authorizers[i].Permission;
        return null;
    }

    // ── Phase 1: Collect ──────────────────────────────────────────────────────

    // Cache of MakeGenericMethod specialisations — avoids repeated lookup on
    // multiple Build() calls (common in tests).
    private static readonly ConcurrentDictionary<Type, MethodInfo> _collectCache = new();

    private static readonly MethodInfo _collectFromTypedOpenMethod =
        typeof(CompiledMessagingRegistry)
            .GetMethod(nameof(CollectFromTyped), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void Collect(
        IEnumerable<IAggregateConfiguration>? configs,
        Dictionary<Type, QueryAccumulator>    queryTable,
        Dictionary<Type, CommandAccumulator>  commandTable,
        Dictionary<Type, EventPolicy>         eventTable)
    {
        if (configs is null) return;
        foreach (var cfg in configs)
        {
            var aggType = cfg.AggregateType; // public property — no reflection
            // One MakeGenericMethod call per AggregateType, cached across Build() invocations.
            var method = _collectCache.GetOrAdd(aggType,
                static t => _collectFromTypedOpenMethod.MakeGenericMethod(t));
            method.Invoke(null, [cfg, queryTable, commandTable, eventTable]);
        }
    }

    /// <summary>
    /// Called once per config class (at startup) via <c>MakeGenericMethod</c>.
    /// Inside here everything is strongly typed — no further reflection.
    /// </summary>
    private static void CollectFromTyped<TAggregate>(
        IAggregateConfiguration<TAggregate>  cfg,
        Dictionary<Type, QueryAccumulator>   queryTable,
        Dictionary<Type, CommandAccumulator> commandTable,
        Dictionary<Type, EventPolicy>        eventTable)
    {
        var b = new AggregateConfigurationBuilder<TAggregate>();
        cfg.Configure(b);            // fully typed, zero reflection
        var regs = b.BuildRegistrations(); // internal, same assembly

        foreach (var q in regs.Queries)
        {
            if (!queryTable.TryGetValue(q.QueryType, out var acc))
                queryTable[q.QueryType] = acc = new QueryAccumulator();
            acc.Merge(q);
        }
        foreach (var c in regs.Commands)
        {
            if (!commandTable.TryGetValue(c.CommandType, out var acc))
                commandTable[c.CommandType] = acc = new CommandAccumulator();
            acc.Merge(c);
        }
        foreach (var e in regs.Events)
            if (e.GroupUnion is not null)
                eventTable[e.GroupUnion] = new EventPolicy(e.GroupUnion);
    }

    // ── Phase 2: O(n) group / union expansion ─────────────────────────────────

    private static void ExpandRules(List<InvalidationRuleDescriptor> rules, Assembly[] assemblies)
    {
        var expanded = new List<InvalidationRuleDescriptor>(rules.Count);
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!rule.IsGroup) { expanded.Add(rule); continue; }
            foreach (var t in FindImplementers(rule.TargetType, assemblies))
                expanded.Add(new InvalidationRuleDescriptor(t, null));
        }
        rules.Clear();
        rules.AddRange(expanded);
    }

    private static IEnumerable<Type> FindImplementers(Type groupOrUnionType, Assembly[] assemblies)
    {
        if (groupOrUnionType.GetCustomAttribute<UnionAttribute>() is not null)
        {
            foreach (var ctor in groupOrUnionType.GetConstructors())
            {
                var ps = ctor.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType != groupOrUnionType)
                    yield return ps[0].ParameterType;
            }
            yield break;
        }
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (!t.IsAbstract && !t.IsInterface && groupOrUnionType.IsAssignableFrom(t))
                    yield return t;
            }
        }
    }

    // ── Phase 3: Compilation ──────────────────────────────────────────────────

    private static CommandPolicy CompileCommandPolicy(
        CommandAccumulator acc,
        IPolicyEvaluator? evaluator,
        Func<ClaimsPrincipal, IAuthContext>? principalFactory)
        => new(
            BuildContextValidator(acc.Validators),
            BuildCompositeAuthorizer(acc.Authorizers, evaluator, principalFactory),
            acc.IdempotencyKey,
            acc.ConflictPolicy,
            acc.ConflictResolverType,
            acc.RetryPolicy,
            acc.Timeout,
            acc.OfflineHandler?.InlineDelegate,
            acc.OfflineHandler?.HandlerType,
            acc.Optimistic.HasValue ? CompileOptimistic(acc.Optimistic.Value) : null,
            CompileInvalidationTargets(acc.Invalidates),
            acc.Reverse.HasValue ? CompileReverse(acc.Reverse.Value) : null,
            acc.OfflineHandler?.Invoke,
            acc.Resilience,
            acc.Rules);

    private static QueryPolicy CompileQueryPolicy(
        QueryAccumulator acc,
        IPolicyEvaluator? evaluator,
        Func<ClaimsPrincipal, IAuthContext>? principalFactory)
        => new(
            BuildContextValidator(acc.Validators),
            BuildCompositeAuthorizer(acc.Authorizers, evaluator, principalFactory),
            acc.Cache,
            acc.Scope,
            acc.ScopeKey,
            acc.TagKey,
            acc.StaleAfter,
            acc.Timeout,
            CompileQueryInvalidators(acc.InvalidatedBy),
            acc.Rules);

    // ── Stage 5: context rules ────────────────────────────────────────────────
    //
    // Every factory registered for the message (baseline + overrides, in order) → ONE typed MessageValidator<T>.
    // The first factory knows T, so no MakeGenericType is needed.

    private static MessageValidator? BuildContextValidator(List<ContextRuleFactory>? factories)
        => factories is null || factories.Count == 0 ? null : factories[0].CreateValidator(factories);

    // Translates registration-phase authorizer descriptors into ONE pre-compiled AND-predicate.
    //
    // Context-only kinds that need YFex.Authorization semantics (Permission, PolicyName, TypeRef)
    // are folded into a single Policy evaluated through the injected IPolicyEvaluator. The sync
    // evaluator path decides Permission (frozen-set lookup); PolicyName/TypeRef are resolvable only
    // on the async authoritative path, so they are HIDDEN → deny here (fail-closed).
    //
    // BEHAVIOUR CHANGE (release note): previously PolicyName/TypeRef descriptors compiled to a null
    // predicate and were therefore SILENTLY UNENFORCED on the local dispatch path. They now deny.
    // Authenticated / Roles / inline predicates keep their exact previous behaviour (inline needs
    // the message object, which the evaluator's context-only path cannot carry).
    //
    // If a Permission/PolicyName/TypeRef authorizer is declared but no evaluator is wired, the gate
    // fails closed rather than silently allowing.
    private static Func<ClaimsPrincipal, object, bool>?
        BuildCompositeAuthorizer(
            List<AuthorizerDescriptor>? descriptors,
            IPolicyEvaluator? evaluator,
            Func<ClaimsPrincipal, IAuthContext>? principalFactory)
    {
        if (descriptors is null || descriptors.Count == 0) return null;

        var predicates = new List<Func<ClaimsPrincipal, object, bool>>(descriptors.Count);
        List<Requirement>? evalRequirements = null;   // folded into one evaluator-backed Policy

        for (int i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            switch (d.Kind)
            {
                case AuthorizerKind.RequireAuthenticated:
                    predicates.Add(static (u, _) => u.Identity?.IsAuthenticated == true);
                    break;

                case AuthorizerKind.Roles when d.Roles is { } roles:
                    predicates.Add((u, _) =>
                    {
                        for (int j = 0; j < roles.Length; j++)
                            if (u.IsInRole(roles[j])) return true;
                        return false;
                    });
                    break;

                case AuthorizerKind.Inline when d.InlinePredicate is not null:
                    predicates.Add(d.InlinePredicate);
                    break;

                case AuthorizerKind.Permission:
                    (evalRequirements ??= []).Add(Requirement.Require(d.Permission));
                    break;

                case AuthorizerKind.PolicyName when d.PolicyName is { } name:
                    (evalRequirements ??= []).Add(
                        Requirement.RequirePolicy(YFex.Authorization.Public.PolicyName.Parse(name)));
                    break;

                case AuthorizerKind.TypeRef when d.AuthorizerType is { } t:
                    (evalRequirements ??= []).Add(
                        Requirement.RequirePolicy(YFex.Authorization.Public.PolicyName.Parse("type:" + t.FullName)));
                    break;
            }
        }

        if (evalRequirements is { Count: > 0 })
        {
            if (evaluator is not null && principalFactory is not null)
            {
                var policy = new Policy(default, evalRequirements.ToArray());
                var eval = evaluator;
                var factory = principalFactory;
                predicates.Add((u, _) => eval.IsAllowed(policy, factory(u)));
            }
            else
            {
                // A permission/policy/type authorizer was declared but authorization is not wired.
                predicates.Add(static (_, _) => false);
            }
        }

        if (predicates.Count == 0) return null;
        if (predicates.Count == 1) return predicates[0];

        var arr = predicates.ToArray();
        return (user, obj) =>
        {
            for (int i = 0; i < arr.Length; i++)
                if (!arr[i](user, obj)) return false;
            return true;
        };
    }

    private static InvalidationTarget[]? CompileInvalidationTargets(
        List<InvalidationRuleDescriptor>? rules)
    {
        if (rules is null || rules.Count == 0) return null;
        var result = new InvalidationTarget[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            Func<object, object, bool>? match = null;
            if (rule.MatchExpression is not null)
            {
                var del = rule.MatchExpression.Compile();
                match = (q, cmd) => (bool)del.DynamicInvoke(q, cmd)!;
            }
            result[i] = new InvalidationTarget(rule.TargetType, match, rule.KeySelector);
        }
        return result;
    }

    private static QueryInvalidator[]? CompileQueryInvalidators(
        List<InvalidationRuleDescriptor>? rules)
    {
        if (rules is null || rules.Count == 0) return null;
        var result = new QueryInvalidator[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            Func<object, object, bool>? match = null;
            if (rule.MatchExpression is not null)
            {
                var del = rule.MatchExpression.Compile();
                match = (q, cmd) => (bool)del.DynamicInvoke(q, cmd)!;
            }
            result[i] = new QueryInvalidator(rule.TargetType, match);
        }
        return result;
    }

    private static OptimisticPolicy CompileOptimistic(OptimisticRuleDescriptor d)
    {
        var matchDel = d.MatchExpression.Compile();
        var applyDel = d.ApplyExpression.Compile();
        return new(d.QueryType,
            (q, cmd) => (bool)matchDel.DynamicInvoke(q, cmd)!,
            (q, cmd) => applyDel.DynamicInvoke(q, cmd)!);}

    // Compiles the reverse rule once (like CompileOptimistic): the map expression becomes a typed
    // delegate that materializes the reverse command with NO reflection on the hot path.
    private static ReversePolicy CompileReverse(ReverseRuleDescriptor d)
    {
        if (d.MapExpression is not null)
        {
            var del = d.MapExpression.Compile();
            // Arity distinguishes the command-only map (Func<TCommand,TReverse>) from the
            // result-bearing map (Func<TCommand,TResult,TReverse>).
            var twoArg = d.MapExpression.Parameters.Count == 2;
            Func<object, object?, object> map = twoArg
                ? (cmd, result) => del.DynamicInvoke(cmd, result)!
                : (cmd, _)      => del.DynamicInvoke(cmd)!;
            // DispatchReverse was captured at registration where TReverse is a generic parameter —
            // it dispatches the already-typed reverse command with NO MakeGenericMethod on the undo path.
            return new ReversePolicy(d.ReverseCommandType, map, null, d.DispatchReverse, null, d.ResultType);
        }
        // InvokeHandler was captured at registration where THandler/TCommand[/TResult] are generic
        // parameters — it resolves + invokes the typed handler with NO reflection on the undo path.
        return new ReversePolicy(d.ReverseCommandType, null, d.HandlerType, null, d.InvokeHandler, d.ResultType);
    }


    // ── Accumulators — mutable only during Build(), discarded after ───────────

    private sealed class QueryAccumulator
    {
        public List<ContextRuleFactory>?         Validators;
        public RulesStage?                       Rules;
        public List<AuthorizerDescriptor>?       Authorizers;
        public CachePolicy?                      Cache;
        public CacheScope                        Scope      = CacheScope.Global;
        public Func<ICacheScopeContext, string>? ScopeKey;
        public Func<object, string>?             TagKey;
        public TimeSpan?                         StaleAfter;
        public TimeSpan?                         Timeout;
        public List<InvalidationRuleDescriptor>? InvalidatedBy;
        public string?                           Description;
        public string[]?                         Keywords;

        public void Merge(QueryRegistrationMetadata m)
        {
            if (m.Validators    is { } v)   (Validators    ??= []).AddRange(v);
            if (m.Rules         is not null) Rules = m.Rules;
            if (m.Authorizers   is { } a)   (Authorizers   ??= []).AddRange(a);
            if (m.InvalidatedBy is { } inv) (InvalidatedBy ??= []).AddRange(inv);
            if (m.Cache      is not null) Cache      = m.Cache;
            if (m.StaleAfter is not null) StaleAfter = m.StaleAfter;
            if (m.Timeout    is not null) Timeout    = m.Timeout;
            if (m.ScopeKey   is not null) ScopeKey   = m.ScopeKey;
            if (m.TagKey     is not null) TagKey     = m.TagKey;
            if (m.Description is not null) Description = m.Description;
            if (m.Keywords   is not null) Keywords   = m.Keywords;
            Scope = m.Scope;
        }
    }

    private sealed class CommandAccumulator
    {
        public List<ContextRuleFactory>?         Validators;
        public RulesStage?                       Rules;
        public List<AuthorizerDescriptor>?       Authorizers;
        public List<InvalidationRuleDescriptor>? Invalidates;
        public Func<object, string>?             IdempotencyKey;
        public ConflictPolicy                    ConflictPolicy = ConflictPolicy.Escalate;
        public Type?                             ConflictResolverType;
        public OfflineHandlerDescriptor?         OfflineHandler;
        public RetryPolicy?                      RetryPolicy;
        public TimeSpan?                         Timeout;
        public global::YFex.Resilience.ResilienceSpec? Resilience;
        public OptimisticRuleDescriptor?         Optimistic;
        public ReverseRuleDescriptor?            Reverse;
        public string?                           Description;
        public string[]?                         Keywords;
        public bool                              Irreversible;

        public void Merge(CommandRegistrationMetadata m)
        {
            if (m.Validators  is { } v) (Validators  ??= []).AddRange(v);
            if (m.Rules       is not null) Rules = m.Rules;
            if (m.Authorizers is { } a) (Authorizers ??= []).AddRange(a);
            if (m.Invalidates is { } i) (Invalidates ??= []).AddRange(i);
            if (m.IdempotencyKey       is not null) IdempotencyKey    = m.IdempotencyKey;
            if (m.ConflictResolverType is not null) ConflictResolverType = m.ConflictResolverType;
            if (m.OfflineHandler       is not null) OfflineHandler    = m.OfflineHandler;
            if (m.RetryPolicy          is not null) RetryPolicy       = m.RetryPolicy;
            if (m.Timeout              is not null) Timeout           = m.Timeout;
            if (m.Resilience           is not null) Resilience        = m.Resilience;
            if (m.Optimistic           is not null) Optimistic        = m.Optimistic;
            if (m.Reverse              is not null) Reverse           = m.Reverse;
            if (m.Description          is not null) Description        = m.Description;
            if (m.Keywords             is not null) Keywords           = m.Keywords;
            if (m.Irreversible)                     Irreversible       = true;
            ConflictPolicy = m.ConflictPolicy;
        }
    }
}

// ── Startup validation helper ─────────────────────────────────────────────────

public static class CompiledMessagingRegistryValidationExtensions
{
    /// <summary>
    /// Warns or throws when message types exist in the scanned assemblies but have no
    /// registered configuration.  Run once at startup after <see cref="CompiledMessagingRegistry.Build"/>.
    /// </summary>
    public static void Validate(
        this CompiledMessagingRegistry registry,
        ConfigurationValidationLevel   level,
        Assembly[]                     scanForMessages,
        Action<string>?                logWarning = null)
    {
        if (level == ConfigurationValidationLevel.Off) return;

        var uncovered = new List<string>();
        foreach (var asm in scanForMessages)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }

            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (t.IsAbstract || t.IsInterface) continue;
                bool isMsg = false;
                foreach (var iface in t.GetInterfaces())
                {
                    if ((iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQuery<>))
                        || iface == typeof(ICommand) || iface == typeof(IEvent))
                    { isMsg = true; break; }
                }
                if (isMsg && !registry.Commands.ContainsKey(t) && !registry.Queries.ContainsKey(t))
                    uncovered.Add(t.Name);
            }
        }

        if (uncovered.Count == 0) return;
        var msg = $"YFex.Cqrs: {uncovered.Count} message type(s) have no configuration: "
                + string.Join(", ", uncovered);
        if (level == ConfigurationValidationLevel.Strict)
            throw new InvalidOperationException(msg);
        logWarning?.Invoke(msg);
    }
}
