using System.Collections.Concurrent;

namespace YFex.Persistence;

/// <summary>
/// Volatile in-process <see cref="IKeyValueStore"/>. Data is lost on restart — for tests,
/// design-time, and as the default when no durable backend is registered. Folds the former
/// <c>MemorySnapshotStore</c> and <c>InMemoryClientStorage</c> into one implementation.
/// </summary>
public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly record struct Entry(byte[] Data, DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var e))
        {
            if (IsExpired(e))
            {
                _store.TryRemove(key, out _);
            }
            else
            {
                return new ValueTask<byte[]?>(e.Data);
            }
        }
        return new ValueTask<byte[]?>((byte[]?)null);
    }

    public ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : (DateTimeOffset?)null;
        _store[key] = new Entry(value, expiresAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var e) && !IsExpired(e))
            return new ValueTask<bool>(true);
        return new ValueTask<bool>(false);
    }

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        List<string> keys = [];
        foreach (var kvp in _store)
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal) && !IsExpired(kvp.Value))
                keys.Add(kvp.Key);
        return new ValueTask<IReadOnlyList<string>>(keys);
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        _store.Clear();
        return ValueTask.CompletedTask;
    }

    private static bool IsExpired(in Entry e)
        => e.ExpiresAt.HasValue && e.ExpiresAt.Value < DateTimeOffset.UtcNow;
}
