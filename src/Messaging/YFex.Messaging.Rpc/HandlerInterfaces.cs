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

// ── Validated handlers (validation design ⚑P7 → D24, opt-in) ─────────────────────────────────────────
// The pipeline (LocalDispatcher / ValidatingHandlerInvoker) has already run shape + context validation when the
// handler is invoked; Valid<T> makes that a compile-time guarantee (only YFex.Validation / the pipeline can create
// one). Register the handler in DI like any other; when both shapes are registered, the validated one wins.

/// <summary>Result-bearing command handler that receives the command already validated.</summary>
public interface IValidatedCommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(YFex.Validation.Valid<TCommand> command, CancellationToken ct);
}

/// <summary>Void command handler that receives the command already validated.</summary>
public interface IValidatedCommandHandler<TCommand> where TCommand : ICommand
{
    Task HandleAsync(YFex.Validation.Valid<TCommand> command, CancellationToken ct);
}
