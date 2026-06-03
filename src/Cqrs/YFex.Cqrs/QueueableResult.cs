using System.Runtime.CompilerServices;

namespace YFex.Cqrs;

/// <summary>Discriminated union for a command that may succeed, fail, or be queued offline.</summary>
[Union]
public readonly struct QueueableResult : IUnion, IResult
{
    private readonly Success _success;
    private readonly Error _error;
    private readonly Queued _queued;
    private readonly byte _tag; // 0=none, 1=success, 2=error, 3=queued

    public QueueableResult(Success value) { _success = value; _tag = 1; }
    public QueueableResult(Error value)   { _error   = value; _tag = 2; }
    public QueueableResult(Queued value)  { _queued  = value; _tag = 3; }

    public object? Value => _tag switch { 1 => _success, 2 => _error, 3 => _queued, _ => null };
    public bool HasValue  => _tag != 0;
    public bool IsSuccess => _tag == 1;
    public bool IsOk      => _tag == 1;
    public bool IsError   => _tag == 2;
    public bool IsQueued  => _tag == 3;

    public bool TryGetSuccess(out Success value) { value = _success; return _tag == 1; }
    public bool TryGetError(out Error value)     { value = _error;   return _tag == 2; }
    public bool TryGetQueued(out Queued value)   { value = _queued;  return _tag == 3; }

    public static implicit operator QueueableResult(Success s) => new(s);
    public static implicit operator QueueableResult(Error e)   => new(e);
    public static implicit operator QueueableResult(Queued q)  => new(q);

    public static QueueableResult Ok()    => new Success();
    public static QueueableResult Queue(Guid key) => new Queued(key);
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
}

/// <summary>Discriminated union for a result-bearing command that may succeed with a value, fail, or be queued offline.</summary>
[Union]
public readonly struct QueueableResult<T> : IUnion, IResult
{
    private readonly T? _value;
    private readonly Error _error;
    private readonly Queued _queued;
    private readonly byte _tag;

    public QueueableResult(T value)      { ArgumentNullException.ThrowIfNull(value); _value  = value; _tag = 1; }
    public QueueableResult(Error value)  { _error  = value; _tag = 2; }
    public QueueableResult(Queued value) { _queued = value; _tag = 3; }

    public object? Value => _tag switch { 1 => _value, 2 => _error, 3 => _queued, _ => null };
    public bool HasValue  => _tag != 0;
    public bool IsSuccess => _tag == 1;
    public bool IsOk      => _tag == 1;
    public bool IsError   => _tag == 2;
    public bool IsQueued  => _tag == 3;
    /// <summary>Typed value when <see cref="IsOk"/> is true; default otherwise.</summary>
    public T? OkValue     => _tag == 1 ? _value : default;

    public bool TryGetValue(out T? value)      { value = _value;  return _tag == 1; }
    public bool TryGetError(out Error value)   { value = _error;  return _tag == 2; }
    public bool TryGetQueued(out Queued value) { value = _queued; return _tag == 3; }

    public static implicit operator QueueableResult<T>(T value)   => new(value);
    public static implicit operator QueueableResult<T>(Error e)   => new(e);
    public static implicit operator QueueableResult<T>(Queued q)  => new(q);

    public static QueueableResult<T> Ok(T value) => new(value);
    public static QueueableResult<T> Queue(Guid key) => new(new Queued(key));
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);

    /// <summary>Unwraps to <see cref="Result{T}"/>, collapsing the queued path (never occurs for non-queueable commands).</summary>
    public Result<T> ToResult() => _tag switch
    {
        1 when _value is not null => Result<T>.Ok(_value),
        2 => _error,
        _ => Result<T>.Fail("Command was queued unexpectedly.")
    };
}

[Union]
public readonly struct QueueableResult<T0, T1> : IUnion, IResult
{
    private readonly T0? _v0;
    private readonly T1? _v1;
    private readonly Error _error;
    private readonly Queued _queued;
    private readonly byte _tag;

    public QueueableResult(T0 v)     { ArgumentNullException.ThrowIfNull(v); _v0    = v;     _tag = 1; }
    public QueueableResult(T1 v)     { ArgumentNullException.ThrowIfNull(v); _v1    = v;     _tag = 2; }
    public QueueableResult(Error e)  { _error  = e; _tag = 3; }
    public QueueableResult(Queued q) { _queued = q; _tag = 4; }

    public object? Value => _tag switch { 1 => _v0, 2 => _v1, 3 => _error, 4 => _queued, _ => null };
    public bool HasValue  => _tag != 0;
    public bool IsSuccess => _tag is 1 or 2;
    public bool IsError   => _tag == 3;
    public bool IsQueued  => _tag == 4;

    public bool TryGetValue(out T0? v)     { v = _v0;    return _tag == 1; }
    public bool TryGetValue(out T1? v)     { v = _v1;    return _tag == 2; }
    public bool TryGetError(out Error v)   { v = _error; return _tag == 3; }
    public bool TryGetQueued(out Queued v) { v = _queued; return _tag == 4; }

    public static implicit operator QueueableResult<T0, T1>(T0 v)    => new(v);
    public static implicit operator QueueableResult<T0, T1>(T1 v)    => new(v);
    public static implicit operator QueueableResult<T0, T1>(Error e) => new(e);
    public static implicit operator QueueableResult<T0, T1>(Queued q)=> new(q);
}

