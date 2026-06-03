using Microsoft.Data.Sqlite;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Sqlite;

/// <summary>
/// <see cref="IClientStorage"/> backed by the <c>Cache</c> SQLite table.
/// Exposes <c>MarkStaleAsync</c>/<c>IsStaleAsync</c> for use by <see cref="SqliteClientCache"/>.
/// </summary>
public sealed class SqliteClientStorage : IClientStorage, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteClientStorage(SqliteConnectionFactory factory) => _factory = factory;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = "SELECT Value, ExpiresAt FROM Cache WHERE Key = $key";
        cmd.Parameters.AddWithValue("$key", key);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        if (!reader.IsDBNull(1))
        {
            var expiresAt = reader.GetInt64(1);
            if (expiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                reader.Close();
                await DeleteAsync(key, ct);
                return null;
            }
        }

        return (byte[])reader.GetValue(0);
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = ttl.HasValue ? (object)(now + (long)ttl.Value.TotalMilliseconds) : DBNull.Value;

        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Cache (Key, Value, IsStale, WrittenAt, ExpiresAt)
                VALUES ($key, $value, 0, $writtenAt, $expiresAt)
                ON CONFLICT(Key) DO UPDATE SET
                    Value = excluded.Value,
                    IsStale = 0,
                    WrittenAt = excluded.WrittenAt,
                    ExpiresAt = excluded.ExpiresAt;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$writtenAt", now);
            cmd.Parameters.AddWithValue("$expiresAt", expiresAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Cache WHERE Key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = "SELECT Key FROM Cache WHERE Key LIKE $prefix ESCAPE '\\'";
        cmd.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");

        var keys = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Cache";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    internal async ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _factory.Connection.CreateCommand();
            cmd.CommandText = "UPDATE Cache SET IsStale = 1 WHERE Key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    internal async ValueTask<bool> IsStaleAsync(string key, CancellationToken ct = default)
    {
        await _factory.EnsureInitializedAsync(ct);
        using var cmd = _factory.Connection.CreateCommand();
        cmd.CommandText = "SELECT IsStale FROM Cache WHERE Key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long v && v == 1L;
    }

    private static string EscapeLike(string v) =>
        v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
