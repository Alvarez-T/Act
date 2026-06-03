using System.Text.Json;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="IOutbox"/> backed by any <see cref="IClientStorage"/> using the <c>outbox:</c>
/// key prefix (Option A). Suitable for IndexedDB and any future custom backend.
/// Each entry is stored at <c>outbox:{idempotencyKey}</c> as a JSON blob.
/// </summary>
public sealed class StorageBackedOutbox : IOutbox
{
    private const string Prefix = "outbox:";

    private readonly IClientStorage _storage;
    private readonly OutboxOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IWritableSyncFailureLog? _failureLog;
    private int _pendingCount = -1; // -1 = needs scan

    internal void SetFailureLog(IWritableSyncFailureLog log) => _failureLog = log;

    public StorageBackedOutbox(IClientStorage storage, OutboxOptions options)
    {
        _storage = storage;
        _options = options;
    }

    public int PendingCount
    {
        get
        {
            if (_pendingCount >= 0) return _pendingCount;
            // Cold read — enumerate keys synchronously (only called from sync property)
            var keys = _storage.GetKeysWithPrefixAsync(Prefix).AsTask().GetAwaiter().GetResult();
            return _pendingCount = keys.Count;
        }
    }

    public event Action<OutboxEntry>? Enqueued;
    public event Action<Guid>? Drained;

    public async ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand
    {
        var key = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, typeof(T));
        var entry = new OutboxEntry(key, typeof(T).AssemblyQualifiedName ?? typeof(T).FullName!,
            payload, DateTimeOffset.UtcNow, 0, null, null);

        OutboxEntry? evicted = null;
        await _writeLock.WaitAsync(ct);
        try
        {
            var keys = await _storage.GetKeysWithPrefixAsync(Prefix, ct);
            if (keys.Count >= _options.MaxEntries && keys.Count > 0)
                evicted = await EvictOldestAsync(keys, ct);

            await WriteEntryAsync(entry, ct);
            _pendingCount = -1;
        }
        finally { _writeLock.Release(); }

        if (evicted is not null && _failureLog is not null)
            await _failureLog.AddAsync(new SyncFailure(evicted.IdempotencyKey, evicted.CommandTypeName,
                evicted.Payload, "outbox-overflow", DateTimeOffset.UtcNow, false), ct);

        Enqueued?.Invoke(entry);
        return new Queued(key);
    }

    internal async ValueTask EnqueueRawAsync(Guid key, string typeName, byte[] payload, CancellationToken ct)
    {
        var entry = new OutboxEntry(key, typeName, payload, DateTimeOffset.UtcNow, 0, null, null);
        await _writeLock.WaitAsync(ct);
        try { await WriteEntryAsync(entry, ct); _pendingCount = -1; }
        finally { _writeLock.Release(); }
        Enqueued?.Invoke(entry);
    }

    public async ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default)
    {
        var keys = await _storage.GetKeysWithPrefixAsync(Prefix, ct);
        var entries = new List<OutboxEntry>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            var bytes = await _storage.GetAsync(keys[i], ct);
            if (bytes is not null)
            {
                var e = JsonSerializer.Deserialize<OutboxEntryDto>(bytes);
                if (e is not null) entries.Add(e.ToEntry());
            }
        }
        entries.Sort(static (a, b) => a.EnqueuedAt.CompareTo(b.EnqueuedAt));
        return entries;
    }

    public async ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default)
    {
        var storageKey = Prefix + key.ToString();
        var bytes = await _storage.GetAsync(storageKey, ct);
        if (bytes is null) return;
        var dto = JsonSerializer.Deserialize<OutboxEntryDto>(bytes);
        if (dto is null) return;
        dto.AttemptCount++;
        dto.LastFailure = failure;
        dto.LastAttemptAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _writeLock.WaitAsync(ct);
        try { await _storage.SetAsync(storageKey, JsonSerializer.SerializeToUtf8Bytes(dto), ct: ct); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask RemoveAsync(Guid key, CancellationToken ct = default)
    {
        await _storage.DeleteAsync(Prefix + key.ToString(), ct);
        _pendingCount = -1;
        Drained?.Invoke(key);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async ValueTask WriteEntryAsync(OutboxEntry e, CancellationToken ct)
    {
        var dto = OutboxEntryDto.From(e);
        await _storage.SetAsync(Prefix + e.IdempotencyKey.ToString(),
            JsonSerializer.SerializeToUtf8Bytes(dto), ct: ct);
    }

    private async ValueTask<OutboxEntry?> EvictOldestAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        OutboxEntry? oldest = null;
        for (int i = 0; i < keys.Count; i++)
        {
            var bytes = await _storage.GetAsync(keys[i], ct);
            if (bytes is null) continue;
            var e = JsonSerializer.Deserialize<OutboxEntryDto>(bytes)?.ToEntry();
            if (e is null) continue;
            if (oldest is null || e.EnqueuedAt < oldest.EnqueuedAt) oldest = e;
        }
        if (oldest is null) return null;
        await _storage.DeleteAsync(Prefix + oldest.IdempotencyKey.ToString(), ct);
        return oldest;
    }

    private sealed class OutboxEntryDto
    {
        public string Key { get; set; } = "";
        public string TypeName { get; set; } = "";
        public byte[] Payload { get; set; } = [];
        public long EnqueuedAtMs { get; set; }
        public int AttemptCount { get; set; }
        public string? LastFailure { get; set; }
        public long? LastAttemptAtMs { get; set; }

        public OutboxEntry ToEntry() => new(
            Guid.Parse(Key), TypeName, Payload,
            DateTimeOffset.FromUnixTimeMilliseconds(EnqueuedAtMs),
            AttemptCount, LastFailure,
            LastAttemptAtMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(LastAttemptAtMs.Value) : null);

        public static OutboxEntryDto From(OutboxEntry e) => new()
        {
            Key = e.IdempotencyKey.ToString(),
            TypeName = e.CommandTypeName,
            Payload = e.Payload,
            EnqueuedAtMs = e.EnqueuedAt.ToUnixTimeMilliseconds(),
            AttemptCount = e.AttemptCount,
            LastFailure = e.LastFailure,
            LastAttemptAtMs = e.LastAttemptAt?.ToUnixTimeMilliseconds()
        };
    }
}
