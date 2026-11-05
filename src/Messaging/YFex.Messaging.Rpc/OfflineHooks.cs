using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Admits a command to run without central, BEFORE its offline handler runs (offline 06 §4: allowance reservations, store-wide unique
/// claims). Registered by YFex.Offline (<c>UseRpc()</c>); without one every offline-capable command runs as before. The dispatchers
/// (<see cref="FusionMessageBus"/>, <see cref="LocalDispatcher"/>) call <see cref="AdmitAsync"/> after stage 1, and
/// <see cref="Abandon"/> when the handler throws.
/// </summary>
public interface IOfflineAdmission
{
    /// <summary>
    /// Reserves what <paramref name="command"/> needs offline. Returns <see langword="default"/> to proceed, or the refusal
    /// (<c>Error.Unavailable(AllowanceExhausted)</c>, <c>Error.Conflict</c> for a claimed value…): nothing runs, nothing is queued.
    /// </summary>
    ValueTask<Error> AdmitAsync(object command, Type commandType, CancellationToken cancellationToken);

    /// <summary>The handler failed: release what <see cref="AdmitAsync"/> reserved for <paramref name="command"/>.</summary>
    void Abandon(object command);
}

/// <summary>
/// Replays a queued outbox entry another way than re-dispatching its command (offline Phase 3: the signed envelope goes to central's
/// intake, which runs it as the envelope's actor). <see cref="OutboxReplayer"/> asks it first; <see langword="null"/> = not handled here.
/// A transient failure throws (the replayer backs off); a permanent one is a rejected <see cref="ReplayOutcome"/>.
/// </summary>
public interface IOutboxReplayRoute
{
    ValueTask<ReplayOutcome?> TryReplayAsync(OutboxEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Told about every command the client's outbox queues, after its entry exists (offline Phase 3: the signed envelope is created and kept
/// in custody right then). Observers run in registration order; an observer that throws fails the dispatch (the entry stays queued).
/// </summary>
public interface IOutboxEnqueueObserver
{
    ValueTask OnEnqueuedAsync(object command, Type commandType, OutboxEntry entry, CancellationToken cancellationToken);
}

/// <summary>The client's <see cref="IOutbox"/> with its <see cref="IOutboxEnqueueObserver"/>s (registered only when there are any).</summary>
internal sealed class ObservedOutbox : IOutbox
{
    private readonly IOutbox _inner;
    private readonly IOutboxEnqueueObserver[] _observers;
    // Filled by the inner outbox's Enqueued event (raised inside EnqueueAsync, before it returns); taken by the same call.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, OutboxEntry> _recent = new();

    private ObservedOutbox(IOutbox inner, IOutboxEnqueueObserver[] observers)
    {
        _inner = inner;
        _observers = observers;
        _inner.Enqueued += OnInnerEnqueued;
    }

    public static IOutbox Wrap(IOutbox inner, IEnumerable<IOutboxEnqueueObserver> observers)
    {
        IOutboxEnqueueObserver[] list = [.. observers];
        return list.Length == 0 ? inner : new ObservedOutbox(inner, list);
    }

    public int PendingCount => _inner.PendingCount;

    public event Action<OutboxEntry>? Enqueued;

    public event Action<Guid>? Drained
    {
        add => _inner.Drained += value;
        remove => _inner.Drained -= value;
    }

    public async ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand
    {
        Queued queued = await _inner.EnqueueAsync(command, ct).ConfigureAwait(false);
        if (_recent.TryRemove(queued.IdempotencyKey, out OutboxEntry? entry))
            foreach (IOutboxEnqueueObserver observer in _observers)
                await observer.OnEnqueuedAsync(command!, typeof(T), entry, ct).ConfigureAwait(false);
        return queued;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default) => _inner.ListPendingAsync(ct);

    public ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default) => _inner.MarkAttemptedAsync(key, failure, ct);

    public ValueTask RemoveAsync(Guid key, CancellationToken ct = default) => _inner.RemoveAsync(key, ct);

    private void OnInnerEnqueued(OutboxEntry entry)
    {
        if (_recent.Count > 1024) _recent.Clear();   // raw re-enqueues (failure-log retries) have no taker
        _recent[entry.IdempotencyKey] = entry;
        Enqueued?.Invoke(entry);
    }
}
