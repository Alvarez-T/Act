using Microsoft.Extensions.DependencyInjection;
using YFex;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging;

using YFex.Persistence;
using YFex.Validation;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Default in-process <see cref="IDispatcher"/> for tests and simple console apps.
/// Handles the three-phase dispatch pipeline:
/// <list type="bullet">
///   <item>Online: validate â†’ authorize â†’ invoke â†’ cache â†’ invalidate</item>
///   <item>Offline query: serve from <see cref="ICache"/> (if <see cref="ICacheable"/>)</item>
///   <item>Offline command: run <c>OnOffline</c> handler â†’ enqueue (if <see cref="IQueueable"/>)</item>
/// </list>
/// </summary>
public sealed class LocalDispatcher : IDispatcher
{
    private readonly IHandlerInvoker _invoker;
    private readonly CompiledMessagingRegistry _registry;
    private readonly INetworkStatus _networkStatus;
    private readonly ICache _cache;
    private readonly IOutbox _outbox;
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _sp;
    private readonly YFex.Resilience.IResiliencePipelineFactory? _pipelineFactory;
    // Stage 2 principal (D29): the remote caller's CallContext, else the host's IPrincipalAccessor.
    private readonly CallPrincipalSource _principal;

    public LocalDispatcher(
        IHandlerInvoker invoker,
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        ICache cache,
        IOutbox outbox,
        IEventBus eventBus,
        IServiceProvider sp,
        // Optional: only present when AddYFexResiliencePolly (or another factory) is registered. When absent,
        // dispatch keeps its legacy behaviour (the CommandPolicy.Timeout CTS path).
        YFex.Resilience.IResiliencePipelineFactory? pipelineFactory = null)
    {
        // A ValidatingHandlerInvoker (server) would repeat stages 1/5 that this dispatcher already runs.
        _invoker = invoker is ValidatingHandlerInvoker validating ? validating.Inner : invoker;
        _registry = registry;
        _networkStatus = networkStatus;
        _cache = cache;
        _outbox = outbox;
        _eventBus = eventBus;
        _sp = sp;
        _pipelineFactory = pipelineFactory;
        _principal = new CallPrincipalSource(sp);
    }

    // â”€â”€ Query â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async ValueTask<CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>
    {
        // Coerce (paging clamps) → stage 1, before the offline cache or the cache key: an invalid query never reaches the
        // cache (09 §7).
        query = MessagePipeline.Coerce(in query);
        if (!MessagePipeline.CheckShape(in query, out var shape))
            return CacheableQueryResult<TResult>.ValidationProblem(shape);

        if (!_networkStatus.IsConnected)
            return await OfflineQueryAsync<TQuery, TResult>(query, ct).ConfigureAwait(false);

        _registry.Queries.TryGetValue(typeof(TQuery), out var policy);

        // Stages (1 above) → 2 → 3–4 → 5 (09 §1): authorize, situation + rules (queries never reject on the verdict), context rules.
        if (!_principal.Authorize(policy?.Authorize, query!))
            return CacheableQueryResult<TResult>.Unauthorized();

        RulesOutcome rules = default;
        if (policy?.Rules is { } ruleStage) rules = await ruleStage.RunAsync(query!, _sp, ct).ConfigureAwait(false);
        using var decisionScope = DecisionContext.Enter(query!, policy?.Rules, rules);

        if (policy?.Validation is { } contextRules)
        {
            var context = await MessagePipeline.CheckContextAsync(query, contextRules, _sp, ct).ConfigureAwait(false);
            if (!context.IsValid) return CacheableQueryResult<TResult>.ValidationProblem(context);
        }

        var cacheKey = QueryCacheKey.For(typeof(TQuery), query!, policy, _sp);
        try
        {
            if (cacheKey is null)
                return new Fresh<TResult>(await InvokeQueryAsync<TResult>(query!, policy, ct).ConfigureAwait(false));

            // Stampede-safe read-through: concurrent misses collapse into one factory call on FusionCache.
            var result = await _cache.GetOrSetAsync<TResult>(
                cacheKey,
                innerCt => InvokeQueryAsync<TResult>(query!, policy, innerCt),
                QueryCacheKey.Options(typeof(TQuery), policy, query!),
                ct).ConfigureAwait(false);

            // Online read-through — authoritative, regardless of whether L1 served it within TTL.
            return new Fresh<TResult>(result);
        }
        catch (ErrorResultException rejected)
        {
            // Same contract as commands and the generated RPC contract impl: a handler's typed rejection (not found,
            // resource permission…) is the result — and, thrown inside the factory, it is never cached.
            return rejected.TypedError;
        }
    }

