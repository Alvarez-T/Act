using Act.Utils;
using System.Globalization;

namespace YFex.Extensions;

public static class PercentualExtensions
{
    /// <summary>
    /// Treats <paramref name="percentageValue"/> as a display percentage value.
    /// <c>47.5m.AsPercentual()</c> → "47.5 %"
    /// </summary>
    public static Percentual AsPercentual(this decimal percentageValue, int? decimalPlaces = null)
        => Percentual.FromPercentage(percentageValue, decimalPlaces);

    /// <inheritdoc cref="AsPercentual(decimal, int?)"/>
    public static Percentual AsPercentual(this double percentageValue, int? decimalPlaces = null)
        => Percentual.FromPercentage((decimal)percentageValue, decimalPlaces);

    /// <inheritdoc cref="AsPercentual(decimal, int?)"/>
    public static Percentual AsPercentual(this int percentageValue, int? decimalPlaces = null)
        => Percentual.FromPercentage(percentageValue, decimalPlaces);

    /// <summary>
    /// Treats <paramref name="fractionValue"/> as a decimal fraction.
    /// <c>0.475m.ToPercentual()</c> → "47.5 %"
    /// </summary>
    public static Percentual ToPercentual(this decimal fractionValue, int? decimalPlaces = null)
        => Percentual.FromFraction(fractionValue, decimalPlaces);

    /// <inheritdoc cref="ToPercentual(decimal, int?)"/>
    public static Percentual ToPercentual(this double fractionValue, int? decimalPlaces = null)
        => Percentual.FromFraction((decimal)fractionValue, decimalPlaces);

    /// <summary>Parses a string such as "47.5" or "47.5 %".</summary>
    public static Percentual ToPercentual(this string s, int? decimalPlaces = null)
        => Percentual.Parse(s, decimalPlaces);

