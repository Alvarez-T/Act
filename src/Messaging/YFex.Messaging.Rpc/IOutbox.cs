using MemoryPack;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>A command awaiting replay in the offline outbox.</summary>
[MemoryPackable]
public sealed partial record OutboxEntry(
    Guid IdempotencyKey,
    string CommandTypeName,
    byte[] Payload,
    DateTimeOffset EnqueuedAt,
    int AttemptCount,
    string? LastFailure,
    DateTimeOffset? LastAttemptAt);

/// <summary>Constrains the maximum size of the offline outbox.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum number of pending entries. Oldest entries overflow to <see cref="ISyncFailureLog"/> with reason "outbox-overflow".</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Optional cap by total serialized size in bytes.</summary>
    public long? MaxStorageBytes { get; set; }
}

/// <summary>
/// Client-side offline command queue. Commands implementing <see cref="IQueueable"/> are
/// enqueued here when disconnected and drained by <see cref="OutboxReplayer"/> on reconnect.
/// </summary>
public interface IOutbox
{
    ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand;
    ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default);
    ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default);
    ValueTask RemoveAsync(Guid key, CancellationToken ct = default);
    int PendingCount { get; }
    event Action<OutboxEntry>? Enqueued;
    event Action<Guid>? Drained;
}
