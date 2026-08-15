namespace YFex;

/// <summary>
/// Provenance primitives — the standalone case types that value-carrying unions compose
/// (e.g. <c>YFex.Cqrs.CacheableQueryResult&lt;T&gt;</c>, <c>YFex.Persistence.CacheResult&lt;T&gt;</c>).
/// Each is a payload-only <see langword="readonly record struct"/>: no delegates, no lists,
/// stack-allocated, and non-boxing when consumed through a hand-written union's typed accessors.
/// </summary>
/// <remarks>
/// The three value states are distinguished by <i>where</i> the value came from, not just whether
/// it exists: <see cref="Fresh{T}"/> is live/authoritative, <see cref="Cached{T}"/> is a still-valid
/// local copy, and <see cref="Stale{T}"/> is a local copy past expiry that should be refreshed.
/// </remarks>

/// <summary>A value obtained live and authoritative (online, within TTL).</summary>
public readonly record struct Fresh<T>(T Value, DateTimeOffset? AsOf = null);

/// <summary>A value served from local cache while still valid (e.g. offline, within TTL) — current, no refresh needed.</summary>
public readonly record struct Cached<T>(T Value, DateTimeOffset? AsOf = null);

/// <summary>A value served from local cache past expiry / after invalidation — usable, but should be refreshed.</summary>
public readonly record struct Stale<T>(T Value, DateTimeOffset? AsOf = null);

/// <summary>Absence of a value. Non-generic, payload-free — the empty case for lookup unions. Never boxes.</summary>
public readonly record struct Miss
{
    /// <summary>The shared miss instance.</summary>
    public static readonly Miss Value = default;
}
