using System.Numerics;
using YFex.Helpers;

namespace YFex;

/// <summary>
/// Compact set of allowed discrete values: allowed options (installment counts, payment methods),
/// enum facets, filters. Integers 0..63 are stored as a <see cref="ulong"/> bitmask; anything else
/// (larger numbers, text, enum codes) as an exact-size sorted <see cref="RawValue"/> array.
/// <para>
/// <c>default</c> (<see cref="IsEmpty"/>) means <b>no restriction</b> — not "nothing allowed".
/// An intersection with no common value yields <see cref="AllowsNothing"/>.
/// </para>
/// <code>
/// OptionSet.Range(1, 6)                         → {1..6} (bits)
/// OptionSet.Range(1, 12).Intersect(Range(1, 6)) → {1..6}
/// </code>
/// </summary>
public readonly struct OptionSet : IEquatable<OptionSet>
{
    private enum Mode : byte { Unrestricted = 0, Bits = 1, Values = 2 }

    private readonly ulong _bits;
    private readonly RawValue[]? _values; // sorted, distinct; only in Values mode
    private readonly Mode _mode;

    private OptionSet(ulong bits)
    {
        _bits = bits;
        _values = null;
        _mode = Mode.Bits;
    }

    private OptionSet(RawValue[] sortedDistinct)
    {
        _bits = 0;
        _values = sortedDistinct;
        _mode = Mode.Values;
    }

    /// <summary>No restriction (every value allowed).</summary>
    public static OptionSet Unrestricted => default;

    /// <summary>Nothing allowed.</summary>
    public static OptionSet Nothing => new(0UL);

    /// <summary>True when there is no restriction (the default).</summary>
    public bool IsEmpty => _mode == Mode.Unrestricted;

    /// <summary>True when the set is a restriction that allows no value at all.</summary>
    public bool AllowsNothing => _mode == Mode.Bits ? _bits == 0 : _mode == Mode.Values && _values!.Length == 0;

    /// <summary>Number of allowed values; 0 when unrestricted (check <see cref="IsEmpty"/> first).</summary>
    public int Count => _mode switch
    {
        Mode.Bits => BitOperations.PopCount(_bits),
        Mode.Values => _values!.Length,
        _ => 0,
    };

    // ── Factories ────────────────────────────────────────────────────────────

    /// <summary>All integers in the inclusive range.</summary>
    public static OptionSet Range(int fromInclusive, int toInclusive)
    {
        if (toInclusive < fromInclusive) return Nothing;
        if (fromInclusive >= 0 && toInclusive < 64)
        {
            ulong mask = (toInclusive == 63 ? ulong.MaxValue : (1UL << (toInclusive + 1)) - 1)
                       & ~((1UL << fromInclusive) - 1);
            return new OptionSet(mask);
        }
        var values = new RawValue[toInclusive - fromInclusive + 1];
        for (int i = 0; i < values.Length; i++) values[i] = RawValue.From((long)fromInclusive + i);
        return new OptionSet(values);
    }

    /// <summary>The given integers (duplicates ignored).</summary>
    public static OptionSet Of(ReadOnlySpan<long> values)
    {
        ulong bits = 0;
        bool small = true;
        foreach (long v in values)
        {
            if ((ulong)v < 64) bits |= 1UL << (int)v;
            else { small = false; break; }
        }
        if (small) return new OptionSet(bits);

        var raw = new RawValue[values.Length];
        for (int i = 0; i < raw.Length; i++) raw[i] = RawValue.From(values[i]);
        return FromUnsorted(raw);
    }

    /// <summary>Any values (text, enum codes, dates…). Integer values 0..63 of kind Int32/Int64 are
    /// stored as bits when every value qualifies.</summary>
    public static OptionSet Of(ReadOnlySpan<RawValue> values)
    {
        ulong bits = 0;
        bool small = true;
        foreach (ref readonly RawValue v in values)
        {
            if (v.Kind is ValueKind.Int32 or ValueKind.Int64 && v.TryGet(out long l) && (ulong)l < 64)
                bits |= 1UL << (int)l;
            else { small = false; break; }
        }
        return small ? new OptionSet(bits) : FromUnsorted(values.ToArray());
    }

    /// <summary>The given enum members (stored with their stable codes).</summary>
    public static OptionSet OfEnum<TEnum>(ReadOnlySpan<TEnum> values) where TEnum : unmanaged, Enum
    {
        var raw = new RawValue[values.Length];
        for (int i = 0; i < raw.Length; i++) raw[i] = RawValue.FromEnum(values[i]);
        return FromUnsorted(raw);
    }

    private static OptionSet FromUnsorted(RawValue[] values)
    {
        Array.Sort(values);
        int n = 0;
        for (int i = 0; i < values.Length; i++)
            if (n == 0 || values[i].CompareTo(values[n - 1]) != 0) values[n++] = values[i];
        if (n != values.Length) Array.Resize(ref values, n);
        return new OptionSet(values);
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="value"/> is allowed (always true when unrestricted).</summary>
    public bool Contains(long value) => _mode switch
    {
        Mode.Unrestricted => true,
        Mode.Bits => (ulong)value < 64 && (_bits & (1UL << (int)value)) != 0,
        _ => IndexOf(RawValue.From(value)) >= 0,
    };

    /// <summary>True when <paramref name="value"/> is allowed (always true when unrestricted).</summary>
    public bool Contains(in RawValue value)
    {
        switch (_mode)
        {
            case Mode.Unrestricted:
                return true;
            case Mode.Bits:
                return value.Kind is ValueKind.Int32 or ValueKind.Int64 or ValueKind.EnumCode
                    && value.TryGet(out long l) && Contains(l);
            default:
                return IndexOf(value) >= 0;
        }
    }

    /// <summary>Most-restrictive combination: values allowed by both sets.</summary>
    public OptionSet Intersect(in OptionSet other)
    {
        if (_mode == Mode.Unrestricted) return other;
        if (other._mode == Mode.Unrestricted) return this;
        if (_mode == Mode.Bits && other._mode == Mode.Bits) return new OptionSet(_bits & other._bits);

        // At least one side holds values: keep the values of one side that the other contains.
        OptionSet source = _mode == Mode.Values ? this : other;
        OptionSet filter = _mode == Mode.Values ? other : this;
        RawValue[] src = source._values!;
        var kept = new RawValue[src.Length];
        int n = 0;
        foreach (ref readonly RawValue v in src.AsSpan())
            if (filter.Contains(in v)) kept[n++] = v;
        if (n != kept.Length) Array.Resize(ref kept, n);
        return new OptionSet(kept);
    }

    /// <summary>Appends a canonical encoding to <paramref name="hash"/> (for fingerprints).</summary>
    public void AppendTo(ref StableHash64 hash)
    {
        hash.Append((byte)_mode);
        if (_mode == Mode.Bits) hash.Append(_bits);
        else if (_mode == Mode.Values)
        {
            hash.Append(_values!.Length);
            foreach (ref readonly RawValue v in _values.AsSpan()) v.AppendTo(ref hash);
        }
    }

    private int IndexOf(in RawValue value)
    {
        RawValue[] values = _values!;
        int lo = 0, hi = values.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (!values[mid].TryCompareTo(in value, out int c)) return -1; // incompatible kind → not a member
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    // ── Enumeration ──────────────────────────────────────────────────────────

    /// <summary>Allowed values in ascending order (nothing when unrestricted).</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Struct enumerator of <see cref="RawValue"/>; bit members yield Int64 values.</summary>
    public struct Enumerator
    {
        private readonly OptionSet _set;
        private ulong _remaining;
        private int _index;

        internal Enumerator(in OptionSet set)
        {
            _set = set;
            _remaining = set._mode == Mode.Bits ? set._bits : 0;
            _index = -1;
            Current = default;
        }

        public RawValue Current { get; private set; }

        public bool MoveNext()
        {
            if (_set._mode == Mode.Bits)
            {
                if (_remaining == 0) return false;
                int bit = BitOperations.TrailingZeroCount(_remaining);
                _remaining &= _remaining - 1;
                Current = RawValue.From((long)bit);
                return true;
            }
            if (_set._mode == Mode.Values && ++_index < _set._values!.Length)
            {
                Current = _set._values[_index];
                return true;
            }
            return false;
        }
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(OptionSet other)
    {
        if (_mode != other._mode) return false;
        return _mode switch
        {
            Mode.Bits => _bits == other._bits,
            Mode.Values => _values.AsSpan().SequenceEqual(other._values),
            _ => true,
        };
    }

    public override bool Equals(object? obj) => obj is OptionSet other && Equals(other);

    public override int GetHashCode()
    {
        if (_mode == Mode.Bits) return HashCode.Combine(_mode, _bits);
        if (_mode != Mode.Values) return 0;
        var h = new HashCode();
        foreach (ref readonly RawValue v in _values.AsSpan()) h.Add(v);
        return h.ToHashCode();
    }

    public static bool operator ==(OptionSet left, OptionSet right) => left.Equals(right);
    public static bool operator !=(OptionSet left, OptionSet right) => !left.Equals(right);
}