    private async Task<TResult> InvokeQueryAsync<TResult>(object query, QueryPolicy? policy, CancellationToken ct)
    {
        using var cts = policy?.Timeout.HasValue == true
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(policy!.Timeout!.Value);
        var effectiveCt = cts?.Token ?? ct;

        return await _invoker.InvokeAsync<TResult>(query, effectiveCt).ConfigureAwait(false);
    }

    private async ValueTask<CacheableQueryResult<TResult>> OfflineQueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct) where TQuery : IQuery<TResult>
    {
        if (query is not ICacheable) return CacheableQueryResult<TResult>.Fail("No network connectivity.");

        _registry.Queries.TryGetValue(typeof(TQuery), out var policy);
        var cacheKey = QueryCacheKey.For(typeof(TQuery), query!, policy, _sp);
        if (cacheKey is null) return CacheableQueryResult<TResult>.Fail("No network connectivity.");

        // Provenance-aware read: keep the stale bit the cache tracked (offline invalidation marks
        // entries stale, not deleted) so the UI can badge it and refresh on reconnect.
        var lookup = await _cache.TryGetAsync<TResult>(cacheKey, ct).ConfigureAwait(false);
        if (!lookup.TryGetHit(out var cv))
            return CacheableQueryResult<TResult>.Fail("No cached value available offline.");

        return cv.IsStale
            ? new Stale<TResult>(cv.GetValue(), cv.LastModified)
            : new Cached<TResult>(cv.GetValue(), cv.LastModified);
    }

    // â”€â”€ Command (with result) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>
    {
        if (!_networkStatus.IsConnected)
            return await OfflineCommandAsync<TCommand, TResult>(cmd, ct).ConfigureAwait(false);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        // Stages 1 → 2 → 5 (09 §1): shape, authorize, context rules.
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult<TResult>.Invalid(shape);

        if (!_principal.Authorize(policy?.Authorize, cmd!))
            return QueueableResult<TResult>.Unauthorized("Unauthorized.");

        // Stages 3–4 (09 §6): situation + rules; Denied / approval required / mismatch policy reject here.
        RulesOutcome rules = default;
        if (policy?.Rules is { } ruleStage)
        {
            rules = await ruleStage.RunAsync(cmd!, _sp, ct).ConfigureAwait(false);
            if (rules.Error is { } rejected) return rejected;
        }

        if (policy?.Validation is { } contextRules)
        {
            var context = await MessagePipeline.CheckContextAsync(cmd, contextRules, _sp, ct).ConfigureAwait(false);
            if (!context.IsValid) return QueueableResult<TResult>.Invalid(context);
        }

        // The decision flows to IRuledCommandHandler / IDecisionAccessor through the async context.
        using var decisionScope = DecisionContext.Enter(cmd!, policy?.Rules, rules);

        // Scope-specific override (YFex.CustomLogic). Optional seam — absent unless the CustomLogic.Cqrs
        // bridge is registered. A handled override replaces the default handler but still flows through the
        // rest of the pipeline (optimistic update, invalidation, executed-observer/journal).
        var customLogic = _sp.GetService<ICommandCustomLogic>();
        TResult result;
        try
        {
            if (customLogic is not null)
            {
                var over = await customLogic.TryHandleAsync(typeof(TCommand), cmd!, ct).ConfigureAwait(false);
                result = over.IsHandled
                    ? (TResult)over.Result!
                    : await InvokeCommandAsync<TResult>(cmd!, policy, ct).ConfigureAwait(false);
            }
            else
            {
                result = await InvokeCommandAsync<TResult>(cmd!, policy, ct).ConfigureAwait(false);
            }
        }
        catch (ErrorResultException rejected)
        {
            // A handler rejecting with a typed error (domain invariant, resource permission, concurrency) — same
            // contract as the generated RPC contract impl: the error becomes the result, nothing else runs.
            return rejected.TypedError;
        }

        await ApplyOptimisticUpdatesAsync(cmd!, policy, ct).ConfigureAwait(false);
        await InvalidateCachesAsync(cmd!, policy, ct).ConfigureAwait(false);
        await NotifyExecutedAsync(cmd!, result, ct).ConfigureAwait(false);

        return QueueableResult<TResult>.Ok(result);
    }

    private async ValueTask<QueueableResult<TResult>> OfflineCommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand<TResult>
    {
        // Stage 1 before anything is queued: an invalid command is never enqueued (09 §8, G6).
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult<TResult>.Invalid(shape);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        if (policy?.OnOfflineHandler is not null)
            await policy.OnOfflineHandler(cmd!, ct).ConfigureAwait(false);
        else if (policy?.OnOfflineHandlerInvoke is not null)
            // Typed resolve + invoke captured at registration (interface-first, concrete fallback); no reflection.
            await policy.OnOfflineHandlerInvoke(_sp, cmd!, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkInvalidationTargetsStaleAsync(cmd!, policy, ct).ConfigureAwait(false);
            var queued = await _outbox.EnqueueAsync(cmd, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandQueuedEvent(queued.IdempotencyKey, typeof(TCommand).Name, DateTimeOffset.UtcNow));
            return QueueableResult<TResult>.Queue(queued.IdempotencyKey);
        }

        return policy?.OnOfflineHandler is not null || policy?.OnOfflineHandlerType is not null
            ? QueueableResult<TResult>.Fail("Offline â€” no result available.")
            : QueueableResult<TResult>.Fail("No network connectivity.");
    }

    // â”€â”€ Command (void) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async ValueTask<QueueableResult> CommandAsync<TCommand>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand
    {
        if (!_networkStatus.IsConnected)
            return await OfflineCommandVoidAsync(cmd, ct).ConfigureAwait(false);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        // Stages 1 → 2 → 5 (09 §1): shape, authorize, context rules.
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult.Invalid(shape);

        if (!_principal.Authorize(policy?.Authorize, cmd!))
            return QueueableResult.Unauthorized("Unauthorized.");

        // Stages 3–4 (09 §6).
        RulesOutcome rules = default;
        if (policy?.Rules is { } ruleStage)
        {
            rules = await ruleStage.RunAsync(cmd!, _sp, ct).ConfigureAwait(false);
            if (rules.Error is { } rejected) return rejected;
        }

        if (policy?.Validation is { } contextRules)
        {
            var context = await MessagePipeline.CheckContextAsync(cmd, contextRules, _sp, ct).ConfigureAwait(false);
            if (!context.IsValid) return QueueableResult.Invalid(context);
        }

        using var decisionScope = DecisionContext.Enter(cmd!, policy?.Rules, rules);

        // Scope-specific override (YFex.CustomLogic). Optional seam; a handled override replaces the
        // default handler, then invalidation + executed-observer run exactly as for the default path.
        var customLogic = _sp.GetService<ICommandCustomLogic>();
        try
        {
            if (customLogic is null || !(await customLogic.TryHandleAsync(typeof(TCommand), cmd!, ct).ConfigureAwait(false)).IsHandled)
                await InvokeCommandVoidAsync(cmd!, policy, ct).ConfigureAwait(false);
        }
        catch (ErrorResultException rejected)
        {
            return rejected.TypedError;
        }
        await InvalidateCachesAsync(cmd!, policy, ct).ConfigureAwait(false);
        await NotifyExecutedAsync(cmd!, result: null, ct).ConfigureAwait(false);

        return QueueableResult.Ok();
    }

    // â”€â”€ Post-execution seam (reversible commands / command journal) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async ValueTask NotifyExecutedAsync(object command, object? result, CancellationToken ct)
    {
        // Optional — fires only when a consumer (e.g. YFex.Cqrs.Compensation) has registered an
        // observer. Runs only on the successful online path; never for queued/failed commands.
        var observer = _sp.GetService<ICommandExecutedObserver>();
        if (observer is not null)
            await observer.OnExecutedAsync(command, result, ct).ConfigureAwait(false);
    }

    private async ValueTask<QueueableResult> OfflineCommandVoidAsync<TCommand>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand
    {
        // Stage 1 before anything is queued: an invalid command is never enqueued (09 §8, G6).
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult.Invalid(shape);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        if (policy?.OnOfflineHandler is not null)
            await policy.OnOfflineHandler(cmd!, ct).ConfigureAwait(false);
        else if (policy?.OnOfflineHandlerInvoke is not null)
            // Typed resolve + invoke captured at registration (interface-first, concrete fallback); no reflection.
            await policy.OnOfflineHandlerInvoke(_sp, cmd!, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkInvalidationTargetsStaleAsync(cmd!, policy, ct).ConfigureAwait(false);
            var queued = await _outbox.EnqueueAsync(cmd, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandQueuedEvent(queued.IdempotencyKey, typeof(TCommand).Name, DateTimeOffset.UtcNow));
            return QueueableResult.Queue(queued.IdempotencyKey);
        }

        return policy?.OnOfflineHandler is not null || policy?.OnOfflineHandlerType is not null
            ? QueueableResult.Ok()
            : QueueableResult.Fail("No network connectivity.");
    }

    // â”€â”€ Publish â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent
    {
        _eventBus.Publish(evt!);
        return ValueTask.CompletedTask;
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async ValueTask InvalidateCachesAsync(object cmd, CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.InvalidationTargets is null) return;
        // Tag-based, no key enumeration (works on FusionCache). Precise when the target declares an
        // entity key (drops only en:{QueryType}:{key}); coarse otherwise (drops all variants).
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
            await _cache.RemoveByTagAsync(QueryCacheKey.InvalidationTag(targets[i], cmd), ct).ConfigureAwait(false);
    }

    private async ValueTask MarkInvalidationTargetsStaleAsync(object cmd, CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.InvalidationTargets is null) return;
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
            await _cache.ExpireByTagAsync(QueryCacheKey.InvalidationTag(targets[i], cmd), ct).ConfigureAwait(false);
    }

    // NOTE: optimistic updates mutate cached entries in place, which needs key enumeration — so this
    // path requires an enumerable ICache (InMemoryCache / KeyValueCache) and throws NotSupported on
    // FusionCacheAdapter. Tags can evict but not mutate; a tag-native design would invalidate + refetch.
    private async ValueTask ApplyOptimisticUpdatesAsync<TCommand>(
        TCommand cmd, CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.Optimistic is null) return;
        var opt = policy.Optimistic;
        var prefix = $"query:{opt.TargetQueryType.FullName}";
        var keys = await _cache.GetKeysWithPrefixAsync(prefix, ct).ConfigureAwait(false);
        for (int i = 0; i < keys.Count; i++)
        {
            await _cache.UpdateAsync<object>(keys[i], current =>
            {
                if (opt.Match(current, cmd!)) return opt.Apply(current, cmd!);
                return current;
            }, ct).ConfigureAwait(false);
        }
    }

    // ── Resilience-aware command invoke (design §1 L4) ───────────────────────
    //
    // When a resilience pipeline factory is registered and the command carries a non-empty spec (composed
    // from .Retry/.Timeout/.UseResource/.Fallback/.Hedging), the invoke runs through the pipeline (retry,
    // timeout, circuit breaker, etc.). Otherwise the legacy per-attempt Timeout CTS path is preserved, so
    // apps without a registered factory keep their exact prior behaviour.

    private async ValueTask<TResult> InvokeCommandAsync<TResult>(object cmd, CommandPolicy? policy, CancellationToken ct)
    {
        if (_pipelineFactory is not null && policy?.Resilience is { } spec && !spec.IsEmpty)
        {
            var pipeline = _pipelineFactory.Get(spec);
            var invoker = _invoker;
            return await pipeline.ExecuteAsync(
                innerCt => new ValueTask<TResult>(invoker.InvokeAsync<TResult>(cmd, innerCt)), ct).ConfigureAwait(false);
        }

        using var cts = policy?.Timeout.HasValue == true
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(policy!.Timeout!.Value);
        return await _invoker.InvokeAsync<TResult>(cmd, cts?.Token ?? ct).ConfigureAwait(false);
    }

    private async ValueTask InvokeCommandVoidAsync(object cmd, CommandPolicy? policy, CancellationToken ct)
    {
        if (_pipelineFactory is not null && policy?.Resilience is { } spec && !spec.IsEmpty)
        {
            var pipeline = _pipelineFactory.Get(spec);
            var invoker = _invoker;
            await pipeline.ExecuteAsync(async innerCt =>
            {
                await invoker.InvokeAsync(cmd, innerCt).ConfigureAwait(false);
                return true; // sentinel — the generic pipeline has no void overload
            }, ct).ConfigureAwait(false);
            return;
        }

        using var cts = policy?.Timeout.HasValue == true
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(policy!.Timeout!.Value);
        await _invoker.InvokeAsync(cmd, cts?.Token ?? ct).ConfigureAwait(false);
    }
}
