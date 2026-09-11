namespace YFex.Cqrs.Compensation;

/// <summary>
/// Default <see cref="ICommandExecutedObserver"/>: appends a journal entry when — and only when —
/// the executed command's <c>CommandPolicy.Reverse</c> is set (i.e. it was declared reversible via
/// <c>.Reversible(...)</c> / <c>.ReversibleWith&lt;T&gt;()</c>). Non-reversible commands are ignored,
/// so the journal stays small.
/// </summary>
public sealed class CommandExecutedObserver : ICommandExecutedObserver
{
    private readonly ICommandJournal _journal;
    private readonly CompiledMessagingRegistry _registry;
    private readonly Func<string?>? _principalAccessor;

    /// <param name="principalAccessor">Optional — captures the acting principal at execution time
    /// for the journal entry. When null, entries record a null principal.</param>
    public CommandExecutedObserver(
        ICommandJournal journal,
        CompiledMessagingRegistry registry,
        Func<string?>? principalAccessor = null)
    {
        _journal = journal;
        _registry = registry;
        _principalAccessor = principalAccessor;
    }

    public async ValueTask OnExecutedAsync(object command, object? result, CancellationToken ct)
    {
        if (command is null) return;

        if (!_registry.Commands.TryGetValue(command.GetType(), out var policy) || policy.Reverse is not { } reverse)
            return; // not reversible → nothing to journal

        await _journal.AppendAsync(
            command,
            result,
            reverse.ReverseCommandType,
            _principalAccessor?.Invoke(),
            ct).ConfigureAwait(false);
    }
}
