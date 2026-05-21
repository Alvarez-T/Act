using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace YFex.Extensions;

public static class MatchExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="value"/> equals any
    /// of the supplied <paramref name="options"/>.
    ///
    /// <code>
    /// status.IsAnyOf(Status.Active, Status.Pending)
    /// day.IsAnyOf(DayOfWeek.Saturday, DayOfWeek.Sunday)
    /// </code>
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ReadOnlySpan{T}"/> (C# 13 params span) so no array is
    /// heap-allocated at the call site.  Falls back to
    /// <see cref="EqualityComparer{T}.Default"/> which is inlined for most
    /// primitives by the JIT.
    /// </remarks>
    public static bool IsAnyOf<T>(this T value, params ReadOnlySpan<T> options)
    {
        // ReadOnlySpan<T> can never be null — guard removed intentionally.
        var comparer = EqualityComparer<T>.Default;
        foreach (ref readonly var option in options)
        {
            if (comparer.Equals(value, option))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="IsAnyOf{T}"/> with a custom <paramref name="comparer"/>.
    /// Useful for case-insensitive string checks.
    ///
    /// <code>
    /// role.IsAnyOf(StringComparer.OrdinalIgnoreCase, "admin", "superuser")
    /// </code>
    /// </summary>
    public static bool IsAnyOf<T>(
        this T value,
        IEqualityComparer<T> comparer,
        params ReadOnlySpan<T> options)
    {
        foreach (ref readonly var option in options)
        {
            if (comparer.Equals(value, option))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="value"/> is NOT any
    /// of the supplied <paramref name="options"/>.
    /// </summary>
    public static bool IsNoneOf<T>(this T value, params ReadOnlySpan<T> options)
        => !value.IsAnyOf(options);

    /// <inheritdoc cref="IsNoneOf{T}(T, ReadOnlySpan{T})"/>
    public static bool IsNoneOf<T>(
        this T value,
        IEqualityComparer<T> comparer,
        params ReadOnlySpan<T> options)
        => !value.IsAnyOf(comparer, options);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="value"/> is contained
    /// in <paramref name="set"/>. Prefer this over <see cref="IsAnyOf{T}"/>
    /// when the candidate set is built once and queried many times — a
    /// <see cref="FrozenSet{T}"/> look-up is O(1) with no allocation.
    ///
    /// <code>
    /// private static readonly FrozenSet&lt;string&gt; ValidCodes =
    ///     FrozenSet.Create("USD", "EUR", "GBP");
    ///
    /// currency.IsIn(ValidCodes)
    /// </code>
    /// </summary>
    public static bool IsIn<T>(this T value, FrozenSet<T> set)
        => set.Contains(value);

    /// <summary>
    /// Convenience overload for any <see cref="IReadOnlySet{T}"/>
    /// (<see cref="HashSet{T}"/>, <see cref="ImmutableHashSet{T}"/>, etc.).
    /// </summary>
    public static bool IsIn<T>(this T value, IReadOnlySet<T> set)
        => set.Contains(value);

    /// <summary>
    /// Returns <see langword="true"/> when every element in
    /// <paramref name="values"/> is equal to every other element.
    /// An empty or single-element span is trivially <see langword="true"/>.
    ///
    /// <code>
    /// // All three discounts are the same?
    /// AreAllEqual(discount1, discount2, discount3)
    /// </code>
    /// </summary>
    /// <remarks>
    /// Note: this is a <em>static</em> utility — not an extension — because
    /// there is no meaningful "receiver" when checking a group of peers.
    /// The original extension form (<c>value.AreAllEqual(…)</c>) implied
    /// the receiver was special when it was not.
    /// </remarks>
    public static bool AreAllEqual<T>(params ReadOnlySpan<T> values)
    {
        if (values.Length <= 1) return true;

        var comparer = EqualityComparer<T>.Default;
        ref readonly var first = ref values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (!comparer.Equals(values[i], first))
                return false;
        }
        return true;
    }

    /// <inheritdoc cref="AreAllEqual{T}(ReadOnlySpan{T})"/>
    public static bool AreAllEqual<T>(IEqualityComparer<T> comparer, params ReadOnlySpan<T> values)
    {
        if (values.Length <= 1) return true;
        ref readonly var first = ref values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (!comparer.Equals(values[i], first))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when no two elements in
    /// <paramref name="values"/> are equal.  Uses a stack-allocated
    /// approach for small spans (≤ 8 elements) and a
    /// <see cref="HashSet{T}"/> for larger ones to stay O(n).
    /// </summary>
    public static bool AreAllDistinct<T>(params ReadOnlySpan<T> values)
    {
        if (values.Length <= 1) return true;

        var comparer = EqualityComparer<T>.Default;

        // O(n²) but allocation-free for the common small-span case.
        if (values.Length <= 8)
        {
            for (int i = 0; i < values.Length - 1; i++)
                for (int j = i + 1; j < values.Length; j++)
                {
                    if (comparer.Equals(values[i], values[j]))
                        return false;
                }
            return true;
        }

        // O(n) with a HashSet for larger inputs.
        var seen = new HashSet<T>(values.Length, comparer);
        foreach (ref readonly var v in values)
        {
            if (!seen.Add(v)) return false;
        }
        return true;
    }

    /// <summary>
    /// Null-safe equality check.  Returns <see langword="false"/> when
    /// <paramref name="value"/> is <see langword="null"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EqualsTo<T>(this T? value, T other)
        where T : class
        => value is not null && EqualityComparer<T>.Default.Equals(value, other);

    // ════════════════════════════════════════════════════════════════════════
    // Pattern matching helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <see langword="true"/> when the string matches
    /// <paramref name="pattern"/> (case-insensitive by default).
    ///
    /// <code>
    /// email.MatchesPattern(@"^[\w.+-]+@[\w-]+\.[a-z]{2,}$")
    /// </code>
    /// </summary>
    public static bool MatchesPattern(
        this string? value,
        [StringSyntax(StringSyntaxAttribute.Regex)] string pattern,
        RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    {
        if (value is null) return false;
        return Regex.IsMatch(value, pattern, options);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the string matches the supplied
    /// pre-compiled <paramref name="regex"/> — use this on hot paths to avoid
    /// regex recompilation.
    /// </summary>
    public static bool MatchesPattern(this string? value, Regex regex)
        => value is not null && regex.IsMatch(value);

    // ════════════════════════════════════════════════════════════════════════
    // Switch / dispatch helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes <paramref name="action"/> when <paramref name="value"/> equals
    /// any of <paramref name="options"/>; returns <see langword="true"/> if
    /// it fired.
    ///
    /// <code>
    /// status.WhenAnyOf([Status.Failed, Status.Cancelled], s => logger.Warn(s))
    /// </code>
    /// </summary>
    public static bool WhenAnyOf<T>(
        this T value,
        ReadOnlySpan<T> options,
        Action<T> action)
    {
        if (!value.IsAnyOf(options)) return false;
        action(value);
        return true;
    }

    /// <summary>
    /// Projects <paramref name="value"/> through <paramref name="selector"/>
    /// when it matches any option; otherwise returns <paramref name="fallback"/>.
    ///
    /// <code>
    /// var label = status.MatchAnyOf(
    ///     [Status.Active, Status.Pending],
    ///     s => s.ToString().ToLower(),
    ///     fallback: "inactive");
    /// </code>
    /// </summary>
    public static TResult MatchAnyOf<T, TResult>(
        this T value,
        ReadOnlySpan<T> options,
        Func<T, TResult> selector,
        TResult fallback = default!)
        => value.IsAnyOf(options) ? selector(value) : fallback;

    // ════════════════════════════════════════════════════════════════════════
    // C# 14 extension block — adds IsAnyOf / IsNoneOf directly onto every T
    // so the call reads: value.IsAnyOf(a, b, c) without importing a static class.
    //
    // Requires: <LangVersion>14</LangVersion> in .csproj  (.NET 10+)
    // ════════════════════════════════════════════════════════════════════════

    // NOTE: uncomment the block below when targeting .NET 10 / C# 14.
    // The static methods above remain as the fallback for older TFMs.

    /*
    extension<T>(T value)
    {
        public bool IsAnyOf(params ReadOnlySpan<T> options)
            => MatchExtensions.IsAnyOf(value, options);

        public bool IsNoneOf(params ReadOnlySpan<T> options)
            => MatchExtensions.IsNoneOf(value, options);

        public bool IsBetween(T min, T max) where T : IComparable<T>
            => MatchExtensions.IsBetween(value, min, max);
    }
    */
}