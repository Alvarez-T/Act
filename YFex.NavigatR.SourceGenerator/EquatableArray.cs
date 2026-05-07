using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace YFex.NavigatR.SourceGenerator;

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;
    public static readonly EquatableArray<T> Empty = new(Array.Empty<T>());
    public EquatableArray(T[] array) => _array = array;
    public EquatableArray(IEnumerable<T> items) => _array = items.ToArray();
    public int Length => _array?.Length ?? 0;
    public bool IsEmpty => Length == 0;
    public T this[int index] => _array![index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null && other._array is null) return true;
        if (_array is null || other._array is null) return false;
        if (_array.Length != other._array.Length) return false;
        for (int i = 0; i < _array.Length; i++)
            if (!_array[i].Equals(other._array[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> o && Equals(o);
    public override int GetHashCode()
    {
        if (_array is null) return 0;
        unchecked { int h = 17; foreach (var i in _array) h = h * 31 + i.GetHashCode(); return h; }
    }
    public IEnumerator<T> GetEnumerator() => (_array ?? Array.Empty<T>()).AsEnumerable().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public static bool operator ==(EquatableArray<T> l, EquatableArray<T> r) => l.Equals(r);
    public static bool operator !=(EquatableArray<T> l, EquatableArray<T> r) => !l.Equals(r);
}