using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>Handler for a query of type <typeparamref name="TQuery"/> returning <typeparamref name="TResult"/>.</summary>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}

/// <summary>Handler for a void command (no result).</summary>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken ct);
}

/// <summary>Handler for a result-bearing command.</summary>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}
