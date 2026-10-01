namespace YFex.Persistence.Tenancy;

/// <summary>
/// <see cref="ICache"/> decorator that partitions keys <b>and tags</b> by the scope in effect, so a
/// tag-based eviction in one tenant never evicts another tenant's entries.
/// </summary>
public sealed class ScopedCache : ICache
{
    private readonly ICache _inner;
    private readonly PersistenceKeyScoper _scoper;

    public ScopedCache(ICache inner, PersistenceKeyScoper scoper)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _scoper = scoper ?? throw new ArgumentNullException(nameof(scoper));
    }

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
        => _inner.GetAsync<T>(_scoper.Map(key), ct);

    public ValueTask<CacheResult<T>> TryGetAsync<T>(string key, CancellationToken ct = default)
        => _inner.TryGetAsync<T>(_scoper.Map(key), ct);

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        => _inner.SetAsync(_scoper.Map(key), value, ttl, ct);

    public ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default)
    {
        var partition = _scoper.PrefixFor(key);
        return _inner.SetAsync(partition + key, value, ScopeTags(options, partition)!, ct);
    }

    public ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken ct = default)
    {
        var partition = _scoper.PrefixFor(key);
        return _inner.GetOrSetAsync(partition + key, factory, ScopeTags(options, partition), ct);
    }

    public ValueTask InvalidateAsync(string key, CancellationToken ct = default)
        => _inner.InvalidateAsync(_scoper.Map(key), ct);

    public ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
        => _inner.UpdateAsync(_scoper.Map(key), mutator, ct);

    public ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
        => _inner.MarkStaleAsync(_scoper.Map(key), ct);

    public ValueTask RemoveByTagAsync(string tag, CancellationToken ct = default)
        => _inner.RemoveByTagAsync(_scoper.Map(tag), ct);

    public ValueTask ExpireByTagAsync(string tag, CancellationToken ct = default)
        => _inner.ExpireByTagAsync(_scoper.Map(tag), ct);

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var partition = _scoper.PrefixFor(prefix);
        var physical = await _inner.GetKeysWithPrefixAsync(partition + prefix, ct).ConfigureAwait(false);
        return _scoper.Unmap(physical, partition);
    }

    // Tags follow the key's partition so RemoveByTag/ExpireByTag stay inside the tenant.
    private static CacheEntryOptions? ScopeTags(CacheEntryOptions? options, string partition)
    {
        if (options?.Tags is not { Count: > 0 } tags || partition.Length == 0) return options;
        var scoped = new string[tags.Count];
        for (var i = 0; i < tags.Count; i++) scoped[i] = partition + tags[i];
        return options with { Tags = scoped };
    }
}
