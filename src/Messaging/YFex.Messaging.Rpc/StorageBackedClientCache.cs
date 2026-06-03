using System.Text.Json;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Generic <see cref="IClientCache"/> that wraps any <see cref="IClientStorage"/> backend,
/// serializing typed values with <c>System.Text.Json</c>. Used by IndexedDB and any future
/// custom backend that only implements raw byte storage.
/// </summary>
public sealed class StorageBackedClientCache : IClientCache
{
    private readonly IClientStorage _storage;

    // Stale keys are tracked in memory alongside storage — stale state is advisory only
    // and does not need to survive process restart.
    private readonly HashSet<string> _staleKeys = new(StringComparer.Ordinal);
    private readonly object _staleLock = new();

    public StorageBackedClientCache(IClientStorage storage) => _storage = storage;

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await _storage.GetAsync(key, ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        lock (_staleLock) _staleKeys.Remove(key);
        await _storage.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), ttl, ct);
    }

    public async ValueTask InvalidateAsync(string key, CancellationToken ct = default)
    {
        lock (_staleLock) _staleKeys.Remove(key);
        await _storage.DeleteAsync(key, ct);
    }

    public async ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
    {
        var bytes = await _storage.GetAsync(key, ct);
        if (bytes is null) return;
        var current = JsonSerializer.Deserialize<T>(bytes);
        if (current is null) return;
        await _storage.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(mutator(current)), ct: ct);
    }

    public ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
    {
        lock (_staleLock) _staleKeys.Add(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _storage.GetKeysWithPrefixAsync(prefix, ct);
}
