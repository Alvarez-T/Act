using Microsoft.Data.Sqlite;
using YFex.Persistence;

namespace YFex.Persistence.Sqlite;

/// <summary>
/// <see cref="IKeyValueStore"/> over a SQLite table (<c>key</c>, <c>value</c> BLOB,
/// <c>expires_at</c>). Folds the former <c>SqliteClientStorage</c>. Connection-per-operation
/// over a WAL database; writes are serialized by a process-level lock to avoid SQLITE_BUSY.
/// </summary>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly string _table;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _tableEnsured;

    /// <param name="table">Table name. Must be a trusted constant (interpolated into DDL/DML).</param>
    public SqliteKeyValueStore(SqliteConnectionFactory factory, string table = "kv_store")
    {
        _factory = factory;
        _table = table;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT value, expires_at FROM {_table} WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        if (!reader.IsDBNull(1) && reader.GetInt64(1) < Now())
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await DeleteAsync(key, ct).ConfigureAwait(false);
            return null;
        }
        return (byte[])reader.GetValue(0);
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        long? expiresAt = ttl.HasValue ? Now() + (long)ttl.Value.TotalMilliseconds : null;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {_table} (key, value, expires_at) VALUES ($key, $value, $expires)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, expires_at = excluded.expires_at
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$expires", expiresAt.HasValue ? expiresAt.Value : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_table} WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await GetAsync(key, ct).ConfigureAwait(false) is not null;

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT key FROM {_table} WHERE key LIKE $prefix ESCAPE '\\' AND (expires_at IS NULL OR expires_at > $now)";
        cmd.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
        cmd.Parameters.AddWithValue("$now", Now());

        var keys = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            keys.Add(reader.GetString(0));
        return keys;
    }

    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_table}";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        if (!_tableEnsured)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_table} (
                    key        TEXT PRIMARY KEY,
                    value      BLOB NOT NULL,
                    expires_at INTEGER
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _tableEnsured = true;
        }
        return conn;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string EscapeLike(string v) =>
        v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
