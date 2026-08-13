using System.Runtime.CompilerServices;

namespace YFex.Persistence;

/// <summary>
/// A present cache value together with its provenance — either <b>fresh</b> (within TTL) or
/// <b>stale</b> (served past expiry, e.g. offline / fail-safe). Hand-written non-boxing
/// <c>[Union]</c> struct (same idiom as <c>YFex.Cqrs.QueueableResult</c>): the payload lives
/// inline, so typed access via <see cref="TryGetValue"/> / the implicit <typeparamref name="T"/>
/// conversion never allocates. The <see cref="IUnion.Value"/> accessor boxes only when used.
/// </summary>
[Union]
public readonly struct CacheValue<T> : IUnion
{
    private readonly T _value;
    private readonly DateTimeOffset? _lastModified;
    private readonly byte _tag; // 0 = none, 1 = fresh, 2 = stale

    private CacheValue(T value, byte tag, DateTimeOffset? lastModified)
    {
        _value = value;
        _tag = tag;
        _lastModified = lastModified;
    }

    /// <summary>A value that is within its TTL.</summary>
    public static CacheValue<T> Fresh(T value, DateTimeOffset? lastModified = null) => new(value, 1, lastModified);

    /// <summary>A value served past expiry (offline / fail-safe) — usable, but should be refreshed.</summary>
    public static CacheValue<T> Stale(T value, DateTimeOffset? lastModified = null) => new(value, 2, lastModified);

    public bool HasValue => _tag != 0;
    public bool IsFresh  => _tag == 1;
    public bool IsStale  => _tag == 2;

    /// <summary>When the entry was last written, when the backend can report it; otherwise null.</summary>
    public DateTimeOffset? LastModified => _lastModified;

    /// <summary>Box-free typed access to the underlying value.</summary>
    public T GetValue() => _value;

    /// <summary>Box-free typed access; always true for a constructed <see cref="CacheValue{T}"/>.</summary>
    public bool TryGetValue(out T value)
    {
        value = _value;
        return _tag != 0;
    }

    /// <summary>API-enforced exhaustive handling over the fresh/stale cases, box-free.</summary>
    public TResult Match<TResult>(Func<T, TResult> fresh, Func<T, TResult> stale)
        => _tag == 1 ? fresh(_value) : stale(_value);

    /// <summary>Unwrap directly to the value — use only where freshness is irrelevant.</summary>
    public static implicit operator T(CacheValue<T> value) => value._value;

    /// <summary>IUnion accessor — boxes only when accessed through the interface.</summary>
    public object? Value => _tag == 0 ? null : _value;
}

/// <summary>
/// Outcome of a cache lookup: a <b>hit</b> (carrying a <see cref="CacheValue{T}"/> with freshness
/// and provenance) or a <b>miss</b>. Composes <see cref="CacheValue{T}"/> as an <i>inline</i> field,
/// so the nested union adds no boxing. Typed access is allocation-free; the implicit
/// <typeparamref name="T"/> conversion yields the value on a hit and <c>default</c> on a miss.
/// </summary>
[Union]
public readonly struct CacheResult<T> : IUnion
{
    private readonly CacheValue<T> _hit; // inline struct — no boxing
    private readonly byte _tag;          // 0 = none, 1 = hit, 2 = miss

    public CacheResult(CacheValue<T> hit)
    {
        _hit = hit;
        _tag = 1;
    }

    private CacheResult(byte tag)
    {
        _hit = default;
        _tag = tag;
    }

    /// <summary>The shared miss singleton.</summary>
    public static readonly CacheResult<T> Missing = new(2);

    public bool HasValue => _tag != 0;
    public bool IsHit  => _tag == 1;
    public bool IsMiss => _tag == 2;

    /// <summary>True when the hit exists but is stale. False on a miss.</summary>
    public bool IsStale => _tag == 1 && _hit.IsStale;

    /// <summary>Box-free access to the value on a hit.</summary>
    public bool TryGetValue(out T value)
    {
        if (_tag == 1) { value = _hit.GetValue(); return true; }
        value = default!;
        return false;
    }

    /// <summary>Box-free access to the full hit (value + provenance).</summary>
    public bool TryGetHit(out CacheValue<T> hit)
    {
        hit = _hit;
        return _tag == 1;
    }

    /// <summary>API-enforced exhaustive handling over the hit/miss cases, box-free.</summary>
    public TResult Match<TResult>(Func<CacheValue<T>, TResult> hit, Func<TResult> miss)
        => _tag == 1 ? hit(_hit) : miss();

    /// <summary>A <see cref="CacheValue{T}"/> promotes to a hit.</summary>
    public static implicit operator CacheResult<T>(CacheValue<T> hit) => new(hit);

    /// <summary>Unwrap directly to the value — yields <c>default</c> on a miss. Use only where a miss
    /// is acceptable as a default; prefer <see cref="TryGetValue"/> otherwise.</summary>
    public static implicit operator T?(CacheResult<T> result) => result._tag == 1 ? result._hit.GetValue() : default;

    /// <summary>IUnion accessor — boxes only when accessed through the interface.</summary>
    public object? Value => _tag == 1 ? _hit.Value : null;
}
