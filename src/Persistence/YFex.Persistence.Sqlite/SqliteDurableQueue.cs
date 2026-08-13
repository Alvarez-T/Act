using Microsoft.Data.Sqlite;
using YFex.Persistence;

namespace YFex.Persistence.Sqlite;

/// <summary>
/// <see cref="IDurableQueue{T}"/> over a SQLite table with FIFO drain, TTL, and indexed
/// ordering. Folds the former <c>SqliteOutbox</c> (native-table strategy) and
/// <c>DataDurableQueue&lt;T&gt;</c> (the YFex.Data bridge). Items are opaque serialized strings.
/// </summary>
public class SqliteDurableQueue<T> : IDurableQueue<T> where T : class
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IQueueItemSerializer<T> _serializer;
    private readonly TimeSpan _ttl;
    private readonly string _table;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _tableEnsured;

    public SqliteDurableQueue(
        SqliteConnectionFactory factory,
        IQueueItemSerializer<T> serializer,
        TimeSpan ttl,
        string table = "durable_queue")
    {
        _factory = factory;
        _serializer = serializer;
        _ttl = ttl;
        _table = table;
    }

    public async Task EnqueueAsync(IReadOnlyList<T> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long now = Now();
            long expires = now + (long)_ttl.TotalMilliseconds;
            foreach (var item in items)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = $"INSERT INTO {_table} (id, payload, created_at, expires_at) VALUES ($id, $payload, $created, $expires)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$payload", _serializer.Serialize(item));
                cmd.Parameters.AddWithValue("$created", now);
                cmd.Parameters.AddWithValue("$expires", expires);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<T>> DequeueAsync(int maxCount, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            long now = Now();

            var rows = new List<(string Id, string Payload)>();
            await using (var sel = conn.CreateCommand())
            {
                sel.CommandText = $"SELECT id, payload FROM {_table} WHERE expires_at > $now ORDER BY created_at, id LIMIT $max";
                sel.Parameters.AddWithValue("$now", now);
                sel.Parameters.AddWithValue("$max", maxCount);
                await using var reader = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (rows.Count > 0)
            {
                await using var del = conn.CreateCommand();
                var ids = string.Join(",", rows.Select((_, i) => "$id" + i));
                del.CommandText = $"DELETE FROM {_table} WHERE id IN ({ids})";
                for (int i = 0; i < rows.Count; i++)
                    del.Parameters.AddWithValue("$id" + i, rows[i].Id);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var items = new List<T>(rows.Count);
            foreach (var row in rows)
            {
                var item = _serializer.Deserialize(row.Payload);
                if (item is not null) items.Add(item);
            }
            return items;
        }
        finally { _writeLock.Release(); }
    }

    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_table} WHERE expires_at <= $now";
            cmd.Parameters.AddWithValue("$now", Now());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_table} WHERE expires_at > $now";
        cmd.Parameters.AddWithValue("$now", Now());
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        if (!_tableEnsured)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_table} (
                    id         TEXT PRIMARY KEY,
                    payload    TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    expires_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS {_table}_drain ON {_table} (expires_at, created_at, id);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _tableEnsured = true;
        }
        return conn;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
