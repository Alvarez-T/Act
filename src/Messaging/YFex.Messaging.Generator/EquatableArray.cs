using System;
using System.Collections;
using System.Collections.Generic;

namespace YFex.Messaging.Generator;

/// <summary>
/// Structural-equality wrapper around a plain array.
/// Required for Roslyn incremental pipeline: T[] and ImmutableArray use reference equality
/// which would force unnecessary re-generation on every keystroke.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    public EquatableArray(T[]? array) => _array = array;

    public static EquatableArray<T> Empty { get; } = new(System.Array.Empty<T>());

    public bool IsEmpty => _array is null || _array.Length == 0;

    public int Count => _array?.Length ?? 0;

    public T this[int i] => _array![i];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null && other._array is null) return true;
        if (_array is null || other._array is null) return false;
        if (_array.Length != other._array.Length) return false;
        for (int i = 0; i < _array.Length; i++)
            if (!_array[i].Equals(other._array[i])) return false;
        return true;
    }

    public override bool Equals(object? obj)
        => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_array is null) return 0;
        unchecked
        {
            int h = 17;
            foreach (var item in _array)
                h = h * 31 + item.GetHashCode();
            return h;
        }
    }

    public IEnumerator<T> GetEnumerator()
        => ((IEnumerable<T>)(_array ?? System.Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