    /// <summary>
    /// Returns <see langword="true"/> and the parsed value when the string is
    /// a valid percentage; <see langword="false"/> otherwise (never throws).
    /// </summary>
    public static bool TryToPercentual(
        this string? s,
        out Percentual result,
        int? decimalPlaces = null)
    {
        if (s is not null && Percentual.TryParse(s, CultureInfo.InvariantCulture, out result))
        {
            if (decimalPlaces.HasValue)
                result = result.WithDecimalPlaces(decimalPlaces.Value);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Multiplies <paramref name="value"/> by this percentage.
    /// <c>200m.ApplyPercentage(15 %) → 30m</c>
    /// </summary>
    public static decimal ApplyPercentage(this decimal value, Percentual percent)
        => percent.ApplyTo(value);

    /// <summary>
    /// Adds the percentage of <paramref name="value"/> to itself.
    /// <c>100m.IncreaseBy(20 %) → 120m</c>
    /// </summary>
    public static decimal IncreaseBy(this decimal value, Percentual percent)
        => value + percent.ApplyTo(value);

    /// <summary>
    /// Subtracts the percentage of <paramref name="value"/> from itself.
    /// <c>100m.ReduceBy(20 %) → 80m</c>
    /// </summary>
    public static decimal ReduceBy(this decimal value, Percentual percent)
        => value - percent.ApplyTo(value);

    /// <summary>
    /// Returns the original value before a percentage increase was applied.
    /// <c>120m.ReverseIncrease(20 %) → 100m</c>
    /// </summary>
    public static decimal ReverseIncrease(this decimal value, Percentual percent)
    {
        var divisor = 1m + percent.ToFraction();
        if (divisor == 0m) throw new DivideByZeroException("Percentage produces a zero divisor.");
        return value / divisor;
    }

    /// <summary>
    /// Returns the original value before a percentage reduction was applied.
    /// <c>80m.ReverseReduction(20 %) → 100m</c>
    /// </summary>
    public static decimal ReverseReduction(this decimal value, Percentual percent)
    {
        var divisor = 1m - percent.ToFraction();
        if (divisor == 0m) throw new DivideByZeroException("100 % reduction produces a zero divisor.");
        return value / divisor;
    }


    /// <summary>
    /// Returns the signed percentage change relative to <paramref name="baseline"/>.
    /// Positive when the new value is larger; negative when smaller.
    /// <c>120m.PercentChangeFrom(100m) → +20 %</c>
    /// <c>80m.PercentChangeFrom(100m)  → −20 %</c>
    /// </summary>
    /// <remarks>
    /// Previously named <c>PercentageDifferenceFrom</c>. Renamed because
    /// "difference" implies an absolute value; this method returns a signed,
    /// directional change — the standard meaning of "percentage change".
    /// </remarks>
    public static Percentual PercentChangeFrom(
        this decimal newValue,
        decimal baseline,
        int? decimalPlaces = null)
    {
        if (baseline == 0m)
            throw new DivideByZeroException("Baseline cannot be zero when computing percentage change.");

        return Percentual.FromPercentage(
            (newValue - baseline) / baseline * 100m,
            decimalPlaces);
    }

    /// <summary>
    /// Returns the unsigned (absolute) percentage difference between two values.
    /// Order does not matter.
    /// <c>80m.AbsolutePercentDifference(100m) → 20 %</c>
    /// </summary>
    public static Percentual AbsolutePercentDifference(
        this decimal value,
        decimal other,
        int? decimalPlaces = null)
    {
        if (other == 0m)
            throw new DivideByZeroException("Reference value cannot be zero.");

        return Percentual.FromPercentage(
            Math.Abs(value - other) / Math.Abs(other) * 100m,
            decimalPlaces);
    }

    /// <summary>
    /// Returns this value expressed as a percentage of <paramref name="total"/>.
    /// <c>30m.AsPercentageOf(200m) → 15 %</c>
    /// </summary>
    public static Percentual AsPercentageOf(
        this decimal part,
        decimal total,
        int? decimalPlaces = null)
    {
        if (total == 0m)
            throw new DivideByZeroException("Total cannot be zero.");

        return Percentual.FromPercentage(part / total * 100m, decimalPlaces);
    }

    /// <summary>
    /// Returns the value that corresponds to this percentage of <paramref name="total"/>.
    /// Inverse of <see cref="AsPercentageOf"/>.
    /// <c>Percentual.FromPercentage(15).PortionOf(200m) → 30m</c>
    /// </summary>
    public static decimal PortionOf(this Percentual percent, decimal total)
        => percent.ApplyTo(total);

    /// <summary>
    /// Compound growth: applies this percentage repeatedly for
    /// <paramref name="periods"/> periods.
    /// <c>100m.CompoundGrowth(10 %, 3) → 133.10m</c>
    /// </summary>
    public static decimal CompoundGrowth(this decimal principal, Percentual ratePerPeriod, int periods)
    {
        if (periods < 0) throw new ArgumentOutOfRangeException(nameof(periods), "Must be ≥ 0.");
        return principal * (decimal)Math.Pow((double)(1m + ratePerPeriod.ToFraction()), periods);
    }

    /// <summary>Arithmetic mean of a sequence of <see cref="Percentual"/> values.</summary>
    public static Percentual Average(this IEnumerable<Percentual> source)
    {
        var list = source as IList<Percentual> ?? source.ToList();
        if (list.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements.");

        decimal sum = 0m;
        int maxDp = 0;
        foreach (var p in list)
        {
            sum += p.ToPercentageValue();
            maxDp = Math.Max(maxDp, p.DecimalPlaces);
        }
        return Percentual.FromPercentage(sum / list.Count, maxDp);
    }

    /// <summary>Sum of a sequence of <see cref="Percentual"/> values.</summary>
    public static Percentual Sum(this IEnumerable<Percentual> source)
    {
        decimal sum = 0m;
        int maxDp = 0;
        foreach (var p in source)
        {
            sum += p.ToPercentageValue();
            maxDp = Math.Max(maxDp, p.DecimalPlaces);
        }
        return Percentual.FromPercentage(sum, maxDp);
    }

    /// <summary>
    /// Returns the largest value in the sequence.
    /// Uses <see cref="IComparable{T}"/> so LINQ's own <c>Max</c> also works.
    /// </summary>
    public static Percentual MaxPercentual(this IEnumerable<Percentual> source)
        => source.Aggregate((a, b) => a > b ? a : b);

    /// <summary>Returns the smallest value in the sequence.</summary>
    public static Percentual MinPercentual(this IEnumerable<Percentual> source)
        => source.Aggregate((a, b) => a < b ? a : b);

    /// <summary>
    /// Weighted average: each percentage is weighted by the corresponding
    /// <paramref name="weights"/> value.
    /// Both sequences must have the same length.
    /// </summary>
    public static Percentual WeightedAverage(
        this IEnumerable<Percentual> source,
        IEnumerable<decimal> weights)
    {
        var pList = source as IList<Percentual> ?? source.ToList();
        var wList = weights as IList<decimal> ?? weights.ToList();

        if (pList.Count != wList.Count)
            throw new ArgumentException("Source and weights must have the same number of elements.");
        if (pList.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements.");

        decimal weightedSum = 0m;
        decimal totalWeight = 0m;
        int maxDp = 0;

        for (int i = 0; i < pList.Count; i++)
        {
            weightedSum += pList[i].ToPercentageValue() * wList[i];
            totalWeight += wList[i];
            maxDp = Math.Max(maxDp, pList[i].DecimalPlaces);
        }

        if (totalWeight == 0m)
            throw new DivideByZeroException("Total weight cannot be zero.");

        return Percentual.FromPercentage(weightedSum / totalWeight, maxDp);
    }

    /// <summary>
    /// Returns the arithmetic mean of <paramref name="source"/> expressed as a
    /// <see cref="Percentual"/> percentage of <paramref name="total"/>.
    ///
    /// <example><code>
    /// // What percentage of budget does the average line item represent?
    /// var avg = lineItems.PercentualAverage(totalBudget);
    /// </code></example>
    /// </summary>
    public static Percentual PercentualAverage(
        this IEnumerable<decimal> source,
        decimal total,
        int? decimalPlaces = null)
    {
        if (total == 0m) throw new DivideByZeroException("Total cannot be zero.");

        decimal sum = 0m;
        int count = 0;
        foreach (var v in source) { sum += v; count++; }

        if (count == 0) throw new InvalidOperationException("Sequence contains no elements.");
        return Percentual.FromPercentage(sum / count / total * 100m, decimalPlaces);
    }

    /// <inheritdoc cref="PercentualAverage(IEnumerable{decimal}, decimal, int?)"/>
    public static Percentual PercentualAverage(
        this IEnumerable<double> source,
        double total,
        int? decimalPlaces = null)
    {
        if (total == 0d) throw new DivideByZeroException("Total cannot be zero.");

        double sum = 0d;
        int count = 0;
        foreach (var v in source) { sum += v; count++; }

        if (count == 0) throw new InvalidOperationException("Sequence contains no elements.");
        return Percentual.FromPercentage((decimal)(sum / count / total * 100d), decimalPlaces);
    }

    /// <inheritdoc cref="PercentualAverage(IEnumerable{decimal}, decimal, int?)"/>
    public static Percentual PercentualAverage(
        this IEnumerable<int> source,
        int total,
        int? decimalPlaces = null)
    {
        if (total == 0) throw new DivideByZeroException("Total cannot be zero.");

        long sum = 0L;
        int count = 0;
        foreach (var v in source) { sum += v; count++; }

        if (count == 0) throw new InvalidOperationException("Sequence contains no elements.");
        return Percentual.FromPercentage((decimal)sum / count / total * 100m, decimalPlaces);
    }

    /// <summary>
    /// Generic version for any numeric type that implements the .NET 7+
    /// <see cref="System.Numerics.INumber{TSelf}"/> interface.
    ///
    /// <example><code>
    /// float[] scores = { 0.8f, 0.9f, 0.75f };
    /// Percentual avg = scores.PercentualAverage(1.0f); // avg score as % of max
    /// </code></example>
    /// </summary>
    public static Percentual PercentualAverage<T>(
        this IEnumerable<T> source,
        T total,
        int? decimalPlaces = null)
        where T : System.Numerics.INumber<T>
    {
        if (T.IsZero(total)) throw new DivideByZeroException("Total cannot be zero.");

        T sum = T.Zero;
        int count = 0;
        foreach (var v in source) { sum += v; count++; }

        if (count == 0) throw new InvalidOperationException("Sequence contains no elements.");

        // Convert via double — T could be float, Half, BFloat16 (.NET 11), etc.
        double mean = double.CreateChecked(sum) / count;
        double totalD = double.CreateChecked(total);
        double percentage = mean / totalD * 100d;

        return Percentual.FromPercentage((decimal)percentage, decimalPlaces);
    }

    /// <summary>
    /// Projects each element to a percentage of the sequence's own total.
    /// The resulting percentages sum to 100 % (subject to rounding).
    ///
    /// <example><code>
    /// decimal[] sales = { 300m, 100m, 600m };
    /// // → [30 %, 10 %, 60 %]
    /// var shares = sales.ToPercentualDistribution().ToList();
    /// </code></example>
    /// </summary>
    public static IEnumerable<Percentual> ToPercentualDistribution(
        this IEnumerable<decimal> source,
        int? decimalPlaces = null)
    {
        var list = source as IList<decimal> ?? source.ToList();
        var total = list.Sum();
        if (total == 0m) throw new DivideByZeroException("Cannot distribute: total is zero.");
        return list.Select(v => Percentual.FromPercentage(v / total * 100m, decimalPlaces));
    }

    /// <summary>
    /// Projects each element of a keyed collection to its percentage share of
    /// the total, preserving the key.
    ///
    /// <example><code>
    /// var revenue = new Dictionary&lt;string, decimal&gt;
    /// {
    ///     ["EMEA"] = 400m, ["APAC"] = 350m, ["AMER"] = 250m
    /// };
    /// var shares = revenue.ToPercentualDistribution();
    /// // → { "EMEA": 40 %, "APAC": 35 %, "AMER": 25 % }
    /// </code></example>
    /// </summary>
    public static Dictionary<TKey, Percentual> ToPercentualDistribution<TKey>(
        this IEnumerable<KeyValuePair<TKey, decimal>> source,
        int? decimalPlaces = null)
        where TKey : notnull
    {
        var dict = source.ToDictionary(kv => kv.Key, kv => kv.Value);
        var total = dict.Values.Sum();
        if (total == 0m) throw new DivideByZeroException("Cannot distribute: total is zero.");
        return dict.ToDictionary(
            kv => kv.Key,
            kv => Percentual.FromPercentage(kv.Value / total * 100m, decimalPlaces));
    }

    /// <summary>
    /// Formats as "47.50 %" with the specified decimal-place override,
    /// without permanently changing the stored precision.
    /// </summary>
    public static string ToDisplay(this Percentual percent, int decimalPlaces)
        => percent.ToStringWithSymbol(decimalPlaces);

    /// <summary>
    /// Formats as a plain decimal fraction string for JSON / CSV export.
    /// <c>Percentual.FromPercentage(47.5m).ToFractionString("F4") → "0.4750"</c>
    /// </summary>
    public static string ToFractionString(this Percentual percent, string format = "G")
        => percent.ToFraction().ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the sign as a string prefix: "+", "−", or "" for zero.
    /// Useful for financial dashboards that colour-code changes.
    /// </summary>
    public static string SignPrefix(this Percentual percent)
        => percent.IsPositive ? "+" : percent.IsNegative ? "−" : "";

    /// <summary>
    /// Formats as a signed string with symbol: "+20.00 %", "−5.50 %", "0.00 %".
    /// </summary>
    public static string ToSignedString(this Percentual percent)
        => $"{percent.SignPrefix()}{percent.ToStringWithSymbol()}";
}