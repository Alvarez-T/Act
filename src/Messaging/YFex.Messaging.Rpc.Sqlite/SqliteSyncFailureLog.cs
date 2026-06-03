using Microsoft.Data.Sqlite;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Sqlite;

/// <summary>
/// <see cref="IWritableSyncFailureLog"/> backed by the dedicated <c>SyncFailures</c> SQLite table.
/// </summary>
public sealed class SqliteSyncFailureLog : IWritableSyncFailureLog
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private IOutbox? _outbox;

    internal void SetOutbox(IOutbox outbox) => _outbox = outbox;

    public SqliteSyncFailureLog(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<SyncFailure> Failures =>
        ListAllAsync(default).AsTask().GetAwaiter().GetResult();

    public SyncFailure? Find(Guid id) =>
        FindAsync(id, default).AsTask().GetAwaiter().GetResult();

    public async ValueTask AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "UPDATE SyncFailures SET IsAcknowledged = 1 WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask RetryAsync(Guid id, CancellationToken ct = default)
    {
        var failure = await FindAsync(id, ct);
        if (failure is null || _outbox is null) return;

        if (_outbox is SqliteOutbox sqlite)
            await sqlite.EnqueueRawAsync(failure.IdempotencyKey, failure.CommandTypeName, failure.Payload, ct);

        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncFailures WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask AddAsync(SyncFailure failure, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO SyncFailures
                    (Id, CommandTypeName, Payload, FailureReason, OccurredAt, IsAcknowledged)
                VALUES ($id, $typeName, $payload, $reason, $occurredAt, $ack)
                """;
            cmd.Parameters.AddWithValue("$id", failure.IdempotencyKey.ToString());
            cmd.Parameters.AddWithValue("$typeName", failure.CommandTypeName);
            cmd.Parameters.AddWithValue("$payload", failure.Payload);
            cmd.Parameters.AddWithValue("$reason", failure.Reason);
            cmd.Parameters.AddWithValue("$occurredAt", failure.FailedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$ack", failure.IsAcknowledged ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
        FailureAdded?.Invoke(failure);
    }

    public event Action<SyncFailure>? FailureAdded;

    // ── private helpers ──────────────────────────────────────────────────────

    private async ValueTask<IReadOnlyList<SyncFailure>> ListAllAsync(CancellationToken ct)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CommandTypeName, Payload, FailureReason, OccurredAt, IsAcknowledged
            FROM SyncFailures ORDER BY OccurredAt DESC
            """;
        var list = new List<SyncFailure>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapFailure(reader));
        return list;
    }

    private async ValueTask<SyncFailure?> FindAsync(Guid id, CancellationToken ct)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CommandTypeName, Payload, FailureReason, OccurredAt, IsAcknowledged
            FROM SyncFailures WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapFailure(reader) : null;
    }

    private static SyncFailure MapFailure(SqliteDataReader r) => new(
        IdempotencyKey:  Guid.Parse(r.GetString(0)),
        CommandTypeName: r.GetString(1),
        Payload:         (byte[])r.GetValue(2),
        Reason:          r.GetString(3),
        FailedAt:        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
        IsAcknowledged:  r.GetInt64(5) == 1L);
}
