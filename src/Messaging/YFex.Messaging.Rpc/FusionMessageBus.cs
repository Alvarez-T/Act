using System.Collections.Frozen;
using ActualLab.Rpc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YFex;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging;

using YFex.Persistence;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

// ── Routing entry types ──────────────────────────────────────────────────────

/// <summary>Pre-compiled dispatch entry for a single query type.</summary>
// Each entry holds the typed proxy delegate; the generic dispatch methods recover it with a type test (no casts of
// results through object, no reflection). Typed-result delegates (D26) carry the server's Error to the caller.
internal sealed class QueryDispatchEntry
{
    internal required Delegate Call { get; init; }
}

internal sealed class CommandDispatchEntry
{
    internal required Delegate Call { get; init; }
}

internal sealed class VoidCommandDispatchEntry
{
    internal required Delegate Call { get; init; }
}

// ── Builder ──────────────────────────────────────────────────────────────────

/// <summary>
/// Mutable builder populated by the source generator's registration code and then frozen
/// into a <see cref="FusionMessageBus"/> at DI startup.
/// </summary>
public sealed class FusionMessageBusBuilder
{
    private readonly List<(Type QueryType, QueryDispatchEntry Entry)> _queries = [];
    private readonly List<(Type CommandType, CommandDispatchEntry Entry)> _commands = [];
    private readonly List<(Type CommandType, VoidCommandDispatchEntry Entry)> _voidCommands = [];

    /// <summary>Route whose proxy returns the server's typed result — errors (validation violations…) reach the caller.</summary>
    public FusionMessageBusBuilder AddQuery<TQuery, TResult>(
        Func<TQuery, CancellationToken, Task<Result<TResult>>> proxyCall)
        where TQuery : IQuery<TResult>
        => AddQueryEntry<TQuery>(proxyCall);

    /// <summary>Legacy route returning the bare value (a server error surfaces as an exception).</summary>
    public FusionMessageBusBuilder AddQuery<TQuery, TResult>(
        Func<TQuery, CancellationToken, Task<TResult>> proxyCall)
        where TQuery : IQuery<TResult>
        => AddQueryEntry<TQuery>(proxyCall);

    /// <summary>Route whose proxy returns the server's typed result — errors (validation violations…) reach the caller.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand, TResult>(
        Func<TCommand, CancellationToken, Task<QueueableResult<TResult>>> proxyCall)
        where TCommand : ICommand<TResult>
        => AddCommandEntry<TCommand>(proxyCall);

    /// <summary>Legacy route returning the bare value.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand, TResult>(
        Func<TCommand, CancellationToken, Task<TResult>> proxyCall)
        where TCommand : ICommand<TResult>
        => AddCommandEntry<TCommand>(proxyCall);

    /// <summary>Void route returning the server's typed result.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand>(
        Func<TCommand, CancellationToken, Task<QueueableResult>> proxyCall)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(proxyCall);
        _voidCommands.Add((typeof(TCommand), new VoidCommandDispatchEntry { Call = proxyCall }));
        return this;
    }

    /// <summary>Legacy void route.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand>(
        Func<TCommand, CancellationToken, Task> proxyCall)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(proxyCall);
        _voidCommands.Add((typeof(TCommand), new VoidCommandDispatchEntry { Call = proxyCall }));
        return this;
    }

    private FusionMessageBusBuilder AddQueryEntry<TQuery>(Delegate call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _queries.Add((typeof(TQuery), new QueryDispatchEntry { Call = call }));
        return this;
    }

    private FusionMessageBusBuilder AddCommandEntry<TCommand>(Delegate call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _commands.Add((typeof(TCommand), new CommandDispatchEntry { Call = call }));
        return this;
    }

    public FusionMessageBus Build(
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        ICache cache,
        IOutbox outbox,
        IEventBus eventBus,
        IServiceProvider sp)
    {
        return new FusionMessageBus(
            _queries.ToFrozenDictionary(x => x.QueryType, x => x.Entry),
            _commands.ToFrozenDictionary(x => x.CommandType, x => x.Entry),
            _voidCommands.ToFrozenDictionary(x => x.CommandType, x => x.Entry),
            registry, networkStatus, cache, outbox, eventBus, sp);
    }
}

// ── Runtime dispatcher ───────────────────────────────────────────────────────

