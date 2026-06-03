namespace YFex.Messaging.Rpc;

/// <summary>
/// Low-level byte-array persistence backing the client cache and offline outbox.
/// Plan 4 ships SQLite and IndexedDB backends; this plan provides <see cref="InMemoryClientStorage"/>.
/// </summary>
public interface IClientStorage
{
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default);
    ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default);
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default);
    ValueTask ClearAsync(CancellationToken ct = default);
}

/// <summary>Volatile, in-process <see cref="IClientStorage"/> for tests and ephemeral use.</summary>
public sealed class InMemoryClientStorage : IClientStorage
{
    private readonly record struct Entry(byte[] Data, DateTimeOffset? ExpiresAt);

    private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(key, out var e)) return new((byte[]?)null);
            if (e.ExpiresAt.HasValue && e.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _store.Remove(key);
                return new((byte[]?)null);
            }
            return new(e.Data);
        }
    }

    public ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : (DateTimeOffset?)null;
        lock (_lock) _store[key] = new(value, expiresAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_lock) _store.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        lock (_lock)
        {
            List<string> keys = [];
            foreach (var k in _store.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    keys.Add(k);
            return new(keys);
        }
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        lock (_lock) _store.Clear();
        return ValueTask.CompletedTask;
    }
}
