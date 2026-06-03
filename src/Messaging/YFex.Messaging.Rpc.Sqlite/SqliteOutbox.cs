using System.Text.Json;
using Microsoft.Data.Sqlite;
using YFex.Cqrs;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Sqlite;

/// <summary>
/// <see cref="IOutbox"/> backed by the dedicated <c>Outbox</c> SQLite table (Option B).
/// Proper SQL indexes make drain-ordering and overflow eviction zero-scan operations.
/// </summary>
public sealed class SqliteOutbox : IOutbox
{
    private readonly SqliteConnectionFactory _factory;
    private readonly OutboxOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IWritableSyncFailureLog? _failureLog;

    internal void SetFailureLog(IWritableSyncFailureLog log) => _failureLog = log;

    public SqliteOutbox(SqliteConnectionFactory factory, OutboxOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public int PendingCount
    {
        get
        {
            _factory.EnsureInitializedAsync(default).AsTask().GetAwaiter().GetResult();
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Outbox";
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    public event Action<OutboxEntry>? Enqueued;
    public event Action<Guid>? Drained;

    public async ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand
    {
        await _factory.EnsureInitializedAsync(ct);
        var key = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, typeof(T));
        var entry = new OutboxEntry(key, typeof(T).AssemblyQualifiedName ?? typeof(T).FullName!,
            payload, DateTimeOffset.UtcNow, 0, null, null);

        OutboxEntry? evicted = null;
        await _writeLock.WaitAsync(ct);
        try
        {
            if (await CountInternalAsync(ct) >= _options.MaxEntries)
                evicted = await EvictOldestAsync(ct);
            await InsertAsync(entry, ct);
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
        await _factory.EnsureInitializedAsync(ct);
        var entry = new OutboxEntry(key, typeName, payload, DateTimeOffset.UtcNow, 0, null, null);
        await _writeLock.WaitAsync(ct);
        try { await InsertAsync(entry, ct); }
        finally { _writeLock.Release(); }
        Enqueued?.Invoke(entry);
    }

    public async ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT IdempotencyKey, CommandTypeName, Payload, EnqueuedAt,
                   AttemptCount, LastAttemptAt, LastFailureReason
            FROM Outbox ORDER BY EnqueuedAt ASC
            """;
        var entries = new List<OutboxEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(MapEntry(reader));
        return entries;
    }

    public async ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Outbox
                SET AttemptCount = AttemptCount + 1,
                    LastAttemptAt = $now,
                    LastFailureReason = $failure
                WHERE IdempotencyKey = $key
                """;
            cmd.Parameters.AddWithValue("$key", key.ToString());
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$failure", failure is null ? DBNull.Value : (object)failure);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask RemoveAsync(Guid key, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Outbox WHERE IdempotencyKey = $key";
            cmd.Parameters.AddWithValue("$key", key.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
        Drained?.Invoke(key);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async ValueTask<int> CountInternalAsync(CancellationToken ct)
    {
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Outbox";
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async ValueTask<OutboxEntry?> EvictOldestAsync(CancellationToken ct)
    {
        using var sel = _factory.Connection.CreateCommand();
        sel.CommandText = """
            SELECT IdempotencyKey, CommandTypeName, Payload, EnqueuedAt,
                   AttemptCount, LastAttemptAt, LastFailureReason
            FROM Outbox ORDER BY EnqueuedAt ASC LIMIT 1
            """;
        OutboxEntry? entry = null;
        using (var reader = await sel.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) entry = MapEntry(reader);

        if (entry is null) return null;

        using var del = _factory.Connection.CreateCommand();
        del.CommandText = "DELETE FROM Outbox WHERE IdempotencyKey = $key";
        del.Parameters.AddWithValue("$key", entry.IdempotencyKey.ToString());
        await del.ExecuteNonQueryAsync(ct);
        return entry;
    }

    private async Task InsertAsync(OutboxEntry e, CancellationToken ct)
    {
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO Outbox
                (IdempotencyKey, CommandTypeName, Payload, EnqueuedAt, AttemptCount, LastAttemptAt, LastFailureReason)
            VALUES ($key, $typeName, $payload, $enqueuedAt, $attempts, $lastAttemptAt, $lastFailure)
            """;
        cmd.Parameters.AddWithValue("$key", e.IdempotencyKey.ToString());
        cmd.Parameters.AddWithValue("$typeName", e.CommandTypeName);
        cmd.Parameters.AddWithValue("$payload", e.Payload);
        cmd.Parameters.AddWithValue("$enqueuedAt", e.EnqueuedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$attempts", e.AttemptCount);
        cmd.Parameters.AddWithValue("$lastAttemptAt", e.LastAttemptAt.HasValue
            ? (object)e.LastAttemptAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$lastFailure", e.LastFailure is null ? DBNull.Value : (object)e.LastFailure);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static OutboxEntry MapEntry(SqliteDataReader r) => new(
        IdempotencyKey:  Guid.Parse(r.GetString(0)),
        CommandTypeName: r.GetString(1),
        Payload:         (byte[])r.GetValue(2),
        EnqueuedAt:      DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
        AttemptCount:    r.GetInt32(4),
        LastFailure:     r.IsDBNull(6) ? null : r.GetString(6),
        LastAttemptAt:   r.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)));
}