/// <summary>
/// Client-side <see cref="IDispatcher"/> that routes through Fusion proxy calls registered
/// by the source generator. Adds offline fallback (cache serve, outbox queue) using the same
/// policy logic as <see cref="LocalDispatcher"/>.
/// </summary>
/// <remarks>
/// Stage 2 (authorize) runs here too, against the client's <see cref="YFex.Authorization.Public.IPrincipalAccessor"/> —
/// for UX only (fail fast, no round trip); the server enforces it again (validation D29). With the per-call header option
/// (<see cref="YFexMessagingRpcClientOptions.CallContext"/>) each call carries the token (and tenant) as RPC headers.
/// </remarks>
public sealed class FusionMessageBus : IDispatcher
{
    private readonly FrozenDictionary<Type, QueryDispatchEntry> _queries;
    private readonly FrozenDictionary<Type, CommandDispatchEntry> _commands;
    private readonly FrozenDictionary<Type, VoidCommandDispatchEntry> _voidCommands;
    private readonly CompiledMessagingRegistry _registry;
    private readonly INetworkStatus _networkStatus;
    private readonly ICache _cache;
    private readonly IOutbox _outbox;
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _sp;
    private readonly CallPrincipalSource _principal;
    private readonly CallContextClientBuilder? _callContext;
    private readonly IScopeContext? _scope;

    internal FusionMessageBus(
        FrozenDictionary<Type, QueryDispatchEntry> queries,
        FrozenDictionary<Type, CommandDispatchEntry> commands,
        FrozenDictionary<Type, VoidCommandDispatchEntry> voidCommands,
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        ICache cache,
        IOutbox outbox,
        IEventBus eventBus,
        IServiceProvider sp)
    {
        _queries = queries;
        _commands = commands;
        _voidCommands = voidCommands;
        _registry = registry;
        _networkStatus = networkStatus;
        _cache = cache;
        _outbox = outbox;
        _eventBus = eventBus;
        _sp = sp;
        _principal = new CallPrincipalSource(sp);
        _callContext = sp.GetService<CallContextClientBuilder>();
        _scope = _callContext?.Tenant == true ? sp.GetService<IScopeContext>() : null;
    }

    // ── Call context (D29): per-call headers ─────────────────────────────────

    private async ValueTask<RpcHeader[]?> HeadersAsync(CancellationToken ct)
    {
        if (_callContext is null) return null;
        RpcHeader[]? headers = null;
        if (_callContext.TokenProvider is { } provider
            && await provider(_sp, ct).ConfigureAwait(false) is { Length: > 0 } token)
            headers = headers.With(new RpcHeader(_callContext.TokenHeader, _callContext.TokenScheme + " " + token));
        if (_callContext.Tenant && _scope.EffectiveIdentity() is { IsEmpty: false } scope)
            headers = headers.With(new RpcHeader(CallContextHeaders.Scope, scope.ToString()));
        return headers;
    }

    // The proxy call is made synchronously inside the setup scope: Fusion's RpcInterceptor consumes it for the outbound
    // call (regular calls; Fusion compute-client calls create their own setup and carry no custom headers).
    private static Task<TR> Send<TM, TR>(Func<TM, CancellationToken, Task<TR>> call, TM message, RpcHeader[]? headers, CancellationToken ct)
    {
        if (headers is null) return call(message, ct);
        using (new RpcOutboundCallSetup { Headers = headers }.Activate())
            return call(message, ct);
    }

    private static Task Send<TM>(Func<TM, CancellationToken, Task> call, TM message, RpcHeader[]? headers, CancellationToken ct)
    {
        if (headers is null) return call(message, ct);
        using (new RpcOutboundCallSetup { Headers = headers }.Activate())
            return call(message, ct);
    }

