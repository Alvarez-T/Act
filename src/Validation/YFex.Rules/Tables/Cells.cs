namespace YFex.Rules;

/// <summary>Wildcard cell usable where a <see cref="Match{T}"/> or <see cref="Band{T}"/> is expected (<c>Cells.Any</c>).</summary>
public readonly struct AnyCell;

/// <summary>Kind of a <see cref="Match{T}"/> cell.</summary>
public enum MatchKind : byte { Any = 0, Is = 1, In = 2, Not = 3 }

/// <summary>
/// Equality-style condition cell (05 §1.1): <c>Any | Is(x) | In(x, y) | Not(x)</c>. Stored non-boxing (the value
/// itself, or an exact-size array for In); compared with <see cref="EqualityComparer{T}.Default"/>.
/// </summary>
public readonly struct Match<T> : IEquatable<Match<T>>
{
    private readonly T _value;
    private readonly T[]? _set;

    private Match(MatchKind kind, T value, T[]? set)
    {
        Kind = kind;
        _value = value;
        _set = set;
    }

    public MatchKind Kind { get; }
    public T Value => _value;
    /// <summary>Values of an <see cref="MatchKind.In"/> cell (empty otherwise).</summary>
    public ReadOnlySpan<T> Values => _set;

    public static Match<T> Any => default;
    public static Match<T> Is(T value) => new(MatchKind.Is, value, null);
    public static Match<T> Not(T value) => new(MatchKind.Not, value, null);
    public static Match<T> In(params ReadOnlySpan<T> values) => new(MatchKind.In, default!, values.ToArray());

    public static implicit operator Match<T>(AnyCell _) => default;

    public bool Matches(T value)
    {
        var eq = EqualityComparer<T>.Default;
        switch (Kind)
        {
            case MatchKind.Any: return true;
            case MatchKind.Is: return eq.Equals(_value, value);
            case MatchKind.Not: return !eq.Equals(_value, value);
            default:
                foreach (var v in _set!) if (eq.Equals(v, value)) return true;
                return false;
        }
    }

    /// <summary>True when some input matches both cells (publish-time overlap analysis).</summary>
    public bool Intersects(in Match<T> other)
    {
        if (Kind == MatchKind.Any || other.Kind == MatchKind.Any) return true;
        if (Kind == MatchKind.Not && other.Kind == MatchKind.Not) return true;              // assumes > 2 possible values
        if (Kind == MatchKind.Not) return other.HasListedValueOtherThan(_value);
        if (other.Kind == MatchKind.Not) return HasListedValueOtherThan(other._value);
        if (Kind == MatchKind.Is) return other.Matches(_value);
        foreach (var v in _set!) if (other.Matches(v)) return true;
        return false;
    }

    /// <summary>True when every input matching <paramref name="other"/> also matches this cell (unreachable-row analysis).</summary>
    public bool Covers(in Match<T> other)
    {
        if (Kind == MatchKind.Any) return true;
        if (other.Kind == MatchKind.Any) return false;
        if (other.Kind == MatchKind.Not) return Kind == MatchKind.Not && EqualityComparer<T>.Default.Equals(_value, other._value);
        if (other.Kind == MatchKind.Is) return Matches(other._value);
        foreach (var v in other._set!) if (!Matches(v)) return false;
        return true;
    }

    // Is/In: some listed value differs from `value`.
    private bool HasListedValueOtherThan(T value)
    {
        var eq = EqualityComparer<T>.Default;
        if (Kind == MatchKind.Is) return !eq.Equals(_value, value);
        foreach (var v in _set!) if (!eq.Equals(v, value)) return true;
        return false;
    }

    public bool Equals(Match<T> other)
    {
        if (Kind != other.Kind) return false;
        var eq = EqualityComparer<T>.Default;
        if (Kind is MatchKind.Is or MatchKind.Not) return eq.Equals(_value, other._value);
        if (Kind != MatchKind.In) return true;
        if (_set!.Length != other._set!.Length) return false;
        for (int i = 0; i < _set.Length; i++) if (!eq.Equals(_set[i], other._set[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Match<T> m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(Kind, Kind is MatchKind.Is or MatchKind.Not ? EqualityComparer<T>.Default.GetHashCode(_value!) : _set?.Length ?? 0);
    public static bool operator ==(Match<T> a, Match<T> b) => a.Equals(b);
    public static bool operator !=(Match<T> a, Match<T> b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        MatchKind.Any => "*",
        MatchKind.Is => $"= {_value}",
        MatchKind.Not => $"≠ {_value}",
        _ => "∈ {" + string.Join(", ", _set!) + "}",
    };
}

/// <summary>
/// Range-style condition cell, half-open <c>[From, To)</c> (05 §1.1): <c>Any | From(a) | Below(b) | Between(a, b)</c>.
/// </summary>
public readonly struct Band<T> : IEquatable<Band<T>> where T : IComparable<T>
{
    private Band(bool hasFrom, T from, bool hasTo, T to)
    {
        HasFrom = hasFrom;
        FromValue = from;
        HasTo = hasTo;
        ToValue = to;
    }

    public bool HasFrom { get; }
    public T FromValue { get; }
    public bool HasTo { get; }
    /// <summary>Exclusive upper bound.</summary>
    public T ToValue { get; }
    public bool IsAny => !HasFrom && !HasTo;

    public static Band<T> Any => default;
    public static Band<T> From(T inclusive) => new(true, inclusive, false, default!);
    public static Band<T> Below(T exclusive) => new(false, default!, true, exclusive);
    public static Band<T> Between(T fromInclusive, T toExclusive) => new(true, fromInclusive, true, toExclusive);

    public static implicit operator Band<T>(AnyCell _) => default;

    public bool Contains(T value)
        => (!HasFrom || value.CompareTo(FromValue) >= 0) && (!HasTo || value.CompareTo(ToValue) < 0);

    /// <summary>True when the two half-open ranges share a value.</summary>
    public bool Intersects(in Band<T> other)
    {
        // [a, b) ∩ [c, d) ≠ ∅  ⇔  a < d && c < b   (missing sides are infinite)
        bool lowOk = !HasFrom || !other.HasTo || FromValue.CompareTo(other.ToValue) < 0;
        bool highOk = !other.HasFrom || !HasTo || other.FromValue.CompareTo(ToValue) < 0;
        return lowOk && highOk;
    }

    /// <summary>True when <paramref name="other"/> ⊆ this.</summary>
    public bool Covers(in Band<T> other)
    {
        bool low = !HasFrom || (other.HasFrom && other.FromValue.CompareTo(FromValue) >= 0);
        bool high = !HasTo || (other.HasTo && other.ToValue.CompareTo(ToValue) <= 0);
        return low && high;
    }

    public bool Equals(Band<T> other)
        => HasFrom == other.HasFrom && HasTo == other.HasTo
        && (!HasFrom || FromValue.CompareTo(other.FromValue) == 0)
        && (!HasTo || ToValue.CompareTo(other.ToValue) == 0);

    public override bool Equals(object? obj) => obj is Band<T> b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(HasFrom, HasFrom ? FromValue : default, HasTo, HasTo ? ToValue : default);
    public static bool operator ==(Band<T> a, Band<T> b) => a.Equals(b);
    public static bool operator !=(Band<T> a, Band<T> b) => !a.Equals(b);

    public override string ToString() => (HasFrom, HasTo) switch
    {
        (false, false) => "*",
        (true, false) => $"≥ {FromValue}",
        (false, true) => $"< {ToValue}",
        _ => $"[{FromValue}, {ToValue})",
    };
}

/// <summary>
/// Readable row literals: <c>using static YFex.Rules.Cells;</c> then
/// <c>new(Is(PaymentMethod.CreditCard), From(new Money(1000)), Any, 12, new Money(80))</c>.
/// </summary>
public static class Cells
{
    public static AnyCell Any => default;
    public static Match<T> Is<T>(T value) => Match<T>.Is(value);
    public static Match<T> Not<T>(T value) => Match<T>.Not(value);
    public static Match<T> In<T>(params ReadOnlySpan<T> values) => Match<T>.In(values);
    public static Band<T> From<T>(T inclusive) where T : IComparable<T> => Band<T>.From(inclusive);
    public static Band<T> Below<T>(T exclusive) where T : IComparable<T> => Band<T>.Below(exclusive);
    public static Band<T> Between<T>(T fromInclusive, T toExclusive) where T : IComparable<T> => Band<T>.Between(fromInclusive, toExclusive);
}
