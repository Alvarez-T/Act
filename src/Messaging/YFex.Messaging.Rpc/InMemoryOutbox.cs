using System.Text.Json;
using System.Text.Json.Serialization;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Volatile in-memory <see cref="IOutbox"/>. Serializes commands with System.Text.Json so the
/// payload byte[] is valid for future persistent backends (Plan 4). Supply a source-generated
/// <see cref="JsonSerializerContext"/> covering the command types for AOT / trim-safe serialization.
/// </summary>
public sealed class InMemoryOutbox : IOutbox
{
    private readonly List<OutboxEntry> _entries = [];
    private readonly OutboxOptions _options;
    private readonly object _lock = new();
    private readonly JsonSerializerContext? _commandJsonContext;
    private readonly IOutboxCallerCapture? _callerCapture;
    private long _sequence;

    // Injected to handle overflow → failure log
    private IWritableSyncFailureLog? _failureLog;
    public void SetFailureLog(IWritableSyncFailureLog log) => _failureLog = log;

    public InMemoryOutbox(OutboxOptions options, JsonSerializerContext? commandJsonContext = null,
        IOutboxCallerCapture? callerCapture = null)
    {
        _options = options;
        _commandJsonContext = commandJsonContext;
        _callerCapture = callerCapture;
    }

    public int PendingCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    public event Action<OutboxEntry>? Enqueued;
    public event Action<Guid>? Drained;

    public async ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand
    {
        var key = Guid.CreateVersion7();
        var payload = OutboxCommandPayload.Serialize(command, _commandJsonContext);
        var caller = _callerCapture?.Capture() ?? default;
        var entry = new OutboxEntry(key, typeof(T).AssemblyQualifiedName ?? typeof(T).FullName!, payload,
            DateTimeOffset.UtcNow, 0, null, null, caller.Subject, caller.Scope, Interlocked.Increment(ref _sequence));

        OutboxEntry? evicted = null;
        lock (_lock)
        {
            if (_entries.Count >= _options.MaxEntries && _entries.Count > 0)
            {
                evicted = _entries[0];
                _entries.RemoveAt(0);
            }
            _entries.Add(entry);
        }

        if (evicted is not null && _failureLog is not null)
            await _failureLog.AddAsync(new SyncFailure(evicted.IdempotencyKey, evicted.CommandTypeName,
                evicted.Payload, "outbox-overflow", DateTimeOffset.UtcNow, false), ct).ConfigureAwait(false);

        Enqueued?.Invoke(entry);
        return new Queued(key);
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var pending = _entries.ToArray();
            Array.Sort(pending, OutboxEntry.CompareReplayOrder);
            return new(pending);
        }
    }

    public ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default)
    {
        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IdempotencyKey == key)
                {
                    _entries[i] = _entries[i] with
                    {
                        AttemptCount = _entries[i].AttemptCount + 1,
                        LastFailure = failure,
                        LastAttemptAt = DateTimeOffset.UtcNow
                    };
                    break;
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(Guid key, CancellationToken ct = default)
    {
        lock (_lock) _entries.RemoveAll(e => e.IdempotencyKey == key);
        Drained?.Invoke(key);
        return ValueTask.CompletedTask;
    }

    /// <summary>Re-enqueues a raw entry (used by <see cref="InMemorySyncFailureLog.RetryAsync"/>).</summary>
    internal ValueTask EnqueueRawAsync(Guid key, string typeName, byte[] payload, CancellationToken ct = default)
    {
        var entry = new OutboxEntry(key, typeName, payload, DateTimeOffset.UtcNow, 0, null, null,
            Sequence: Interlocked.Increment(ref _sequence));
        lock (_lock) _entries.Add(entry);
        Enqueued?.Invoke(entry);
        return ValueTask.CompletedTask;
    }
}
