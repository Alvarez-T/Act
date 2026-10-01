namespace YFex;

/// <summary>
/// Inclusive <c>[Min, Max]</c> pair — guards on rule parameters, input limits, Composer facets.
/// (Own type: <see cref="System.Range"/> is int-index only.)
/// <code>
/// var guard = new Bounds&lt;Percentual&gt;(Percentual.Zero, Percentual.FromPercentage(20));
/// guard.Contains(p); guard.Clamp(p);
/// </code>
/// </summary>
public readonly record struct Bounds<T>(T Min, T Max);

/// <summary>Operations on <see cref="Bounds{T}"/> for comparable <typeparamref name="T"/>.</summary>
public static class BoundsExtensions
{
    /// <summary>True when <paramref name="value"/> lies within the inclusive bounds.</summary>
    public static bool Contains<T>(this in Bounds<T> bounds, T value) where T : IComparable<T>
        => value.CompareTo(bounds.Min) >= 0 && value.CompareTo(bounds.Max) <= 0;

    /// <summary>Returns <paramref name="value"/> limited to the inclusive bounds.</summary>
    public static T Clamp<T>(this in Bounds<T> bounds, T value) where T : IComparable<T>
        => value.CompareTo(bounds.Min) < 0 ? bounds.Min
         : value.CompareTo(bounds.Max) > 0 ? bounds.Max
         : value;

    /// <summary>True when Min ≤ Max.</summary>
    public static bool IsValid<T>(this in Bounds<T> bounds) where T : IComparable<T>
        => bounds.Min.CompareTo(bounds.Max) <= 0;

    /// <summary>The overlap of two bounds (max of mins, min of maxes). May be invalid (empty).</summary>
    public static Bounds<T> Intersect<T>(this in Bounds<T> bounds, in Bounds<T> other) where T : IComparable<T>
        => new(bounds.Min.CompareTo(other.Min) >= 0 ? bounds.Min : other.Min,
               bounds.Max.CompareTo(other.Max) <= 0 ? bounds.Max : other.Max);
}
