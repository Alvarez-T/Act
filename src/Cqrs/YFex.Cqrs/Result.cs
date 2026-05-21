using System;
using System.Runtime.CompilerServices;
using YFex.Cqrs;

namespace YFex.Cqrs;

public record struct Success;

// Non-generic Result with non-boxing pattern
public interface IResult
{
}

[Union]
public readonly struct Result : IUnion, IResult
{
    private readonly Success _success;
    private readonly Error _error;
    private readonly byte _tag; // 0 = none, 1 = success, 2 = error

    public Result(Success value)
    {
        _success = value;
        _tag = 1;
    }

    public Result(Error value)
    {
        _error = value;
        _tag = 2;
    }

    // IUnion implementation - only boxes when accessed through this property
    public object? Value => _tag switch
    {
        1 => _success,
        2 => _error,
        _ => null
    };

    // Non-boxing access pattern
    public bool HasValue => _tag != 0;

    public bool TryGetValue(out Success value)
    {
        value = _success;
        return _tag == 1;
    }

    public bool TryGetValue(out Error value)
    {
        value = _error;
        return _tag == 2;
    }

    // Implicit conversions
    public static implicit operator Result(Success success) => new(success);
    public static implicit operator Result(Error error) => new(error);

    // Static helper methods
    public static Result Ok() => new Success();
    public static Result Success() => new Success();

    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}

// Generic Result<T> with non-boxing pattern
[Union]
public readonly struct Result<T> : IUnion, IResult
{
    private readonly T? _value;
    private readonly Error _error;
    private readonly byte _tag; // 0 = none, 1 = value, 2 = error

    public Result(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        _tag = 1;
    }

    public Result(Error value)
    {
        _error = value;
        _tag = 2;
    }

    // IUnion implementation - only boxes when accessed
    public object? Value => _tag switch
    {
        1 => _value,
        2 => _error,
        _ => null
    };

    // Non-boxing access pattern
    public bool HasValue => _tag != 0;

    public bool TryGetValue(out T? value)
    {
        value = _value;
        return _tag == 1;
    }

    public bool TryGetValue(out Error value)
    {
        value = _error;
        return _tag == 2;
    }

    // Implicit conversions
    public static implicit operator Result<T>(T value) => new(value);
    public static implicit operator Result<T>(Error error) => new(error);

    // Static helper methods
    public static Result<T> Ok(T value) => value;

    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}

// Result<T0, T1> with non-boxing pattern
[Union]
public readonly struct Result<T0, T1> : IUnion, IResult
{
    private readonly T0? _value0;
    private readonly T1? _value1;
    private readonly Error _error;
    private readonly byte _tag; // 0 = none, 1 = T0, 2 = T1, 3 = error

    public Result(T0 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value0 = value;
        _tag = 1;
    }

    public Result(T1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value1 = value;
        _tag = 2;
    }

    public Result(Error value)
    {
        _error = value;
        _tag = 3;
    }

    public object? Value => _tag switch
    {
        1 => _value0,
        2 => _value1,
        3 => _error,
        _ => null
    };

    // Non-boxing access pattern
    public bool HasValue => _tag != 0;

    public bool TryGetValue(out T0? value)
    {
        value = _value0;
        return _tag == 1;
    }

    public bool TryGetValue(out T1? value)
    {
        value = _value1;
        return _tag == 2;
    }

    public bool TryGetValue(out Error value)
    {
        value = _error;
        return _tag == 3;
    }

    // Implicit conversions
    public static implicit operator Result<T0, T1>(T0 value) => new(value);
    public static implicit operator Result<T0, T1>(T1 value) => new(value);
    public static implicit operator Result<T0, T1>(Error error) => new(error);

    // Static helpers
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}

