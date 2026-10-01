using System.Runtime.CompilerServices;

namespace YFex;

/// <summary>Case of <see cref="Rate"/>: a percentage of a base amount.</summary>
public readonly record struct PercentRate(Percentual Percent);

/// <summary>Case of <see cref="Rate"/>: a fixed amount, independent of the base.</summary>
public readonly record struct FixedRate(Money Amount);

/// <summary>Which case a <see cref="Rate"/> holds.</summary>
public enum RateKind : byte { None = 0, Percent = 1, Fixed = 2 }

/// <summary>
/// "Percent or fixed amount" — discounts, fees, interest, commissions (Rules, Finance, Payment,
/// Fiscal). <see cref="Apply"/> computes the amount from a base with explicit rounding, so callers
/// never do money math themselves.
/// <code>
/// Rate r = Rate.Percent(Percentual.FromPercentage(5));
/// Money off = r.Apply(new Money(200m), RoundingPolicy.Currency());   // 10.00
/// var text = r switch { PercentRate p => …, FixedRate f => … };        // non-boxing match
/// </code>
/// </summary>
[Union]
public readonly struct Rate : IUnion, IEquatable<Rate>
{
    private readonly Percentual _percent;
    private readonly Money _amount;
    private readonly byte _tag; // 0 = none, 1 = percent, 2 = fixed

    public Rate(PercentRate value)
    {
        _percent = value.Percent;
        _amount = default;
        _tag = 1;
    }

    public Rate(FixedRate value)
    {
        _percent = default;
        _amount = value.Amount;
        _tag = 2;
    }

    public static Rate Percent(Percentual percent) => new(new PercentRate(percent));
    public static Rate Fixed(Money amount) => new(new FixedRate(amount));

    /// <summary>Required by <see cref="IUnion"/>. <b>Boxes</b> — never call it in library code.</summary>
    public object? Value => _tag switch
    {
        1 => new PercentRate(_percent),
        2 => new FixedRate(_amount),
        _ => null,
    };

    public bool HasValue => _tag != 0;
    public RateKind Kind => (RateKind)_tag;

    public bool TryGetValue(out PercentRate value)
    {
        value = new PercentRate(_percent);
        return _tag == 1;
    }

    public bool TryGetValue(out FixedRate value)
    {
        value = new FixedRate(_amount);
        return _tag == 2;
    }

    /// <summary>
    /// Amount this rate represents over <paramref name="base"/>: percent → base × p, rounded by
    /// <paramref name="rounding"/>; fixed → the fixed amount (rounded too); none → zero.
    /// </summary>
    public Money Apply(Money @base, in RoundingPolicy rounding) => _tag switch
    {
        1 => new Money(rounding.Round(_percent.ApplyTo(@base.Amount))),
        2 => rounding.Round(_amount),
        _ => Money.Zero,
    };

    public static implicit operator Rate(PercentRate value) => new(value);
    public static implicit operator Rate(FixedRate value) => new(value);

    public bool Equals(Rate other) => _tag == other._tag && _tag switch
    {
        1 => _percent.ToPercentageValue() == other._percent.ToPercentageValue(),
        2 => _amount == other._amount,
        _ => true,
    };

    public override bool Equals(object? obj) => obj is Rate other && Equals(other);
    public override int GetHashCode() => _tag switch
    {
        1 => HashCode.Combine(1, _percent.ToPercentageValue()),
        2 => HashCode.Combine(2, _amount),
        _ => 0,
    };

    public static bool operator ==(Rate left, Rate right) => left.Equals(right);
    public static bool operator !=(Rate left, Rate right) => !left.Equals(right);

    public override string ToString() => _tag switch
    {
        1 => _percent.ToString(),
        2 => _amount.ToString(),
        _ => string.Empty,
    };
}
