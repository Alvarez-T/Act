using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.Registration;
using YFex.Cqrs.Runtime;

namespace YFex.Cqrs;

public enum ConfigurationValidationLevel { Off, Loose, Strict }

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

    private CompiledMessagingRegistry(
        FrozenDictionary<Type, CommandPolicy> commands,
        FrozenDictionary<Type, QueryPolicy>   queries,
        FrozenDictionary<Type, EventPolicy>   events)
    { Commands = commands; Queries = queries; Events = events; }

    // ─────────────────────────────────────────────────────────────────────────
    // Build — three-phase: Collect → Expand → Compile
    // ─────────────────────────────────────────────────────────────────────────

    public static CompiledMessagingRegistry Build(
        IEnumerable<IAggregateConfiguration>  baseline,
        IEnumerable<IAggregateConfiguration>? serverOverrides     = null,
        IEnumerable<IAggregateConfiguration>? clientOverrides     = null,
        Assembly[]?                           scanForImplementers = null)
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

        // ── Phase 3: Compile ─────────────────────────────────────────────────
        var compiledCommands = new Dictionary<Type, CommandPolicy>(commandTable.Count);
        foreach (var (type, acc) in commandTable)
            compiledCommands[type] = CompileCommandPolicy(acc);

        var compiledQueries = new Dictionary<Type, QueryPolicy>(queryTable.Count);
        foreach (var (type, acc) in queryTable)
            compiledQueries[type] = CompileQueryPolicy(acc);

        return new CompiledMessagingRegistry(
            compiledCommands.ToFrozenDictionary(),
            compiledQueries.ToFrozenDictionary(),
            eventTable.ToFrozenDictionary());
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

    private static CommandPolicy CompileCommandPolicy(CommandAccumulator acc)
        => new(
            BuildCompositeValidator(acc.Validators),
            BuildCompositeAuthorizer(acc.Authorizers),
            acc.IdempotencyKey,
            acc.ConflictPolicy,
            acc.ConflictResolverType,
            acc.RetryPolicy,
            acc.Timeout,
            acc.OfflineHandler?.InlineDelegate,
            acc.OfflineHandler?.HandlerType,
            acc.Optimistic.HasValue ? CompileOptimistic(acc.Optimistic.Value) : null,
            CompileInvalidationTargets(acc.Invalidates));

    private static QueryPolicy CompileQueryPolicy(QueryAccumulator acc)
        => new(
            BuildCompositeValidator(acc.Validators),
            BuildCompositeAuthorizer(acc.Authorizers),
            acc.Cache,
            acc.Scope,
            acc.ScopeKey,
            acc.StaleAfter,
            acc.Timeout,
            CompileQueryInvalidators(acc.InvalidatedBy));

    // ── Composite delegate builders ───────────────────────────────────────────
    //
    // Principle 6: N descriptors → ONE pre-compiled delegate.
    // Principle 9: sync fast-path avoids AsyncStateMachineBox allocation when
    //              validators complete synchronously (the common case).

    private static Func<object, CancellationToken, ValueTask<ValidationResult>>?
        BuildCompositeValidator(List<ValidatorDescriptor>? descriptors)
    {
        if (descriptors is null || descriptors.Count == 0) return null;

        // Collect only inline delegates (type-ref validators handled by Plan 3 dispatcher).
        var inline = new List<Func<object, CancellationToken, ValueTask<ValidationResult>>>(
            descriptors.Count);
        for (int i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            if (d.Kind == ValidatorKind.Inline && d.InlineDelegate is not null)
                inline.Add(d.InlineDelegate);
        }

        if (inline.Count == 0) return null;
        if (inline.Count == 1) return inline[0]; // hot path: single validator — zero overhead

        // Multi-validator: sync fast-path avoids state-machine allocation for sync validators.
        var arr = inline.ToArray();
        return (obj, ct) =>
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var vt = arr[i](obj, ct);
                if (vt.IsCompletedSuccessfully)
                {
                    // Synchronous result — inspect without await (no state machine).
                    var r = vt.Result;
                    if (!r.IsValid) return new ValueTask<ValidationResult>(r);
                }
                else
                {
                    // Async validator — hand off remaining work to the async continuation.
                    return ContinueValidationAsync(arr, i, vt, obj, ct);
                }
            }
            return new ValueTask<ValidationResult>(ValidationResult.Success());
        };
    }

    // Async fallback — only invoked when a validator truly yields (uncommon case).
    // The allocation of the state machine here is unavoidable and correct.
    private static async ValueTask<ValidationResult> ContinueValidationAsync(
        Func<object, CancellationToken, ValueTask<ValidationResult>>[] arr,
        int asyncIdx,
        ValueTask<ValidationResult> asyncTask,
        object obj,
        CancellationToken ct)
    {
        var r = await asyncTask.ConfigureAwait(false);
        if (!r.IsValid) return r;
        for (int i = asyncIdx + 1; i < arr.Length; i++)
        {
            r = await arr[i](obj, ct).ConfigureAwait(false);
            if (!r.IsValid) return r;
        }
        return ValidationResult.Success();
    }

    private static Func<ClaimsPrincipal, object, bool>?
        BuildCompositeAuthorizer(List<AuthorizerDescriptor>? descriptors)
    {
        if (descriptors is null || descriptors.Count == 0) return null;

        var predicates = new List<Func<ClaimsPrincipal, object, bool>>(descriptors.Count);
        for (int i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            Func<ClaimsPrincipal, object, bool>? pred = d.Kind switch
            {
                AuthorizerKind.RequireAuthenticated
                    => static (u, _) => u.Identity?.IsAuthenticated == true,
                AuthorizerKind.Roles when d.Roles is { } roles
                    => (u, _) =>
                    {
                        for (int j = 0; j < roles.Length; j++)
                            if (u.IsInRole(roles[j])) return true;
                        return false;
                    },
                AuthorizerKind.Inline => d.InlinePredicate,
                // PolicyName / TypeRef: evaluated by Plan 3 dispatcher via DI lookup (cached).
                _ => null,
            };
            if (pred is not null) predicates.Add(pred);
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
            result[i] = new InvalidationTarget(rule.TargetType, match);
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


    // ── Accumulators — mutable only during Build(), discarded after ───────────

    private sealed class QueryAccumulator
    {
        public List<ValidatorDescriptor>?        Validators;
        public List<AuthorizerDescriptor>?       Authorizers;
        public CachePolicy?                      Cache;
        public CacheScope                        Scope      = CacheScope.Global;
        public Func<ICacheScopeContext, string>? ScopeKey;
        public TimeSpan?                         StaleAfter;
        public TimeSpan?                         Timeout;
        public List<InvalidationRuleDescriptor>? InvalidatedBy;

        public void Merge(QueryRegistrationMetadata m)
        {
            if (m.Validators    is { } v)   (Validators    ??= []).AddRange(v);
            if (m.Authorizers   is { } a)   (Authorizers   ??= []).AddRange(a);
            if (m.InvalidatedBy is { } inv) (InvalidatedBy ??= []).AddRange(inv);
            if (m.Cache      is not null) Cache      = m.Cache;
            if (m.StaleAfter is not null) StaleAfter = m.StaleAfter;
            if (m.Timeout    is not null) Timeout    = m.Timeout;
            if (m.ScopeKey   is not null) ScopeKey   = m.ScopeKey;
            Scope = m.Scope;
        }
    }

    private sealed class CommandAccumulator
    {
        public List<ValidatorDescriptor>?        Validators;
        public List<AuthorizerDescriptor>?       Authorizers;
        public List<InvalidationRuleDescriptor>? Invalidates;
        public Func<object, string>?             IdempotencyKey;
        public ConflictPolicy                    ConflictPolicy = ConflictPolicy.Escalate;
        public Type?                             ConflictResolverType;
        public OfflineHandlerDescriptor?         OfflineHandler;
        public RetryPolicy?                      RetryPolicy;
        public TimeSpan?                         Timeout;
        public OptimisticRuleDescriptor?         Optimistic;

        public void Merge(CommandRegistrationMetadata m)
        {
            if (m.Validators  is { } v) (Validators  ??= []).AddRange(v);
            if (m.Authorizers is { } a) (Authorizers ??= []).AddRange(a);
            if (m.Invalidates is { } i) (Invalidates ??= []).AddRange(i);
            if (m.IdempotencyKey       is not null) IdempotencyKey    = m.IdempotencyKey;
            if (m.ConflictResolverType is not null) ConflictResolverType = m.ConflictResolverType;
            if (m.OfflineHandler       is not null) OfflineHandler    = m.OfflineHandler;
            if (m.RetryPolicy          is not null) RetryPolicy       = m.RetryPolicy;
            if (m.Timeout              is not null) Timeout           = m.Timeout;
            if (m.Optimistic           is not null) Optimistic        = m.Optimistic;
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
