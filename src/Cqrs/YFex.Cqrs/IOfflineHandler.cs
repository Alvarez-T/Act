namespace YFex.Cqrs;

/// <summary>Handles a command locally when the device is offline (no result variant).</summary>
public interface IOfflineHandler<TCommand> where TCommand : ICommand
{
    ValueTask HandleAsync(TCommand command, CancellationToken ct);
}

/// <summary>Handles a command locally when the device is offline, producing a local result.</summary>
public interface IOfflineHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    ValueTask<TResult> HandleAsync(TCommand command, CancellationToken ct);
}
