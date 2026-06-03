using System.Text.Json;

namespace YFex.Messaging.Rpc;

/// <summary>
/// <see cref="IWritableSyncFailureLog"/> backed by any <see cref="IClientStorage"/> using the
/// <c>failure:</c> key prefix (Option A). Each failure is stored at
/// <c>failure:{idempotencyKey}</c> as a JSON blob.
/// </summary>
public sealed class StorageBackedSyncFailureLog : IWritableSyncFailureLog
{
    private const string Prefix = "failure:";

    private readonly IClientStorage _storage;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IOutbox? _outbox;

    internal void SetOutbox(IOutbox outbox) => _outbox = outbox;

    public StorageBackedSyncFailureLog(IClientStorage storage) => _storage = storage;

    public IReadOnlyList<SyncFailure> Failures =>
        ListAllAsync(default).AsTask().GetAwaiter().GetResult();

    public SyncFailure? Find(Guid id) =>
        FindAsync(id, default).AsTask().GetAwaiter().GetResult();

    public async ValueTask AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        var failure = await FindAsync(id, ct);
        if (failure is null) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            await _storage.SetAsync(Prefix + id.ToString(),
                JsonSerializer.SerializeToUtf8Bytes(failure with { IsAcknowledged = true }), ct: ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask RetryAsync(Guid id, CancellationToken ct = default)
    {
        var failure = await FindAsync(id, ct);
        if (failure is null || _outbox is null) return;

        if (_outbox is StorageBackedOutbox sbo)
            await sbo.EnqueueRawAsync(failure.IdempotencyKey, failure.CommandTypeName, failure.Payload, ct);

        await _writeLock.WaitAsync(ct);
        try { await _storage.DeleteAsync(Prefix + id.ToString(), ct); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask AddAsync(SyncFailure failure, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _storage.SetAsync(Prefix + failure.IdempotencyKey.ToString(),
                JsonSerializer.SerializeToUtf8Bytes(failure), ct: ct);
        }
        finally { _writeLock.Release(); }
        FailureAdded?.Invoke(failure);
    }

    public event Action<SyncFailure>? FailureAdded;

    // ── private helpers ──────────────────────────────────────────────────────

    private async ValueTask<IReadOnlyList<SyncFailure>> ListAllAsync(CancellationToken ct)
    {
        var keys = await _storage.GetKeysWithPrefixAsync(Prefix, ct);
        var list = new List<SyncFailure>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            var bytes = await _storage.GetAsync(keys[i], ct);
            if (bytes is not null)
            {
                var f = JsonSerializer.Deserialize<SyncFailure>(bytes);
                if (f is not null) list.Add(f);
            }
        }
        return list;
    }

    private async ValueTask<SyncFailure?> FindAsync(Guid id, CancellationToken ct)
    {
        var bytes = await _storage.GetAsync(Prefix + id.ToString(), ct);
        return bytes is null ? null : JsonSerializer.Deserialize<SyncFailure>(bytes);
    }
}
