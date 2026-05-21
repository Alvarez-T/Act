using System.Numerics;

namespace YFex.Extensions;

public static class NumberExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is within
    /// the inclusive range [<paramref name="min"/>, <paramref name="max"/>].
    ///
    /// <code>
    /// age.IsBetween(18, 65)
    /// price.IsBetween(0m, 999.99m)
    /// </code>
    /// </summary>
    public static bool IsBetween<T>(this T value, T min, T max)
        where T : IComparable<T>
    {
        if (min.CompareTo(max) > 0)
            throw new ArgumentException($"{nameof(min)} must be ≤ {nameof(max)}.");
        return value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0;
    }

    /// <summary>Exclusive variant: (min, max) open interval.</summary>
    public static bool IsStrictlyBetween<T>(this T value, T min, T max)
        where T : IComparable<T>
        => value.CompareTo(min) > 0 && value.CompareTo(max) < 0;

    /// <summary>
    /// Returns <see langword="true"/> for numeric types when the value is
    /// positive (> 0), negative (< 0), or zero, without boxing.
    /// </summary>
    public static bool IsPositive<T>(this T value) where T : INumber<T>
        => T.IsPositive(value) && !T.IsZero(value);

    public static bool IsNegative<T>(this T value) where T : INumber<T>
        => T.IsNegative(value);

    public static bool IsZero<T>(this T value) where T : INumber<T>
        => T.IsZero(value);

    public static int ToInt(this Enum enumValue)
    => Convert.ToInt32(enumValue);

    public static int ToInt(this bool boolean)
        => Convert.ToInt32(boolean);

    public static int? TryParseToInt(this string? content)
    => int.TryParse(content, out var value) ? value : null;

    public static int ParseToInt(this string? content)
        => int.Parse(content!);
}
