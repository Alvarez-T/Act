using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Messaging;

namespace YFex.Messaging.Rpc;

// ── Routing entry types ──────────────────────────────────────────────────────

/// <summary>Pre-compiled dispatch entry for a single query type.</summary>
internal sealed class QueryDispatchEntry
{
    internal required Func<object, CancellationToken, Task<object?>> Dispatch { get; init; }
}

/// <summary>Pre-compiled dispatch entry for a single command type (returns result).</summary>
internal sealed class CommandDispatchEntry
{
    internal required Func<object, CancellationToken, Task<object?>> Dispatch { get; init; }
}

/// <summary>Pre-compiled dispatch entry for a void command type.</summary>
internal sealed class VoidCommandDispatchEntry
{
    internal required Func<object, CancellationToken, Task> Dispatch { get; init; }
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

    /// <summary>Registers a Fusion proxy call for a query type. Called by generated registration code.</summary>
    public FusionMessageBusBuilder AddQuery<TQuery, TResult>(
        Func<TQuery, CancellationToken, Task<TResult>> proxyCall)
        where TQuery : IQuery<TResult>
    {
        _queries.Add((typeof(TQuery), new QueryDispatchEntry
        {
            Dispatch = async (msg, ct) => await proxyCall((TQuery)msg, ct).ConfigureAwait(false),
        }));
        return this;
    }

