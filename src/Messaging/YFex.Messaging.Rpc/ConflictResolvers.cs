using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>Escalates the conflict — tells the dispatcher to return a <c>Conflict</c> error.</summary>
public sealed class EscalateConflictResolver<TCommand> : IConflictResolver<TCommand>
    where TCommand : ICommand
{
    public static readonly EscalateConflictResolver<TCommand> Instance = new();

    public ValueTask<ConflictPolicy> ResolveAsync(TCommand command, Error serverError, CancellationToken ct) =>
        new(ConflictPolicy.Escalate);
}

/// <summary>Instructs the dispatcher to re-enqueue the command for later retry.</summary>
public sealed class RetryLaterConflictResolver<TCommand> : IConflictResolver<TCommand>
    where TCommand : ICommand
{
    public ValueTask<ConflictPolicy> ResolveAsync(TCommand command, Error serverError, CancellationToken ct) =>
        new(ConflictPolicy.RetryLater);
}

/// <summary>Silently discards the command — the conflict is treated as a no-op.</summary>
public sealed class DiscardConflictResolver<TCommand> : IConflictResolver<TCommand>
    where TCommand : ICommand
{
    public static readonly DiscardConflictResolver<TCommand> Instance = new();

    public ValueTask<ConflictPolicy> ResolveAsync(TCommand command, Error serverError, CancellationToken ct) =>
        new(ConflictPolicy.Discard);
}