[Union]
public readonly struct QueueableResult<T0, T1, T2> : IUnion, IResult
{
    private readonly T0? _v0;
    private readonly T1? _v1;
    private readonly T2? _v2;
    private readonly Error _error;
    private readonly Queued _queued;
    private readonly byte _tag;

    public QueueableResult(T0 v)     { ArgumentNullException.ThrowIfNull(v); _v0    = v; _tag = 1; }
    public QueueableResult(T1 v)     { ArgumentNullException.ThrowIfNull(v); _v1    = v; _tag = 2; }
    public QueueableResult(T2 v)     { ArgumentNullException.ThrowIfNull(v); _v2    = v; _tag = 3; }
    public QueueableResult(Error e)  { _error  = e; _tag = 4; }
    public QueueableResult(Queued q) { _queued = q; _tag = 5; }

    public object? Value => _tag switch { 1=>_v0, 2=>_v1, 3=>_v2, 4=>_error, 5=>_queued, _=>null };
    public bool HasValue  => _tag != 0;
    public bool IsError   => _tag == 4;
    public bool IsQueued  => _tag == 5;

    public bool TryGetValue(out T0? v)     { v = _v0;    return _tag == 1; }
    public bool TryGetValue(out T1? v)     { v = _v1;    return _tag == 2; }
    public bool TryGetValue(out T2? v)     { v = _v2;    return _tag == 3; }
    public bool TryGetError(out Error v)   { v = _error; return _tag == 4; }
    public bool TryGetQueued(out Queued v) { v = _queued; return _tag == 5; }

    public static implicit operator QueueableResult<T0,T1,T2>(T0 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2>(T1 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2>(T2 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2>(Error e) => new(e);
    public static implicit operator QueueableResult<T0,T1,T2>(Queued q)=> new(q);
}

[Union]
public readonly struct QueueableResult<T0, T1, T2, T3> : IUnion, IResult
{
    private readonly T0? _v0;
    private readonly T1? _v1;
    private readonly T2? _v2;
    private readonly T3? _v3;
    private readonly Error _error;
    private readonly Queued _queued;
    private readonly byte _tag;

    public QueueableResult(T0 v)     { ArgumentNullException.ThrowIfNull(v); _v0    = v; _tag = 1; }
    public QueueableResult(T1 v)     { ArgumentNullException.ThrowIfNull(v); _v1    = v; _tag = 2; }
    public QueueableResult(T2 v)     { ArgumentNullException.ThrowIfNull(v); _v2    = v; _tag = 3; }
    public QueueableResult(T3 v)     { ArgumentNullException.ThrowIfNull(v); _v3    = v; _tag = 4; }
    public QueueableResult(Error e)  { _error  = e; _tag = 5; }
    public QueueableResult(Queued q) { _queued = q; _tag = 6; }

    public object? Value => _tag switch { 1=>_v0, 2=>_v1, 3=>_v2, 4=>_v3, 5=>_error, 6=>_queued, _=>null };
    public bool HasValue  => _tag != 0;
    public bool IsError   => _tag == 5;
    public bool IsQueued  => _tag == 6;

    public bool TryGetValue(out T0? v)     { v = _v0;    return _tag == 1; }
    public bool TryGetValue(out T1? v)     { v = _v1;    return _tag == 2; }
    public bool TryGetValue(out T2? v)     { v = _v2;    return _tag == 3; }
    public bool TryGetValue(out T3? v)     { v = _v3;    return _tag == 4; }
    public bool TryGetError(out Error v)   { v = _error; return _tag == 5; }
    public bool TryGetQueued(out Queued v) { v = _queued; return _tag == 6; }

    public static implicit operator QueueableResult<T0,T1,T2,T3>(T0 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2,T3>(T1 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2,T3>(T2 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2,T3>(T3 v)    => new(v);
    public static implicit operator QueueableResult<T0,T1,T2,T3>(Error e) => new(e);
    public static implicit operator QueueableResult<T0,T1,T2,T3>(Queued q)=> new(q);
}
