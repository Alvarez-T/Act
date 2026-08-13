namespace YFex.Persistence;

/// <summary>
/// No-op <see cref="IDurableQueue{T}"/>. Discards everything enqueued and always
/// dequeues empty. Use as the default when durable queueing is disabled.
/// </summary>
public sealed class NullDurableQueue<T> : IDurableQueue<T>
{
    public Task EnqueueAsync(IReadOnlyList<T> items, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<T>> DequeueAsync(int maxCount, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<T>>([]);

    public Task PurgeExpiredAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