// Result<T0, T1, T2> with non-boxing pattern
[Union]
public readonly struct Result<T0, T1, T2> : IUnion, IResult
{
    private readonly T0? _value0;
    private readonly T1? _value1;
    private readonly T2? _value2;
    private readonly Error _error;
    private readonly byte _tag; // 0 = none, 1 = T0, 2 = T1, 3 = T2, 4 = error

    public Result(T0 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value0 = value;
        _tag = 1;
    }

    public Result(T1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value1 = value;
        _tag = 2;
    }

    public Result(T2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value2 = value;
        _tag = 3;
    }

    public Result(Error value)
    {
        _error = value;
        _tag = 4;
    }

    public object? Value => _tag switch
    {
        1 => _value0,
        2 => _value1,
        3 => _value2,
        4 => _error,
        _ => null
    };

    // Non-boxing access pattern
    public bool HasValue => _tag != 0;

    public bool TryGetValue(out T0? value)
    {
        value = _value0;
        return _tag == 1;
    }

    public bool TryGetValue(out T1? value)
    {
        value = _value1;
        return _tag == 2;
    }

    public bool TryGetValue(out T2? value)
    {
        value = _value2;
        return _tag == 3;
    }

    public bool TryGetValue(out Error value)
    {
        value = _error;
        return _tag == 4;
    }

    // Implicit conversions
    public static implicit operator Result<T0, T1, T2>(T0 value) => new(value);
    public static implicit operator Result<T0, T1, T2>(T1 value) => new(value);
    public static implicit operator Result<T0, T1, T2>(T2 value) => new(value);
    public static implicit operator Result<T0, T1, T2>(Error error) => new(error);

    // Static helpers
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}

// Result<T0, T1, T2, T3> with non-boxing pattern
[Union]
public readonly struct Result<T0, T1, T2, T3> : IUnion, IResult
{
    private readonly T0? _value0;
    private readonly T1? _value1;
    private readonly T2? _value2;
    private readonly T3? _value3;
    private readonly Error _error;
    private readonly byte _tag; // 0 = none, 1 = T0, 2 = T1, 3 = T2, 4 = T3, 5 = error

    public Result(T0 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value0 = value;
        _tag = 1;
    }

    public Result(T1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value1 = value;
        _tag = 2;
    }

    public Result(T2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value2 = value;
        _tag = 3;
    }

    public Result(T3 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value3 = value;
        _tag = 4;
    }

    public Result(Error value)
    {
        _error = value;
        _tag = 5;
    }

    public object? Value => _tag switch
    {
        1 => _value0,
        2 => _value1,
        3 => _value2,
        4 => _value3,
        5 => _error,
        _ => null
    };

    // Non-boxing access pattern
    public bool HasValue => _tag != 0;

    public bool TryGetValue(out T0? value)
    {
        value = _value0;
        return _tag == 1;
    }

    public bool TryGetValue(out T1? value)
    {
        value = _value1;
        return _tag == 2;
    }

    public bool TryGetValue(out T2? value)
    {
        value = _value2;
        return _tag == 3;
    }

    public bool TryGetValue(out T3? value)
    {
        value = _value3;
        return _tag == 4;
    }

    public bool TryGetValue(out Error value)
    {
        value = _error;
        return _tag == 5;
    }

    // Implicit conversions
    public static implicit operator Result<T0, T1, T2, T3>(T0 value) => new(value);
    public static implicit operator Result<T0, T1, T2, T3>(T1 value) => new(value);
    public static implicit operator Result<T0, T1, T2, T3>(T2 value) => new(value);
    public static implicit operator Result<T0, T1, T2, T3>(T3 value) => new(value);
    public static implicit operator Result<T0, T1, T2, T3>(Error error) => new(error);

    // Static helpers
    public static Error Fail(string message = "") => new(ErrorType.Fail, message);
    public static Error NotFound(string message = "") => new(ErrorType.NotFound, message);
    public static Error Unauthorized(string message = "") => new(ErrorType.Unauthorized, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error ValidationProblem(string message) => new(ErrorType.ValidationProblem, message);
}