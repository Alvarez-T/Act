using System;
using System.Collections.Generic;
using System.Linq;

namespace YFex.Persistence.Generator;

/// <summary>
/// Immutable array wrapper that implements structural equality for use in
/// Roslyn incremental generator models. Without this, the default array
/// reference equality causes the pipeline to re-run on every compilation
/// even when the semantic content has not changed.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public T[] Items => _items ?? System.Array.Empty<T>();

    public int Count => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other)
    {
        if (ReferenceEquals(_items, other._items)) return true;
        if (_items is null || other._items is null) return false;
        if (_items.Length != other._items.Length) return false;
        for (int i = 0; i < _items.Length; i++)
            if (!_items[i].Equals(other._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj)
        => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_items is null) return 0;
        unchecked
        {
            int h = 17;
            foreach (var item in _items) h = h * 31 + item.GetHashCode();
            return h;
        }
    }

    public T[] ToArray() => _items is null ? System.Array.Empty<T>() : (T[])_items.Clone();
}
