namespace YFex.Persistence;

/// <summary>
/// Typed cache with optional durability and offline semantics. Unlike a durable store, a
/// cache entry may be evicted or served stale. Backed by an <see cref="IKeyValueStore"/> (so
/// it works over memory or SQLite) via <see cref="KeyValueCache"/>, or by FusionCache via
/// <c>FusionCacheAdapter</c>. Replaces the former messaging <c>IClientCache</c>.
/// </summary>
public interface ICache
{
    /// <summary>Returns the cached value, or <see langword="default"/> if absent/expired.</summary>
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>
    /// Reads the entry with its provenance: a hit (fresh or stale, with an optional last-modified
    /// timestamp) or a miss. Prefer this over <see cref="GetAsync{T}"/> when the caller needs to
    /// distinguish "missing" from "present but stale" (e.g. offline UX).
    /// </summary>
    ValueTask<CacheResult<T>> TryGetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>Stores <paramref name="value"/>, optionally expiring after <paramref name="ttl"/>.</summary>
    ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>Stores <paramref name="value"/> with full <see cref="CacheEntryOptions"/> (fail-safe,
    /// timeouts, L1 size/priority). Simpler backends honor <see cref="CacheEntryOptions.Duration"/> only.</summary>
    ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default);

    /// <summary>Removes the entry for <paramref name="key"/>.</summary>
    ValueTask InvalidateAsync(string key, CancellationToken ct = default);

    /// <summary>Atomically reads, applies <paramref name="mutator"/>, and writes back (optimistic update).</summary>
    ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default);

    /// <summary>Marks an entry stale without removing it — still served offline, refreshed on reconnect.</summary>
    ValueTask MarkStaleAsync(string key, CancellationToken ct = default);

    /// <summary>Returns cache keys beginning with <paramref name="prefix"/> (for batch invalidation).</summary>
    ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default);
}
