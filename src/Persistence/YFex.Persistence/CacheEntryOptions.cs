using Microsoft.Extensions.Caching.Memory;

namespace YFex.Persistence;

/// <summary>
/// Backend-agnostic per-entry cache options. FusionCache honors all of them; simpler backends
/// (<see cref="InMemoryCache"/>, <see cref="KeyValueCache"/>) honor <see cref="Duration"/> and
/// ignore the rest as best-effort.
/// </summary>
public sealed record CacheEntryOptions
{
    /// <summary>Hard time-to-live for the entry.</summary>
    public TimeSpan? Duration { get; init; }

    // ── Fail-safe: keep serving the expired value while a refresh runs / after it fails ──
    public bool IsFailSafeEnabled { get; init; }
    public TimeSpan? FailSafeMaxDuration { get; init; }
    public TimeSpan? FailSafeThrottleDuration { get; init; }

    // ── Factory timeouts: return stale fast, finish the refresh in the background ──
    public TimeSpan? FactorySoftTimeout { get; init; }
    public TimeSpan? FactoryHardTimeout { get; init; }

    /// <summary>Proactively refresh once this fraction (0..1) of <see cref="Duration"/> has elapsed.</summary>
    public float? EagerRefreshThreshold { get; init; }

    // ── L1 memory bound (#7): counted against the memory cache SizeLimit; evicted by Priority ──
    /// <summary>Relative size of this entry, charged against the L1 memory bound. Default 1.</summary>
    public long Size { get; init; } = 1;

    /// <summary>Eviction priority when L1 is under memory pressure.</summary>
    public CacheItemPriority Priority { get; init; } = CacheItemPriority.Normal;

    /// <summary>
    /// Tags attached to the entry for group invalidation via <see cref="ICache.RemoveByTagAsync"/> /
    /// <see cref="ICache.ExpireByTagAsync"/>. FusionCache invalidates by tag natively; other backends
    /// store tags per entry and enumerate.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}