    public async ValueTask<CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>
    {
        // Coerce (paging clamps) → stage 1, before the offline cache, the cache key and the server call (09 §7).
        query = MessagePipeline.Coerce(in query);
        if (!MessagePipeline.CheckShape(in query, out var shape))
            return CacheableQueryResult<TResult>.ValidationProblem(shape);

        if (!_networkStatus.IsConnected)
        {
            if (query is not ICacheable) return CacheableQueryResult<TResult>.Fail("No network connectivity.");
            _registry.Queries.TryGetValue(typeof(TQuery), out var offlinePolicy);
            var cacheKey = QueryCacheKey.For(typeof(TQuery), query!, offlinePolicy, _sp);
            if (cacheKey is null) return CacheableQueryResult<TResult>.Fail("No cached value available offline.");

            // Provenance-aware read: carry the stale bit (offline invalidation marks entries stale,
            // not deleted) so the UI can badge it and refresh on reconnect.
            var lookup = await _cache.TryGetAsync<TResult>(cacheKey, ct).ConfigureAwait(false);
            if (!lookup.TryGetHit(out var cv))
                return CacheableQueryResult<TResult>.Fail("No cached value available offline.");
            return cv.IsStale
                ? new Stale<TResult>(cv.GetValue(), cv.LastModified)
                : new Cached<TResult>(cv.GetValue(), cv.LastModified);
        }

        if (!_queries.TryGetValue(typeof(TQuery), out var entry))
            return CacheableQueryResult<TResult>.Fail($"No Fusion route registered for {typeof(TQuery).Name}. Ensure the source generator has run.");

        // Stage 2 client-side (UX; the server re-checks).
        _registry.Queries.TryGetValue(typeof(TQuery), out var queryPolicy);
        if (!_principal.Authorize(queryPolicy?.Authorize, query!))
            return CacheableQueryResult<TResult>.Unauthorized();

        var headers = await HeadersAsync(ct).ConfigureAwait(false);
        TResult result;
        switch (entry.Call)
        {
            case Func<TQuery, CancellationToken, Task<Result<TResult>>> typed:
                var r = await Send(typed, query, headers, ct).ConfigureAwait(false);
                if (r.TryGetValue(out Error error)) return error;
                if (!r.TryGetValue(out TResult? value)) return CacheableQueryResult<TResult>.Fail("Empty result.");
                result = value!;
                break;
            case Func<TQuery, CancellationToken, Task<TResult>> plain:
                result = await Send(plain, query, headers, ct).ConfigureAwait(false);
                break;
            default:
                return CacheableQueryResult<TResult>.Fail($"Fusion route for {typeof(TQuery).Name} has an unexpected shape.");
        }

        if (query is ICacheable)
        {
            _registry.Queries.TryGetValue(typeof(TQuery), out var policy);
            var cacheKey = QueryCacheKey.For(typeof(TQuery), query!, policy, _sp)!;
            await _cache.SetAsync(cacheKey, result, QueryCacheKey.Options(typeof(TQuery), policy, query!), ct).ConfigureAwait(false);
        }

        return new Fresh<TResult>(result);
    }

    public async ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>
    {
        if (!_networkStatus.IsConnected)
            return await OfflineCommandAsync<TCommand, TResult>(cmd, ct).ConfigureAwait(false);

        if (!_commands.TryGetValue(typeof(TCommand), out var entry))
            return QueueableResult<TResult>.Fail($"No Fusion route for {typeof(TCommand).Name}.");

        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult<TResult>.Invalid(shape);

        // Stage 2 client-side (UX; the server re-checks).
        _registry.Commands.TryGetValue(typeof(TCommand), out var commandPolicy);
        if (!_principal.Authorize(commandPolicy?.Authorize, cmd!))
            return QueueableResult<TResult>.Unauthorized("Unauthorized.");

        var headers = await HeadersAsync(ct).ConfigureAwait(false);
        TResult result;
        switch (entry.Call)
        {
            case Func<TCommand, CancellationToken, Task<QueueableResult<TResult>>> typed:
                var r = await Send(typed, cmd, headers, ct).ConfigureAwait(false);
                if (r.TryGetError(out var error)) return error;
                if (!r.TryGetValue(out TResult? value)) return r;   // queued (never from a server) / empty: pass through
                result = value!;
                break;
            case Func<TCommand, CancellationToken, Task<TResult>> plain:
                result = await Send(plain, cmd, headers, ct).ConfigureAwait(false);
                break;
            default:
                return QueueableResult<TResult>.Fail($"Fusion route for {typeof(TCommand).Name} has an unexpected shape.");
        }

        await InvalidateCachesAsync(cmd!, typeof(TCommand), ct).ConfigureAwait(false);
        return QueueableResult<TResult>.Ok(result);
    }

