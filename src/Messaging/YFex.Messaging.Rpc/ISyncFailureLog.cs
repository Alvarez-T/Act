namespace YFex.Messaging.Rpc;

/// <summary>
/// A command (or orchestration step) that could not be completed after exhausting retries or TTL.
/// The three trailing properties (design §8.1) generalize the log to carry orchestration provenance:
/// they stay at their defaults for the original outbox-expiry path and are populated when a saga/PM
/// dead-letters a step.
/// </summary>
public sealed record SyncFailure(
    Guid IdempotencyKey,
    string CommandTypeName,
    byte[] Payload,
    string Reason,
    DateTimeOffset FailedAt,
    bool IsAcknowledged)
{
    /// <summary>Originating saga/PM identity (design §8.1). Null for outbox-origin failures.</summary>
    public string? SagaId { get; init; }

    /// <summary>The failing step's index within a saga, when applicable (design §8.1).</summary>
    public int? StepIndex { get; init; }

    /// <summary>Failure category — e.g. <c>"outbox"</c>, <c>"saga-compensation"</c>, <c>"saga-escalate"</c> (design §8.1).</summary>
    public string Category { get; init; } = "outbox";

    /// <summary>
    /// Structured violations when the server rejected the replayed command with a validation error (G7) — the "sync
    /// issues" screen shows them with messages and paths instead of <see cref="Reason"/>'s flat text.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(YFex.Messaging.Rpc.Serialization.ViolationListJsonConverter))]
    public YFex.Validation.ViolationList Violations { get; init; }
}

/// <summary>
/// Read-only view of commands that permanently failed to sync.
/// Implementations must also implement <see cref="IWritableSyncFailureLog"/> (internal).
/// </summary>
public interface ISyncFailureLog
{
    IReadOnlyList<SyncFailure> Failures { get; }
    SyncFailure? Find(Guid idempotencyKey);
    ValueTask AcknowledgeAsync(Guid idempotencyKey, CancellationToken ct = default);
    ValueTask RetryAsync(Guid idempotencyKey, CancellationToken ct = default);
    event Action<SyncFailure>? FailureAdded;
}

/// <summary>Write path used by <see cref="OutboxReplayer"/> and tests.</summary>
public interface IWritableSyncFailureLog : ISyncFailureLog
{
    ValueTask AddAsync(SyncFailure failure, CancellationToken ct = default);
}

/// <summary>Volatile in-memory <see cref="ISyncFailureLog"/> for tests.</summary>
public sealed class InMemorySyncFailureLog : IWritableSyncFailureLog
{
    private readonly List<SyncFailure> _failures = [];
    private readonly object _lock = new();

    // Injected by the DI container — used by RetryAsync to re-enqueue
    private IOutbox? _outbox;
    internal void SetOutbox(IOutbox outbox) => _outbox = outbox;

    public IReadOnlyList<SyncFailure> Failures
    {
        get { lock (_lock) return [.._failures]; }
    }

    public SyncFailure? Find(Guid id)
    {
        lock (_lock)
        {
            for (int i = 0; i < _failures.Count; i++)
                if (_failures[i].IdempotencyKey == id) return _failures[i];
            return null;
        }
    }

    public ValueTask AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            for (int i = 0; i < _failures.Count; i++)
            {
                if (_failures[i].IdempotencyKey == id)
                {
                    _failures[i] = _failures[i] with { IsAcknowledged = true };
                    break;
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask RetryAsync(Guid id, CancellationToken ct = default)
    {
        SyncFailure? failure = null;
        lock (_lock)
        {
            for (int i = 0; i < _failures.Count; i++)
                if (_failures[i].IdempotencyKey == id) { failure = _failures[i]; break; }
        }
        if (failure is null || _outbox is null) return;

        // Re-enqueue the raw entry via the outbox's internal raw-entry path
        if (_outbox is InMemoryOutbox raw)
            await raw.EnqueueRawAsync(failure.IdempotencyKey, failure.CommandTypeName, failure.Payload, ct).ConfigureAwait(false);

        lock (_lock) _failures.RemoveAll(f => f.IdempotencyKey == id);
    }

    public ValueTask AddAsync(SyncFailure failure, CancellationToken ct = default)
    {
        lock (_lock) _failures.Add(failure);
        FailureAdded?.Invoke(failure);
        return ValueTask.CompletedTask;
    }

    public event Action<SyncFailure>? FailureAdded;
}
