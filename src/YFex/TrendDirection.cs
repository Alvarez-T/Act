namespace YFex;

/// <summary>Which way a <see cref="TrendValue"/> moved.</summary>
public enum TrendDirection
{
    /// <summary>No change, or a change under the flat threshold (<see cref="TrendValue.WithFlatBelow"/>).</summary>
    Flat,

    /// <summary>The current value is greater than the previous one.</summary>
    Up,

    /// <summary>The current value is less than the previous one.</summary>
    Down,
}
