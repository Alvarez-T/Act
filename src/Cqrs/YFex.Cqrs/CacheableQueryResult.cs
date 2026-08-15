using System.Runtime.CompilerServices;
using YFex;

namespace YFex.Cqrs;

/// <summary>
/// Outcome of a <b>cacheable</b> query dispatch, carrying provenance: a value that is
/// <see cref="Fresh{T}"/> (live/authoritative), <see cref="Cached{T}"/> (from local cache, still
/// valid), or <see cref="Stale{T}"/> (from cache past expiry — needs refresh), or an
/// <see cref="Error"/>. This is the third axis <see cref="Result{T}"/> cannot express: a success
/// carries <i>where it came from</i>, so the UI can badge offline/stale data and refresh on reconnect.
/// Only queries marked <see cref="ICacheable"/> can reach the cache cases; non-cacheable queries are
/// served as a plain <see cref="Result{T}"/> by the generated helpers.
/// </summary>
/// <remarks>
/// Hand-written non-boxing <c>[Union]</c> struct, same idiom as <see cref="Result{T}"/> /
/// <see cref="QueueableResult{T}"/>: one constructor per case type, a single inline payload keyed by
/// <c>_tag</c> (the three value cases share one <typeparamref name="T"/> slot — they differ only in
/// provenance, so no per-case field is wasted), no delegates. Typed access via <c>TryGet…</c> and
/// <see cref="OkValue"/> never allocates; <see cref="IUnion.Value"/> boxes only when used. Converts
/// implicitly to <see cref="Result{T}"/> so callers that only branch on success/error are unaffected.
/// </remarks>
[Union]
public readonly struct CacheableQueryResult<T> : IUnion, IResult
{
    private readonly T? _value;
    private readonly Error _error;
    private readonly DateTimeOffset? _asOf;
    private readonly byte _tag; // 0 = none, 1 = fresh, 2 = cached, 3 = stale, 4 = error

    public CacheableQueryResult(Fresh<T> value)  { _value = value.Value; _asOf = value.AsOf; _error = default; _tag = 1; }
    public CacheableQueryResult(Cached<T> value) { _value = value.Value; _asOf = value.AsOf; _error = default; _tag = 2; }
    public CacheableQueryResult(Stale<T> value)  { _value = value.Value; _asOf = value.AsOf; _error = default; _tag = 3; }
    public CacheableQueryResult(Error value)     { _value = default;     _asOf = default;    _error = value;   _tag = 4; }

    // IUnion — reconstructs the case struct, boxing only when accessed through this accessor.
    public object? Value => _tag switch
    {
        1 => new Fresh<T>(_value!, _asOf),
        2 => new Cached<T>(_value!, _asOf),
        3 => new Stale<T>(_value!, _asOf),
        4 => _error,
        _ => null,
    };

    public bool HasValue  => _tag != 0;
    /// <summary>True for any value case (fresh, cached, or stale).</summary>
    public bool IsOk      => _tag is 1 or 2 or 3;
    public bool IsError   => _tag == 4;
    public bool IsFresh   => _tag == 1;
    public bool IsCached  => _tag == 2;
    public bool IsStale   => _tag == 3;
    /// <summary>True when the value came from local cache (cached or stale), not a live fetch.</summary>
    public bool FromCache => _tag is 2 or 3;
    /// <summary>Typed value when <see cref="IsOk"/> is true; <see langword="default"/> otherwise.</summary>
    public T? OkValue     => IsOk ? _value : default;
    /// <summary>When the value was produced/stored, when the source reports it; otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? AsOf => _asOf;

    // ── Non-boxing typed access ───────────────────────────────────────────────
    public bool TryGetValue(out T? value)        { value = _value;                       return IsOk; }
    public bool TryGetError(out Error value)      { value = _error;                       return _tag == 4; }
    public bool TryGetFresh(out Fresh<T> value)   { value = new Fresh<T>(_value!, _asOf);  return _tag == 1; }
    public bool TryGetCached(out Cached<T> value) { value = new Cached<T>(_value!, _asOf); return _tag == 2; }
    public bool TryGetStale(out Stale<T> value)   { value = new Stale<T>(_value!, _asOf);  return _tag == 3; }

    // ── Construction from the case types ──────────────────────────────────────
    public static implicit operator CacheableQueryResult<T>(Fresh<T> value)  => new(value);
    public static implicit operator CacheableQueryResult<T>(Cached<T> value) => new(value);
    public static implicit operator CacheableQueryResult<T>(Stale<T> value)  => new(value);
    public static implicit operator CacheableQueryResult<T>(Error error)     => new(error);

    /// <summary>Collapse to <see cref="Result{T}"/>, dropping provenance (cached/stale become Ok) — for
    /// legacy callers and non-cacheable queries that only branch on success/error.</summary>
    public Result<T> ToResult() => IsOk ? Result<T>.Ok(_value!) : _error;
    public static implicit operator Result<T>(CacheableQueryResult<T> result) => result.ToResult();

    // ── Helpers (mirror Result<T>) ────────────────────────────────────────────
    public static CacheableQueryResult<T> Ok(T value) => new(new Fresh<T>(value));
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}
