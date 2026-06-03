namespace YFex.Cqrs;

/// <summary>Custom conflict resolution logic for a command type.</summary>
public interface IConflictResolver<TCommand> where TCommand : ICommand
{
    ValueTask<ConflictPolicy> ResolveAsync(TCommand command, Error serverError, CancellationToken ct);
}
