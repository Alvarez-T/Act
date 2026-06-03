using System.Text.Json;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Sqlite;

/// <summary>
/// <see cref="IClientCache"/> backed by <see cref="SqliteClientStorage"/>. Serializes typed
/// values with <c>System.Text.Json</c> into the shared <c>Cache</c> table.
/// </summary>
public sealed class SqliteClientCache : IClientCache
{
    private readonly SqliteClientStorage _storage;

    public SqliteClientCache(SqliteClientStorage storage) => _storage = storage;

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await _storage.GetAsync(key, ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        => await _storage.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), ttl, ct);

    public ValueTask InvalidateAsync(string key, CancellationToken ct = default)
        => _storage.DeleteAsync(key, ct);

    public async ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
    {
        var bytes = await _storage.GetAsync(key, ct);
        if (bytes is null) return;
        var current = JsonSerializer.Deserialize<T>(bytes);
        if (current is null) return;
        await _storage.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(mutator(current)), ct: ct);
    }

    public ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
        => _storage.MarkStaleAsync(key, ct);

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _storage.GetKeysWithPrefixAsync(prefix, ct);
}
