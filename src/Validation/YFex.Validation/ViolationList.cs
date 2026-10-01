namespace YFex.Validation;

/// <summary>
/// Immutable list of violations wrapping an exact-size array (or nothing). Struct enumerator; no LINQ.
/// <c>default</c> = empty, no allocation.
/// </summary>
public readonly struct ViolationList
{
    private readonly Violation[]? _items;

    /// <summary>Takes ownership of <paramref name="items"/> (must not be mutated afterwards).</summary>
    internal ViolationList(Violation[]? items) => _items = items is { Length: > 0 } ? items : null;

    public static ViolationList Empty => default;

    /// <summary>Copies <paramref name="items"/> into a new list.</summary>
    public static ViolationList From(ReadOnlySpan<Violation> items)
        => items.IsEmpty ? default : new ViolationList(items.ToArray());

    /// <summary>
    /// Wraps <paramref name="items"/> without copying — the caller hands over ownership and must never
    /// mutate the array afterwards. For stores that keep immutable exact-size arrays (the State bag).
    /// </summary>
    public static ViolationList FromOwnedArray(Violation[]? items) => new(items);

    public int Count => _items?.Length ?? 0;
    public bool IsEmpty => _items is null;

    public ref readonly Violation this[int index] => ref _items![index];

    public ReadOnlySpan<Violation> AsSpan() => _items;

    public Enumerator GetEnumerator() => new(_items);

    /// <summary>True when any violation has at least <paramref name="atLeast"/> severity.</summary>
    public bool Any(Severity atLeast)
    {
        foreach (ref readonly Violation v in AsSpan())
            if (v.Severity >= atLeast) return true;
        return false;
    }

    /// <summary>Number of <see cref="Severity.Error"/> violations.</summary>
    public int ErrorCount
    {
        get
        {
            int n = 0;
            foreach (ref readonly Violation v in AsSpan())
                if (v.Severity == Severity.Error) n++;
            return n;
        }
    }

    /// <summary>True when any violation has <paramref name="code"/>.</summary>
    public bool Contains(RuleCode code)
    {
        foreach (ref readonly Violation v in AsSpan())
            if (v.Code == code) return true;
        return false;
    }

    /// <summary>Violations at exactly <paramref name="path"/>. Allocates only for a strict subset.</summary>
    public ViolationList For(in FieldPath path)
    {
        ReadOnlySpan<Violation> items = AsSpan();
        int matches = 0;
        foreach (ref readonly Violation v in items)
            if (v.Path.Equals(path)) matches++;

        if (matches == items.Length) return this;
        if (matches == 0) return default;

        var result = new Violation[matches];
        int n = 0;
        foreach (ref readonly Violation v in items)
            if (v.Path.Equals(path)) result[n++] = v;
        return new ViolationList(result);
    }

    /// <summary>Violations whose formatted path equals <paramref name="formattedPath"/> (<c>"Lines[2].Qty"</c>).</summary>
    public ViolationList For(ReadOnlySpan<char> formattedPath)
    {
        ReadOnlySpan<Violation> items = AsSpan();
        int matches = 0;
        foreach (ref readonly Violation v in items)
            if (v.Path.FormattedEquals(formattedPath)) matches++;

        if (matches == items.Length) return this;
        if (matches == 0) return default;

        var result = new Violation[matches];
        int n = 0;
        foreach (ref readonly Violation v in items)
            if (v.Path.FormattedEquals(formattedPath)) result[n++] = v;
        return new ViolationList(result);
    }

    /// <summary>Concatenation (allocates only when both are non-empty).</summary>
    public ViolationList Concat(in ViolationList other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        var result = new Violation[Count + other.Count];
        _items!.CopyTo(result, 0);
        other._items!.CopyTo(result, Count);
        return new ViolationList(result);
    }

    public ref struct Enumerator
    {
        private readonly Violation[]? _items;
        private int _index;

        internal Enumerator(Violation[]? items)
        {
            _items = items;
            _index = -1;
        }

        public bool MoveNext() => _items is not null && ++_index < _items.Length;

        public readonly ref readonly Violation Current => ref _items![_index];
    }
}
