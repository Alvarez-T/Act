using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YFex;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging;

using YFex.Persistence;

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

    public LocalDispatcher(
        IHandlerInvoker invoker,
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        ICache cache,
        IOutbox outbox,
        IEventBus eventBus,
        IServiceProvider sp)
    {
        _invoker = invoker;
        _registry = registry;
        _networkStatus = networkStatus;
        _cache = cache;
        _outbox = outbox;
        _eventBus = eventBus;
        _sp = sp;
    }

    // â”€â”€ Query â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async ValueTask<CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>
    {
        if (!_networkStatus.IsConnected)
            return await OfflineQueryAsync<TQuery, TResult>(query, ct).ConfigureAwait(false);

        _registry.Queries.TryGetValue(typeof(TQuery), out var policy);

        var validationResult = policy?.Validate is not null
            ? await policy.Validate(query!, ct).ConfigureAwait(false)
            : ValidationResult.Success();
        if (!validationResult.IsValid)
            return CacheableQueryResult<TResult>.ValidationProblem(FormatErrors(validationResult));

        if (policy?.Authorize is not null && !policy.Authorize(ClaimsPrincipal.Current ?? new(), query!))
            return CacheableQueryResult<TResult>.Unauthorized();

        var cacheKey = QueryCacheKey.For(typeof(TQuery), query!, policy, _sp);
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

        var valResult = policy?.Validate is not null
            ? await policy.Validate(cmd!, ct).ConfigureAwait(false)
            : ValidationResult.Success();
        if (!valResult.IsValid)
            return QueueableResult<TResult>.Fail(FormatErrors(valResult));

        if (policy?.Authorize is not null && !policy.Authorize(ClaimsPrincipal.Current ?? new(), cmd!))
            return QueueableResult<TResult>.Fail("Unauthorized.");

        using var cts = policy?.Timeout.HasValue == true
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(policy!.Timeout!.Value);
        var effectiveCt = cts?.Token ?? ct;

        var result = await _invoker.InvokeAsync<TResult>(cmd!, effectiveCt).ConfigureAwait(false);

        await ApplyOptimisticUpdatesAsync(cmd!, policy, ct).ConfigureAwait(false);
        await InvalidateCachesAsync(cmd!, policy, ct).ConfigureAwait(false);

        return QueueableResult<TResult>.Ok(result);
    }

    private async ValueTask<QueueableResult<TResult>> OfflineCommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand<TResult>
    {
        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        if (policy?.OnOfflineHandler is not null)
            await policy.OnOfflineHandler(cmd!, ct).ConfigureAwait(false);
        else if (policy?.OnOfflineHandlerType is not null)
        {
            // Resolve by the interface first (typical DI registration pattern);
            // fall back to the concrete type if the interface isn't registered.
            var ifaceType = typeof(IOfflineHandler<TCommand, TResult>);
            var handlerObj = _sp.GetService(ifaceType) ?? _sp.GetRequiredService(policy.OnOfflineHandlerType);
            var handler = (IOfflineHandler<TCommand, TResult>)handlerObj;
            await handler.HandleAsync(cmd, ct).ConfigureAwait(false);
        }

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

        var valResult = policy?.Validate is not null
            ? await policy.Validate(cmd!, ct).ConfigureAwait(false)
            : ValidationResult.Success();
        if (!valResult.IsValid)
            return QueueableResult.Fail(FormatErrors(valResult));

        if (policy?.Authorize is not null && !policy.Authorize(ClaimsPrincipal.Current ?? new(), cmd!))
            return QueueableResult.Fail("Unauthorized.");

        using var cts = policy?.Timeout.HasValue == true
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null) cts.CancelAfter(policy!.Timeout!.Value);
        var effectiveCt = cts?.Token ?? ct;

        await _invoker.InvokeAsync(cmd!, effectiveCt).ConfigureAwait(false);
        await InvalidateCachesAsync(cmd!, policy, ct).ConfigureAwait(false);

        return QueueableResult.Ok();
    }

    private async ValueTask<QueueableResult> OfflineCommandVoidAsync<TCommand>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand
    {
        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);

        if (policy?.OnOfflineHandler is not null)
            await policy.OnOfflineHandler(cmd!, ct).ConfigureAwait(false);
        else if (policy?.OnOfflineHandlerType is not null)
        {
            var handler = (IOfflineHandler<TCommand>)_sp.GetRequiredService(policy.OnOfflineHandlerType);
            await handler.HandleAsync(cmd, ct).ConfigureAwait(false);
        }

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

    private static string FormatErrors(ValidationResult result)
    {
        if (result.Errors.Count == 0) return "Validation failed.";
        var sb = new StringBuilder();
        var errors = result.Errors;
        for (int i = 0; i < errors.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(errors[i].Message);
        }
        return sb.ToString();
    }
}
