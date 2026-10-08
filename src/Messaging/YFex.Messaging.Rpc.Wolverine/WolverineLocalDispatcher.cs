using Wolverine;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Cqrs.Runtime;
using IYCqrsCommand = YFex.Cqrs.ICommand;
using IYCqrsCommandResult = YFex.Cqrs.ICommand<object>; // just for constraint reference
using IYEvent = YFex.Cqrs.IEvent;
using IYQuery = YFex.Cqrs.IQuery<object>; // just for doc reference
using YCqrsResult = YFex.Cqrs.Result<object>;
using YCqrsQueued = YFex.Cqrs.QueueableResult;
using YCqrsQueuedT = YFex.Cqrs.QueueableResult<object>;

// The Wolverine package also defines ICommand and IEvent. All CQRS types
// below are explicitly qualified via the alias to eliminate ambiguity.
namespace YFex.Messaging.Rpc.Wolverine;

/// <summary>
/// Server-side <see cref="YFex.Cqrs.IDispatcher"/> that bypasses Fusion and routes directly to
/// Wolverine's <see cref="IMessageBus"/>. Used for in-process static-helper calls on the server.
/// The server has no offline scenario, so outbox / cache logic is intentionally absent.
/// </summary>
public sealed class WolverineLocalDispatcher : YFex.Cqrs.IDispatcher
{
    private readonly IMessageBus _bus;
    private readonly IEventBus _eventBus;
    private readonly YFex.Cqrs.CompiledMessagingRegistry? _registry;
    private readonly IServiceProvider? _services;
    private readonly CallPrincipalSource _principal;

    /// <param name="registry">Policies for validation stage 5 (context rules); stage 1 (shape) always runs.</param>
    /// <param name="services">Resolves context rules; required when <paramref name="registry"/> has any.</param>
    public WolverineLocalDispatcher(IMessageBus bus, IEventBus eventBus,
        YFex.Cqrs.CompiledMessagingRegistry? registry = null, IServiceProvider? services = null)
    {
        _bus      = bus;
        _eventBus = eventBus;
        _registry = registry;
        _services = services;
        _principal = new CallPrincipalSource(services, registry);
    }

    // Validation stages 1 → 2 → 5 (09 §1). Stage 2 authorizes the remote caller (CallContext, D29) or the host principal.
    // Returns null when valid, else the error.
    // Stages 3–4 (rules) run between authorize and context rules; the caller publishes the decision around the bus call.
    private async ValueTask<(YFex.Cqrs.Error? Error, YFex.Cqrs.Runtime.RulesStage? Stage, YFex.Cqrs.Runtime.RulesOutcome Rules)> ValidateAsync<T>(
        T message, bool isQuery, CancellationToken ct)
    {
        if (!MessagePipeline.CheckShape(in message, out var shape)) return (YFex.Cqrs.Error.Validation(shape), null, default);
        YFex.Cqrs.Runtime.MessageValidator? context = null;
        YFex.Cqrs.Runtime.RulesStage? stage = null;
        if (_registry is not null)
        {
            if (isQuery && _registry.Queries.TryGetValue(typeof(T), out var q)) (context, stage) = (q.Validation, q.Rules);
            else if (!isQuery && _registry.Commands.TryGetValue(typeof(T), out var c)) (context, stage) = (c.Validation, c.Rules);
        }
        var verdict = await _principal.AuthorizeAsync(message!, ct).ConfigureAwait(false);
        if (!verdict.IsAllowed) return (YFex.Cqrs.Error.From(verdict), null, default);
        YFex.Cqrs.Runtime.RulesOutcome rules = default;
        if (stage is not null && _services is not null)
        {
            rules = await stage.RunAsync(message!, _services, ct).ConfigureAwait(false);
            if (rules.Error.HasValue) return (rules.Error.Value, stage, rules);
        }
        if (context is null || _services is null) return (null, stage, rules);
        var result = await MessagePipeline.CheckContextAsync(message, context, _services, ct).ConfigureAwait(false);
        return (result.IsValid ? (YFex.Cqrs.Error?)null : YFex.Cqrs.Error.Validation(result), stage, rules);
    }

    public async ValueTask<YFex.Cqrs.CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : YFex.Cqrs.IQuery<TResult>
    {
        query = MessagePipeline.Coerce(in query);
        var (error, stage, rules) = await ValidateAsync(query, isQuery: true, ct).ConfigureAwait(false);
        if (error.HasValue) return error.Value;
        using var decisionScope = YFex.Cqrs.Runtime.DecisionContext.Enter(query!, stage, rules);

        // Server-side: always live/authoritative, no cache or offline path.
        var result = await _bus.InvokeAsync<TResult>(query!, ct).ConfigureAwait(false);
        return new YFex.Fresh<TResult>(result);
    }

    public async ValueTask<YFex.Cqrs.QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : YFex.Cqrs.ICommand<TResult>
    {
        var (error, stage, rules) = await ValidateAsync(cmd, isQuery: false, ct).ConfigureAwait(false);
        if (error.HasValue) return error.Value;
        using var decisionScope = YFex.Cqrs.Runtime.DecisionContext.Enter(cmd!, stage, rules);

        var result = await _bus.InvokeAsync<TResult>(cmd!, ct).ConfigureAwait(false);
        return YFex.Cqrs.QueueableResult<TResult>.Ok(result);
    }

    public async ValueTask<YFex.Cqrs.QueueableResult> CommandAsync<TCommand>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : YFex.Cqrs.ICommand
    {
        var (error, stage, rules) = await ValidateAsync(cmd, isQuery: false, ct).ConfigureAwait(false);
        if (error.HasValue) return error.Value;
        using var decisionScope = YFex.Cqrs.Runtime.DecisionContext.Enter(cmd!, stage, rules);

        await _bus.InvokeAsync(cmd!, ct).ConfigureAwait(false);
        return YFex.Cqrs.QueueableResult.Ok();
    }

    public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : YFex.Cqrs.IEvent
    {
        _eventBus.Publish(evt!);
        return ValueTask.CompletedTask;
    }
}
