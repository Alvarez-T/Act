namespace YFex.Persistence;

/// <summary>
/// Volatile in-process <see cref="ICache"/> that stores typed objects directly (no
/// serialization) — the default cache when no durable backend is configured. Folds the former
/// <c>InMemoryClientCache</c>. For durability use <see cref="KeyValueCache"/> or
/// <c>FusionCacheAdapter</c>.
/// </summary>
public sealed class InMemoryCache : ICache
{
    private sealed record CacheEntry(object? Value, bool IsStale, DateTimeOffset? ExpiresAt, DateTimeOffset StoredAt);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var e)) return new ValueTask<T?>(default(T?));
            if (e.ExpiresAt.HasValue && e.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _cache.Remove(key);
                return new ValueTask<T?>(default(T?));
            }
            return new ValueTask<T?>(e.Value is T t ? t : default(T?));
        }
    }

    public ValueTask<CacheResult<T>> TryGetAsync<T>(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var e))
                return new ValueTask<CacheResult<T>>(CacheResult<T>.Missing);
            if (e.ExpiresAt.HasValue && e.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                _cache.Remove(key);
                return new ValueTask<CacheResult<T>>(CacheResult<T>.Missing);
            }
            if (e.Value is not T t)
                return new ValueTask<CacheResult<T>>(CacheResult<T>.Missing);

            var value = e.IsStale ? CacheValue<T>.Stale(t, e.StoredAt) : CacheValue<T>.Fresh(t, e.StoredAt);
            return new ValueTask<CacheResult<T>>(value);
        }
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : (DateTimeOffset?)null;
        lock (_lock) _cache[key] = new CacheEntry(value, false, expiresAt, DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

    /// <summary>Honors <see cref="CacheEntryOptions.Duration"/>; other options are ignored (single-process, unbounded).</summary>
    public ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default)
        => SetAsync(key, value, options.Duration, ct);

    public ValueTask InvalidateAsync(string key, CancellationToken ct = default)
    {
        lock (_lock) _cache.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var e) && e.Value is T current)
                _cache[key] = e with { Value = mutator(current) };
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var e))
                _cache[key] = e with { IsStale = true };
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        lock (_lock)
        {
            List<string> keys = [];
            foreach (var k in _cache.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    keys.Add(k);
            return new ValueTask<IReadOnlyList<string>>(keys);
        }
    }

    /// <summary>Returns true if the entry exists and has been marked stale.</summary>
    public bool IsStale(string key)
    {
        lock (_lock) return _cache.TryGetValue(key, out var e) && e.IsStale;
    }
}
