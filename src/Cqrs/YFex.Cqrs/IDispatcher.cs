namespace YFex.Cqrs;

/// <summary>
/// Transport-agnostic dispatch entry point. Registered in DI; the static helpers call
/// <see cref="YFexDispatcherProvider.Current"/> to resolve it without depending on DI directly.
/// </summary>
public interface IDispatcher
{
    ValueTask<Result<TResult>> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>;

    ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>;

    ValueTask<QueueableResult> CommandAsync<TCommand>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand;

    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent;
}
