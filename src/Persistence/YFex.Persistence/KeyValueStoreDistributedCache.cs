using Microsoft.Extensions.Caching.Distributed;

namespace YFex.Persistence;

/// <summary>
/// Adapts an <see cref="IKeyValueStore"/> to <see cref="IDistributedCache"/> so it can serve
/// as FusionCache's durable L2 (memory, SQLite, …). Sliding expiration is not supported by the
/// underlying store and is ignored; absolute/relative TTL maps to the store's TTL.
/// </summary>
public sealed class KeyValueStoreDistributedCache : IDistributedCache
{
    private readonly IKeyValueStore _store;

    public KeyValueStoreDistributedCache(IKeyValueStore store) => _store = store;

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        => await _store.GetAsync(key, token).ConfigureAwait(false);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => await _store.SetAsync(key, value, ResolveTtl(options), token).ConfigureAwait(false);

    public void Refresh(string key) { /* sliding expiration unsupported */ }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
        => await _store.DeleteAsync(key, token).ConfigureAwait(false);

    private static TimeSpan? ResolveTtl(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpirationRelativeToNow is { } rel) return rel;
        if (options.AbsoluteExpiration is { } abs)
        {
            var delta = abs - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }
}
