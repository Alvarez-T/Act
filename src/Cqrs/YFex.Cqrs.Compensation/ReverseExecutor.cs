using System.Text.Json;
using YFex.Cqrs.Runtime;

namespace YFex.Cqrs.Compensation;

/// <summary>Materializes and dispatches the reverse of a journaled command.</summary>
public interface ICommandReverser
{
    /// <summary>Reverses a specific journal entry. Returns false when the entry is already reversed,
    /// its command type / reverse policy can no longer be resolved, or nothing was dispatched.</summary>
    ValueTask<bool> ReverseAsync(CommandJournalEntry entry, IDispatcher dispatcher, CancellationToken ct = default);

    /// <summary>Undoes the newest not-yet-reversed command. No-op (returns false) when the journal
    /// has no reversible entries left — so a double-undo with nothing pending is safe.</summary>
    ValueTask<bool> UndoLastAsync(IDispatcher dispatcher, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ICommandReverser"/>. The entire undo path is reflection-free and AOT-safe:
/// <list type="bullet">
/// <item>The original command type is resolved from the registry's known command types by name — no
/// <c>Type.GetType</c>.</item>
/// <item>The reverse command is produced by the pre-compiled <c>ReversePolicy.Map</c> and dispatched
/// by <c>ReversePolicy.DispatchReverse</c>, a strongly-typed delegate captured at registration where
/// <c>TReverse</c> was a generic parameter — no <c>MakeGenericMethod</c>.</item>
/// <item>Custom handlers run through <c>ReversePolicy.InvokeHandler</c>, captured where the handler /
/// command / result were generic parameters — no <c>Activator</c>, no <c>GetMethod</c>/<c>Invoke</c>.</item>
/// </list>
/// </summary>
public sealed class ReverseExecutor : ICommandReverser
{
    private readonly ICommandJournal _journal;
    private readonly CompiledMessagingRegistry _registry;
    private readonly IServiceProvider? _services; // only needed for custom-handler reversal

    // Reflection-free command-type resolution: the journal stores an assembly-qualified (or full) type
    // name; the registry already knows every registered command Type, so we map name → Type once at
    // construction and avoid the AOT-hostile Type.GetType on the undo path.
    private readonly Dictionary<string, Type> _commandTypesByName;

    public ReverseExecutor(ICommandJournal journal, CompiledMessagingRegistry registry, IServiceProvider? services = null)
    {
        _journal = journal;
        _registry = registry;
        _services = services;

        _commandTypesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in _registry.Commands.Keys)
        {
            if (t.AssemblyQualifiedName is { } aqn) _commandTypesByName[aqn] = t;
            if (t.FullName is { } fn) _commandTypesByName.TryAdd(fn, t);
        }
    }

    public async ValueTask<bool> UndoLastAsync(IDispatcher dispatcher, CancellationToken ct = default)
    {
        var entry = await _journal.TryPopNewestUnreversedAsync(ct).ConfigureAwait(false);
        if (entry is null) return false; // nothing to undo → safe no-op
        return await ReverseAsync(entry, dispatcher, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> ReverseAsync(CommandJournalEntry entry, IDispatcher dispatcher, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (entry.Reversed) return false; // idempotency

        // Resolve the original command type from the registry — no Type.GetType.
        if (!_commandTypesByName.TryGetValue(entry.CommandTypeName, out var originalType))
            return false;

        if (!_registry.Commands.TryGetValue(originalType, out var policy) || policy.Reverse is not { } reverse)
            return false;

        var original = JsonSerializer.Deserialize(entry.Command, originalType);
        if (original is null) return false;

        // Deserialize the journaled result against the DECLARED result type (typeof(TResult), captured
        // in the reverse policy) — again no Type.GetType.
        object? result = null;
        if (entry.Result is not null && reverse.ResultType is { } resultType)
            result = JsonSerializer.Deserialize(entry.Result, resultType);

        if (reverse.DispatchReverse is not null)
        {
            // Map materializes the typed reverse command; DispatchReverse dispatches it through the
            // generic IDispatcher.CommandAsync<TReverse> — both reflection-free.
            var reverseCommand = reverse.Map!(original, result);
            await reverse.DispatchReverse(dispatcher, reverseCommand, ct).ConfigureAwait(false);
        }
        else if (reverse.InvokeHandler is not null)
        {
            if (_services is null)
                throw new InvalidOperationException(
                    $"Reverse handler for '{originalType.FullName}' requires an IServiceProvider to resolve it.");
            await reverse.InvokeHandler(_services, original, result, ct).ConfigureAwait(false);
        }
        else
        {
            return false;
        }

        await _journal.MarkReversedAsync(entry.Seq, ct).ConfigureAwait(false);
        return true;
    }
}
