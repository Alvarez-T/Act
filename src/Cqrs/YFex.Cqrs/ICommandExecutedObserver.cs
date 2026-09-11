namespace YFex.Cqrs;

/// <summary>
/// The dispatcher seam for post-execution observers (used by reversible commands / the command
/// journal in <c>YFex.Cqrs.Compensation</c>, but dependency-free here so any dispatcher can call it).
///
/// A dispatcher notifies this observer once, immediately after a command has executed
/// <b>successfully</b> (non-error, non-queued). An observer decides what to do with it — e.g. append
/// reversible commands to a journal so they can later be undone. Lives in <c>YFex.Cqrs</c> (beside
/// <see cref="IDispatcher"/>) so the dispatcher implementation depends only on the contract, never on
/// a specific consumer.
/// </summary>
/// <remarks>
/// Contract for dispatchers: after the underlying handler returns a successful result, resolve
/// <see cref="ICommandExecutedObserver"/> from DI (optional — may be absent) and call
/// <see cref="OnExecutedAsync"/> with the command and its result (<c>null</c> for the void overload).
/// It MUST NOT fire for queued (offline) or failed commands. A reverse command dispatched by a
/// consumer flows through the same dispatcher; because its own reverse policy is normally unset, the
/// observer naturally skips it — the dispatcher need not special-case it.
/// </remarks>
public interface ICommandExecutedObserver
{
    /// <summary>Invoked post-success. <paramref name="result"/> is null for void commands.</summary>
    ValueTask OnExecutedAsync(object command, object? result, CancellationToken ct);
}