    public async ValueTask<QueueableResult> CommandAsync<TCommand>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand
    {
        if (!_networkStatus.IsConnected)
            return await OfflineCommandVoidAsync(cmd, ct).ConfigureAwait(false);

        if (!_voidCommands.TryGetValue(typeof(TCommand), out var entry))
            return QueueableResult.Fail($"No Fusion route for {typeof(TCommand).Name}.");

        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult.Invalid(shape);

        // Stage 2 client-side (UX; the server re-checks).
        _registry.Commands.TryGetValue(typeof(TCommand), out var commandPolicy);
        if (!_principal.Authorize(commandPolicy?.Authorize, cmd!))
            return QueueableResult.Unauthorized("Unauthorized.");

        var headers = await HeadersAsync(ct).ConfigureAwait(false);
        switch (entry.Call)
        {
            case Func<TCommand, CancellationToken, Task<QueueableResult>> typed:
                var r = await Send(typed, cmd, headers, ct).ConfigureAwait(false);
                if (r.TryGetError(out var error)) return error;
                break;
            case Func<TCommand, CancellationToken, Task> plain:
                await Send(plain, cmd, headers, ct).ConfigureAwait(false);
                break;
            default:
                return QueueableResult.Fail($"Fusion route for {typeof(TCommand).Name} has an unexpected shape.");
        }
        await InvalidateCachesAsync(cmd!, typeof(TCommand), ct).ConfigureAwait(false);
        return QueueableResult.Ok();
    }

    public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent
    {
        _eventBus.Publish(evt!);
        return ValueTask.CompletedTask;
    }

    // ── Offline helpers ───────────────────────────────────────────────────────

    private async ValueTask<QueueableResult<TResult>> OfflineCommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand<TResult>
    {
        // Stage 1 before anything is queued: an invalid command is never enqueued (09 §8, G6).
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult<TResult>.Invalid(shape);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);
        await RunOfflineHandlerAsync(cmd!, policy, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkStaleAsync(cmd!, policy, ct).ConfigureAwait(false);
            var queued = await _outbox.EnqueueAsync(cmd, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandQueuedEvent(queued.IdempotencyKey, typeof(TCommand).Name, DateTimeOffset.UtcNow));
            return QueueableResult<TResult>.Queue(queued.IdempotencyKey);
        }

        return QueueableResult<TResult>.Fail("No network connectivity.");
    }

    private async ValueTask<QueueableResult> OfflineCommandVoidAsync<TCommand>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand
    {
        // Stage 1 before anything is queued: an invalid command is never enqueued (09 §8, G6).
        if (!MessagePipeline.CheckShape(in cmd, out var shape))
            return QueueableResult.Invalid(shape);

        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);
        await RunOfflineHandlerAsync(cmd!, policy, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkStaleAsync(cmd!, policy, ct).ConfigureAwait(false);
            var queued = await _outbox.EnqueueAsync(cmd, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandQueuedEvent(queued.IdempotencyKey, typeof(TCommand).Name, DateTimeOffset.UtcNow));
            return QueueableResult.Queue(queued.IdempotencyKey);
        }

        return QueueableResult.Fail("No network connectivity.");
    }

    private async ValueTask RunOfflineHandlerAsync(
        object cmd, YFex.Cqrs.Runtime.CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.OnOfflineHandler is not null)
        {
            await policy.OnOfflineHandler(cmd, ct).ConfigureAwait(false);
            return;
        }

        // Strongly-typed resolve + invoke captured at registration where the handler/command[/result] are
        // generic parameters — no reflection (was GetMethod/Invoke, an IL2075 site). The delegate resolves
        // the handler from DI (interface first, concrete fallback) and calls the typed HandleAsync.
        if (policy?.OnOfflineHandlerInvoke is not null)
            await policy.OnOfflineHandlerInvoke(_sp, cmd, ct).ConfigureAwait(false);
    }

    private async ValueTask InvalidateCachesAsync(object cmd, Type commandType, CancellationToken ct)
    {
        if (!_registry.Commands.TryGetValue(commandType, out var policy) || policy.InvalidationTargets is null)
            return;
        // Tag-based, no key enumeration (works on FusionCache). Precise when the target declares an
        // entity key (drops only en:{QueryType}:{key}); coarse otherwise (drops all variants).
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
            await _cache.RemoveByTagAsync(QueryCacheKey.InvalidationTag(targets[i], cmd), ct).ConfigureAwait(false);
    }

    private async ValueTask MarkStaleAsync(
        object cmd, YFex.Cqrs.Runtime.CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.InvalidationTargets is null) return;
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
            await _cache.ExpireByTagAsync(QueryCacheKey.InvalidationTag(targets[i], cmd), ct).ConfigureAwait(false);
    }
}
