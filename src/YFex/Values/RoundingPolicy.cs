namespace YFex;

/// <summary>
/// How money amounts are rounded: number of decimals + <see cref="MidpointRounding"/>. Shared by
/// Rules (adjustments), Finance and Fiscal so rounding happens explicitly and in one place.
/// <code>
/// var r = RoundingPolicy.Currency();              // 2 decimals, AwayFromZero
/// Money m = r.Round(new Money(10.005m));
/// </code>
/// </summary>
public readonly record struct RoundingPolicy
{
    public RoundingPolicy(int decimals, MidpointRounding mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 28);
        Decimals = decimals;
        Mode = mode;
    }

    public int Decimals { get; }
    public MidpointRounding Mode { get; }

    /// <summary>Currency rounding (default: 2 decimals, midpoint away from zero — the BR fiscal convention).</summary>
    public static RoundingPolicy Currency(int decimals = 2, MidpointRounding mode = MidpointRounding.AwayFromZero)
        => new(decimals, mode);

    public decimal Round(decimal value) => Math.Round(value, Decimals, Mode);

    /// <summary>
    /// Rounds a money amount. Note <see cref="Money"/> itself keeps <see cref="Money.Scale"/> decimals,
    /// so policies with more decimals than that are further rounded by <see cref="Money"/>.
    /// </summary>
    public Money Round(Money value) => new(Math.Round(value.Amount, Decimals, Mode));
}
