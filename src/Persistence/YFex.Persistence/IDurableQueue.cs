namespace YFex.Persistence;

/// <summary>
/// Durable, ordered queue of <typeparamref name="T"/> items backed by a persistent store.
/// Items survive process restarts and are dequeued in FIFO order.
/// Implementations: <see cref="NullDurableQueue{T}"/> (no-op),
/// <c>SqliteDurableQueue&lt;T&gt;</c> (YFex.Persistence.Sqlite).
/// </summary>
/// <remarks>
/// This is the generic persistence primitive that domain-specific durable queues
/// (e.g. a telemetry offline queue) build on. The store never needs to know about
/// the domain type — serialization is supplied via <see cref="IQueueItemSerializer{T}"/>.
/// </remarks>
public interface IDurableQueue<T> : IAsyncDisposable
{
    /// <summary>Appends <paramref name="items"/> to the tail of the queue.</summary>
    Task EnqueueAsync(IReadOnlyList<T> items, CancellationToken ct);

    /// <summary>
    /// Removes and returns up to <paramref name="maxCount"/> non-expired items from the head.
    /// Returns an empty list when the queue is empty.
    /// </summary>
    Task<IReadOnlyList<T>> DequeueAsync(int maxCount, CancellationToken ct);

    /// <summary>Deletes items whose time-to-live has elapsed.</summary>
    Task PurgeExpiredAsync(CancellationToken ct);

    /// <summary>Returns the number of non-expired items currently queued.</summary>
    Task<int> CountAsync(CancellationToken ct);
}
