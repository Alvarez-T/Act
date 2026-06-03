using Wolverine;
using YFex.Messaging;
using YFex.Messaging.Rpc;
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

    public WolverineLocalDispatcher(IMessageBus bus, IEventBus eventBus)
    {
        _bus      = bus;
        _eventBus = eventBus;
    }

    public async ValueTask<YFex.Cqrs.Result<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query, CancellationToken ct = default)
        where TQuery : YFex.Cqrs.IQuery<TResult>
    {
        var result = await _bus.InvokeAsync<TResult>(query!, ct).ConfigureAwait(false);
        return YFex.Cqrs.Result<TResult>.Ok(result);
    }

    public async ValueTask<YFex.Cqrs.QueueableResult<TResult>> CommandAsync<TCommand, TResult>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : YFex.Cqrs.ICommand<TResult>
    {
        var result = await _bus.InvokeAsync<TResult>(cmd!, ct).ConfigureAwait(false);
        return YFex.Cqrs.QueueableResult<TResult>.Ok(result);
    }

    public async ValueTask<YFex.Cqrs.QueueableResult> CommandAsync<TCommand>(
        TCommand cmd, CancellationToken ct = default)
        where TCommand : YFex.Cqrs.ICommand
    {
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