    /// <summary>Registers a Fusion proxy call for a result-bearing command.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand, TResult>(
        Func<TCommand, CancellationToken, Task<TResult>> proxyCall)
        where TCommand : ICommand<TResult>
    {
        _commands.Add((typeof(TCommand), new CommandDispatchEntry
        {
            Dispatch = async (msg, ct) => await proxyCall((TCommand)msg, ct).ConfigureAwait(false),
        }));
        return this;
    }

    /// <summary>Registers a Fusion proxy call for a void command.</summary>
    public FusionMessageBusBuilder AddCommand<TCommand>(
        Func<TCommand, CancellationToken, Task> proxyCall)
        where TCommand : ICommand
    {
        _voidCommands.Add((typeof(TCommand), new VoidCommandDispatchEntry
        {
            Dispatch = async (msg, ct) => await proxyCall((TCommand)msg, ct).ConfigureAwait(false),
        }));
        return this;
    }

    public FusionMessageBus Build(
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        IClientCache cache,
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
public sealed class FusionMessageBus : IDispatcher
{
    private readonly FrozenDictionary<Type, QueryDispatchEntry> _queries;
    private readonly FrozenDictionary<Type, CommandDispatchEntry> _commands;
    private readonly FrozenDictionary<Type, VoidCommandDispatchEntry> _voidCommands;
    private readonly CompiledMessagingRegistry _registry;
    private readonly INetworkStatus _networkStatus;
    private readonly IClientCache _cache;
    private readonly IOutbox _outbox;
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _sp;

    internal FusionMessageBus(
        FrozenDictionary<Type, QueryDispatchEntry> queries,
        FrozenDictionary<Type, CommandDispatchEntry> commands,
        FrozenDictionary<Type, VoidCommandDispatchEntry> voidCommands,
        CompiledMessagingRegistry registry,
        INetworkStatus networkStatus,
        IClientCache cache,
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
    }

    public async ValueTask<Result<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>
    {
        if (!_networkStatus.IsConnected)
        {
            if (query is not ICacheable) return Result<TResult>.Fail("No network connectivity.");
            var cacheKey = CacheKey<TQuery>(query);
            var cached = await _cache.GetAsync<TResult>(cacheKey, ct).ConfigureAwait(false);
            return cached is not null
                ? Result<TResult>.Ok(cached)
                : Result<TResult>.Fail("No cached value available offline.");
        }

        if (!_queries.TryGetValue(typeof(TQuery), out var entry))
            return Result<TResult>.Fail($"No Fusion route registered for {typeof(TQuery).Name}. Ensure the source generator has run.");

        var raw = await entry.Dispatch(query!, ct).ConfigureAwait(false);
        var result = (TResult)raw!;

        if (query is ICacheable)
        {
            _registry.Queries.TryGetValue(typeof(TQuery), out var policy);
            await _cache.SetAsync(CacheKey<TQuery>(query), result, policy?.Cache?.AbsoluteExpiration, ct).ConfigureAwait(false);
        }

        return Result<TResult>.Ok(result);
    }

    public async ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>
    {
        if (!_networkStatus.IsConnected)
            return await OfflineCommandAsync<TCommand, TResult>(cmd, ct).ConfigureAwait(false);

        if (!_commands.TryGetValue(typeof(TCommand), out var entry))
            return QueueableResult<TResult>.Fail($"No Fusion route for {typeof(TCommand).Name}.");

        var raw = await entry.Dispatch(cmd!, ct).ConfigureAwait(false);
        var result = (TResult)raw!;

        await InvalidateCachesAsync(typeof(TCommand), ct).ConfigureAwait(false);
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

        await entry.Dispatch(cmd!, ct).ConfigureAwait(false);
        await InvalidateCachesAsync(typeof(TCommand), ct).ConfigureAwait(false);
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
        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);
        await RunOfflineHandlerAsync(cmd!, policy, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkStaleAsync(typeof(TCommand), policy, ct).ConfigureAwait(false);
            var queued = await _outbox.EnqueueAsync(cmd, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandQueuedEvent(queued.IdempotencyKey, typeof(TCommand).Name, DateTimeOffset.UtcNow));
            return QueueableResult<TResult>.Queue(queued.IdempotencyKey);
        }

        return QueueableResult<TResult>.Fail("No network connectivity.");
    }

    private async ValueTask<QueueableResult> OfflineCommandVoidAsync<TCommand>(
        TCommand cmd, CancellationToken ct) where TCommand : ICommand
    {
        _registry.Commands.TryGetValue(typeof(TCommand), out var policy);
        await RunOfflineHandlerAsync(cmd!, policy, ct).ConfigureAwait(false);

        if (cmd is IQueueable)
        {
            await MarkStaleAsync(typeof(TCommand), policy, ct).ConfigureAwait(false);
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
        if (policy?.OnOfflineHandlerType is null) return;

        var handler = _sp.GetService(policy.OnOfflineHandlerType);
        if (handler is null) return;

        // Invoke HandleAsync via reflection — this is the offline error path, not the hot path.
        var method = policy.OnOfflineHandlerType.GetMethod("HandleAsync",
            BindingFlags.Public | BindingFlags.Instance);
        if (method is null) return;

        var result = method.Invoke(handler, [cmd, ct]);
        if (result is Task t) await t.ConfigureAwait(false);
        else if (result is ValueTask vt) await vt.ConfigureAwait(false);
    }

    private async ValueTask InvalidateCachesAsync(Type commandType, CancellationToken ct)
    {
        if (!_registry.Commands.TryGetValue(commandType, out var policy) || policy.InvalidationTargets is null)
            return;
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
        {
            var prefix = $"query:{targets[i].QueryType.FullName}";
            var keys = await _cache.GetKeysWithPrefixAsync(prefix, ct).ConfigureAwait(false);
            for (int j = 0; j < keys.Count; j++)
                await _cache.InvalidateAsync(keys[j], ct).ConfigureAwait(false);
        }
    }

    private async ValueTask MarkStaleAsync(
        Type commandType, YFex.Cqrs.Runtime.CommandPolicy? policy, CancellationToken ct)
    {
        if (policy?.InvalidationTargets is null) return;
        var targets = policy.InvalidationTargets;
        for (int i = 0; i < targets.Length; i++)
        {
            var prefix = $"query:{targets[i].QueryType.FullName}";
            var keys = await _cache.GetKeysWithPrefixAsync(prefix, ct).ConfigureAwait(false);
            for (int j = 0; j < keys.Count; j++)
                await _cache.MarkStaleAsync(keys[j], ct).ConfigureAwait(false);
        }
    }

    private static string CacheKey<TQuery>(TQuery query) =>
        $"query:{typeof(TQuery).FullName}:{query!.GetHashCode()}";
}
