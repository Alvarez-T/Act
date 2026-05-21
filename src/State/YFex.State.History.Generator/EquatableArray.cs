using System;
using System.Collections;
using System.Collections.Generic;

namespace YFex.State.History.Generator;

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    public EquatableArray(T[]? array) => _array = array;

    public static readonly EquatableArray<T> Empty = new(System.Array.Empty<T>());

    public int Length => _array?.Length ?? 0;
    public bool IsEmpty => _array is null || _array.Length == 0;
    public T this[int index] => _array![index];
    public ReadOnlySpan<T> AsSpan() => _array.AsSpan();

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null && other._array is null) return true;
        if (_array is null || other._array is null) return false;
        if (_array.Length != other._array.Length) return false;
        for (int i = 0; i < _array.Length; i++)
            if (!_array[i].Equals(other._array[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);
    public override int GetHashCode()
    {
        if (_array is null) return 0;
        unchecked
        {
            int hash = (int)2166136261;
            foreach (var item in _array)
                hash = (hash ^ (item?.GetHashCode() ?? 0)) * 16777619;
            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public Enumerator GetEnumerator() => new(_array);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((_array ?? System.Array.Empty<T>()) as IEnumerable<T>).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((_array ?? System.Array.Empty<T>()) as IEnumerable).GetEnumerator();

    public struct Enumerator
    {
        private readonly T[]? _array;
        private int _index;

        internal Enumerator(T[]? array) { _array = array; _index = -1; }

        public bool MoveNext()
        {
            int next = _index + 1;
            if (_array is null || next >= _array.Length) return false;
            _index = next;
            return true;
        }

        public readonly T Current => _array![_index];
    }
}
