using ZiggyCreatures.Caching.Fusion;

namespace YFex.Persistence;

/// <summary>
/// <see cref="ICache"/> backed by FusionCache — L1 (in-memory) with optional durable L2
/// (any <see cref="IKeyValueStore"/> via <see cref="KeyValueStoreDistributedCache"/>). Gives
/// stampede protection and fail-safe out of the box.
/// </summary>
/// <remarks>
/// FusionCache does not enumerate keys, so <see cref="GetKeysWithPrefixAsync"/> is unsupported
/// here — use <see cref="KeyValueCache"/> when prefix-based batch invalidation is required, or
/// FusionCache tagging directly.
/// </remarks>
public sealed class FusionCacheAdapter : ICache
{
    private readonly IFusionCache _cache;
    private readonly TimeSpan _defaultDuration;

    public FusionCacheAdapter(IFusionCache cache, TimeSpan? defaultDuration = null)
    {
        _cache = cache;
        _defaultDuration = defaultDuration ?? TimeSpan.FromMinutes(5);
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var maybe = await _cache.TryGetAsync<T>(key, token: ct).ConfigureAwait(false);
        return maybe.HasValue ? maybe.Value : default;
    }

    public async ValueTask<CacheResult<T>> TryGetAsync<T>(string key, CancellationToken ct = default)
    {
        var maybe = await _cache.TryGetAsync<T>(key, token: ct).ConfigureAwait(false);
        if (!maybe.HasValue) return CacheResult<T>.Missing;
        // FusionCache's MaybeValue does not surface fail-safe staleness or a timestamp on TryGet,
        // so a hit is reported as Fresh. Provenance can be enriched via FusionCache events later.
        return CacheValue<T>.Fresh(maybe.Value);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        => _cache.SetAsync(key, value, new FusionCacheEntryOptions { Duration = ttl ?? _defaultDuration }, token: ct);

    public ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default)
        => _cache.SetAsync(key, value, ToFusion(options), token: ct);

    public ValueTask InvalidateAsync(string key, CancellationToken ct = default)
        => _cache.RemoveAsync(key, token: ct);

    public async ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
    {
        var maybe = await _cache.TryGetAsync<T>(key, token: ct).ConfigureAwait(false);
        if (!maybe.HasValue) return;
        await _cache.SetAsync(key, mutator(maybe.Value), token: ct).ConfigureAwait(false);
    }

    /// <summary>Logically expires the entry (fail-safe keeps serving it until refreshed).</summary>
    public ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
        => _cache.ExpireAsync(key, token: ct);

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => throw new NotSupportedException(
            "FusionCacheAdapter cannot enumerate keys. Use KeyValueCache for prefix-based batch " +
            "invalidation, or FusionCache tagging (RemoveByTagAsync).");

    /// <summary>Maps backend-agnostic <see cref="CacheEntryOptions"/> onto FusionCache's per-entry options.</summary>
    private FusionCacheEntryOptions ToFusion(CacheEntryOptions o)
    {
        var options = new FusionCacheEntryOptions
        {
            Duration = o.Duration ?? _defaultDuration,
            IsFailSafeEnabled = o.IsFailSafeEnabled,
            Size = o.Size,
            Priority = o.Priority,
        };

        if (o.FailSafeMaxDuration is { } fsMax) options.FailSafeMaxDuration = fsMax;
        if (o.FailSafeThrottleDuration is { } fsThrottle) options.FailSafeThrottleDuration = fsThrottle;
        if (o.FactorySoftTimeout is { } soft) options.FactorySoftTimeout = soft;
        if (o.FactoryHardTimeout is { } hard) options.FactoryHardTimeout = hard;
        if (o.EagerRefreshThreshold is { } eager) options.EagerRefreshThreshold = eager;

        return options;
    }
}
