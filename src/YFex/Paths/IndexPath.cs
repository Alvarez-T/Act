using System.Runtime.CompilerServices;

namespace YFex;

/// <summary>
/// Up to <see cref="MaxDepth"/> collection indices, stored inline (no allocation). Fills the
/// <c>[]</c> placeholders of a <see cref="FieldPath"/> template in order. Deeper nesting is rejected
/// (the validation generator reports YFV0014 at compile time).
/// </summary>
public readonly struct IndexPath : IEquatable<IndexPath>
{
    /// <summary>Maximum number of indices.</summary>
    public const int MaxDepth = 4;

    [InlineArray(MaxDepth)]
    private struct Buffer { private int _e0; }

    private readonly Buffer _items;
    private readonly byte _count;

    private IndexPath(in Buffer items, int count)
    {
        _items = items;
        _count = (byte)count;
    }

    public static IndexPath Empty => default;

    public static IndexPath Of(int i0) => Empty.Append(i0);
    public static IndexPath Of(int i0, int i1) => Empty.Append(i0).Append(i1);
    public static IndexPath Of(int i0, int i1, int i2) => Empty.Append(i0).Append(i1).Append(i2);

    public static IndexPath Of(ReadOnlySpan<int> indices)
    {
        if (indices.Length > MaxDepth) ThrowTooDeep();
        Buffer b = default;
        indices.CopyTo(b);
        return new IndexPath(b, indices.Length);
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public int this[int index]
    {
        get
        {
            if ((uint)index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[index];
        }
    }

    /// <summary>Copies the indices to <paramref name="destination"/>; returns how many were written.</summary>
    public int CopyTo(Span<int> destination)
    {
        ReadOnlySpan<int> items = _items;
        items[.._count].CopyTo(destination);
        return _count;
    }

    /// <summary>Returns a copy with <paramref name="index"/> added at the end.</summary>
    public IndexPath Append(int index)
    {
        if (_count == MaxDepth) ThrowTooDeep();
        Buffer b = _items;
        b[_count] = index;
        return new IndexPath(b, _count + 1);
    }

    /// <summary>Returns a copy with <paramref name="index"/> added at the front (used when a nested
    /// path is placed under an indexed parent: <c>Lines[2]</c> + <c>Quantity</c>).</summary>
    public IndexPath Prepend(int index)
    {
        if (_count == MaxDepth) ThrowTooDeep();
        Buffer b = default;
        b[0] = index;
        CopyTo(((Span<int>)b)[1..]);
        return new IndexPath(b, _count + 1);
    }

    /// <summary>Returns a copy with <paramref name="other"/>'s indices appended.</summary>
    public IndexPath Concat(in IndexPath other)
    {
        if (_count + other._count > MaxDepth) ThrowTooDeep();
        Buffer b = _items;
        other.CopyTo(((Span<int>)b)[_count..]);
        return new IndexPath(b, _count + other._count);
    }

    public bool Equals(IndexPath other)
    {
        if (_count != other._count) return false;
        for (int i = 0; i < _count; i++)
            if (_items[i] != other._items[i]) return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is IndexPath other && Equals(other);

    public override int GetHashCode()
    {
        var h = new HashCode();
        for (int i = 0; i < _count; i++) h.Add(_items[i]);
        return h.ToHashCode();
    }

    public static bool operator ==(IndexPath left, IndexPath right) => left.Equals(right);
    public static bool operator !=(IndexPath left, IndexPath right) => !left.Equals(right);

    private static void ThrowTooDeep()
        => throw new InvalidOperationException($"A path supports at most {MaxDepth} collection indices.");
}
