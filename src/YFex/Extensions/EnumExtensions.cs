using System.Collections.Frozen;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace YFex.Extensions;

public static class EnumExtensions
{
    /// <summary>
    /// Returns the <see cref="EnumCodeAttribute"/> code if present,
    /// otherwise the member name.
    ///
    /// <code>
    /// TipoEmpresa.Cliente.GetCode()  // → "C"
    /// TipoEmpresa.Vendedor.GetCode() // → "V"
    /// </code>
    /// </summary>
    public static string GetCode<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => EnumCache<TEnum>.GetCode(value);

    /// <summary>Returns <see langword="true"/> when this member carries an <see cref="EnumCodeAttribute"/>.</summary>
    public static bool HasCode<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => EnumCache<TEnum>.HasCode(value);

    /// <summary>
    /// Finds the enum member whose <see cref="EnumCodeAttribute"/> matches
    /// <paramref name="code"/>. Throws <see cref="ArgumentException"/> when
    /// no match is found.
    ///
    /// <code>
    /// TipoEmpresa tipo = EnumExtensions.FromCode&lt;TipoEmpresa&gt;("C");
    /// // → TipoEmpresa.Cliente
    /// </code>
    /// </summary>
    public static TEnum FromCode<TEnum>(
        string code,
        StringComparison comparison = StringComparison.Ordinal)
        where TEnum : struct, Enum
    {
        if (!TryFromCode<TEnum>(code, out var result, comparison))
            throw new ArgumentException(
                $"No member of {typeof(TEnum).Name} has code \"{code}\".",
                nameof(code));
        return result;
    }

    /// <summary>Non-throwing. Returns <see langword="null"/> when no match is found.</summary>
    public static TEnum? TryFromCode<TEnum>(
        string? code,
        StringComparison comparison = StringComparison.Ordinal)
        where TEnum : struct, Enum
        => TryFromCode<TEnum>(code, out var r, comparison) ? r : null;

    private static bool TryFromCode<TEnum>(
        string? code, out TEnum result, StringComparison comparison)
        where TEnum : struct, Enum
    {
        result = default;
        if (code is null) return false;
        return EnumCache<TEnum>.TryFromCode(code, comparison, out result);
    }

    /// <summary>Returns all (value → code) pairs. Useful for seeding lookup tables at startup.</summary>
    public static IReadOnlyDictionary<TEnum, string> GetCodeMap<TEnum>()
        where TEnum : struct, Enum
        => EnumCache<TEnum>.CodeMap;

    /// <summary>
    /// Returns the best available display label for this enum member.
    /// Resolution order:
    /// <list type="number">
    ///   <item><see cref="DisplayAttribute.GetName()"/> (supports resx localisation)</item>
    ///   <item><see cref="DescriptionAttribute.Description"/></item>
    ///   <item>The member name itself</item>
    /// </list>
    /// </summary>
    public static string GetLabel<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => EnumCache<TEnum>.GetLabel(value);

    /// <summary>Returns all (value, label) pairs — one call to build a dropdown.</summary>
    public static IReadOnlyList<(TEnum Value, string Label)> GetSelectList<TEnum>()
        where TEnum : struct, Enum
        => EnumCache<TEnum>.SelectList;

    /// <summary>
    /// Reinterprets the enum bits as <see cref="int"/> without boxing.
    /// Safe only for <c>int</c>-backed enums (the default underlying type).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => Unsafe.As<TEnum, int>(ref value);

    /// <summary>Safe only for <c>long</c>-backed enums.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToLong<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => Unsafe.As<TEnum, long>(ref value);

    /// <summary>Safe only for <c>byte</c>-backed enums.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => Unsafe.As<TEnum, byte>(ref value);

    /// <summary>General case — boxes, but works for any underlying type.</summary>
    public static object ToUnderlyingValue<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => Convert.ChangeType(value, Enum.GetUnderlyingType(typeof(TEnum)));

    // ── Internal helpers for [Flags] bitwise ops ─────────────────────────────
    // Named after what they do, not the CLR type they happen to use internally.

    /// <summary>
    /// Safely widens any enum to a <c>long</c> for bitwise arithmetic,
    /// regardless of underlying type (byte / short / int / long).
    /// The Unsafe.SizeOf branches are JIT constants — only one survives.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AsBits<TEnum>(ref TEnum value) where TEnum : struct, Enum
    {
        if (Unsafe.SizeOf<TEnum>() == 1) return Unsafe.As<TEnum, byte>(ref value);
        if (Unsafe.SizeOf<TEnum>() == 2) return Unsafe.As<TEnum, short>(ref value);
        if (Unsafe.SizeOf<TEnum>() == 4) return Unsafe.As<TEnum, int>(ref value);
        return Unsafe.As<TEnum, long>(ref value);
    }

    /// <summary>Narrows a <c>long</c> back to an enum, respecting its actual size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TEnum FromBits<TEnum>(long bits) where TEnum : struct, Enum
    {
        if (Unsafe.SizeOf<TEnum>() == 1) { byte v = (byte)bits; return Unsafe.As<byte, TEnum>(ref v); }
        if (Unsafe.SizeOf<TEnum>() == 2) { short v = (short)bits; return Unsafe.As<short, TEnum>(ref v); }
        if (Unsafe.SizeOf<TEnum>() == 4) { int v = (int)bits; return Unsafe.As<int, TEnum>(ref v); }
        return Unsafe.As<long, TEnum>(ref bits);
    }

    public static TEnum ToEnum<TEnum>(this string value, bool ignoreCase = true)
        where TEnum : struct, Enum
        => Enum.Parse<TEnum>(value, ignoreCase);

    public static TEnum? ToEnumOrNull<TEnum>(this string? value, bool ignoreCase = true)
        where TEnum : struct, Enum
        => value is not null && Enum.TryParse<TEnum>(value, ignoreCase, out var r) ? r : null;

    /// <summary>Converts an int to TEnum without boxing; throws for undefined values.</summary>
    public static TEnum ToEnum<TEnum>(this int value)
        where TEnum : struct, Enum
    {
        var result = Unsafe.As<int, TEnum>(ref value);
        if (!result.IsDefined())
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{value} is not defined in {typeof(TEnum).Name}.");
        return result;
    }

    public static TEnum? ToEnumOrNull<TEnum>(this int value)
        where TEnum : struct, Enum
    {
        var candidate = Unsafe.As<int, TEnum>(ref value);
        return candidate.IsDefined() ? candidate : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefined<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => Enum.IsDefined(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUndefined<TEnum>(this TEnum value)
        where TEnum : struct, Enum
        => !Enum.IsDefined(value);

    // ════════════════════════════════════════════════════════════════════════
    // [FLAGS] SUPPORT  —  uses AsBits/FromBits for safe bitwise ops
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Returns <see langword="true"/> when ALL bits of <paramref name="flag"/> are set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasAllFlags<TEnum>(this TEnum value, TEnum flag)
        where TEnum : struct, Enum
        => value.HasFlag(flag);  // JIT-intrinsified since .NET 5

    /// <summary>Returns <see langword="true"/> when ANY bit of <paramref name="flags"/> is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasAnyFlag<TEnum>(this TEnum value, TEnum flags)
        where TEnum : struct, Enum
        => (AsBits(ref value) & AsBits(ref flags)) != 0;

    /// <summary>Returns a new value with <paramref name="flag"/> set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum WithFlag<TEnum>(this TEnum value, TEnum flag)
        where TEnum : struct, Enum
        => FromBits<TEnum>(AsBits(ref value) | AsBits(ref flag));

    /// <summary>Returns a new value with <paramref name="flag"/> cleared.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum WithoutFlag<TEnum>(this TEnum value, TEnum flag)
        where TEnum : struct, Enum
        => FromBits<TEnum>(AsBits(ref value) & ~AsBits(ref flag));

    /// <summary>Returns a new value with <paramref name="flag"/> toggled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ToggleFlag<TEnum>(this TEnum value, TEnum flag)
        where TEnum : struct, Enum
        => FromBits<TEnum>(AsBits(ref value) ^ AsBits(ref flag));

    /// <summary>
    /// Decomposes a [Flags] value into its individual set bits.
    ///
    /// <code>
    /// (Permission.Read | Permission.Write).GetFlags()
    ///   → [Permission.Read, Permission.Write]
    /// </code>
    /// </summary>
    public static IEnumerable<TEnum> GetFlags<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        long v = AsBits(ref value);
        foreach (var defined in EnumCache<TEnum>.Values)
        {
            var d = defined;
            long f = AsBits(ref d);
            if (f != 0 && (v & f) == f) yield return defined;
        }
    }

    /// <summary>All defined values, cached — no allocation after first call.</summary>
    public static TEnum[] GetValues<TEnum>()
        where TEnum : struct, Enum
        => EnumCache<TEnum>.Values;

    /// <summary>
    /// Executes <paramref name="action"/> when the value matches, then
    /// returns the value — enables fluent chaining without breaking out of
    /// an expression.
    ///
    /// <code>
    /// order.Status
    ///      .When(Status.Failed,  s => logger.Error(s))
    ///      .When(Status.Shipped, s => notifier.Send(order));
    /// </code>
    /// </summary>
    public static TEnum When<TEnum>(
        this TEnum value, TEnum match, Action<TEnum> action)
        where TEnum : struct, Enum
    {
        if (EqualityComparer<TEnum>.Default.Equals(value, match))
            action(value);
        return value;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// CACHE  —  all reflection runs once per TEnum, results are immutable.
// ════════════════════════════════════════════════════════════════════════════

internal static class EnumCache<TEnum> where TEnum : struct, Enum
{
    internal static readonly TEnum[] Values = Enum.GetValues<TEnum>();

    // ── Code mapping ────────────────────────────────────────────────────────

    // Array for small enums (cache-local linear scan beats hashing for ≤ 15 items).
    private static readonly (TEnum Value, string Code)[] _codePairs = BuildCodePairs();

    // Two FrozenDictionaries cover both ordinal cases without runtime branching.
    // FrozenDictionary uses a perfect hash — O(1) with near-zero overhead.
    private static readonly FrozenDictionary<string, TEnum> _codeToValueOrdinal =
        _codePairs.ToFrozenDictionary(x => x.Code, x => x.Value, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, TEnum> _codeToValueIgnoreCase =
        _codePairs.ToFrozenDictionary(x => x.Code, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<TEnum, string> _codeByValue =
        _codePairs.ToFrozenDictionary(x => x.Value, x => x.Code);

    internal static readonly IReadOnlyDictionary<TEnum, string> CodeMap = _codeByValue;

    // ── Label mapping ────────────────────────────────────────────────────────

    private static readonly FrozenDictionary<TEnum, string> _labels = BuildLabels();

    internal static readonly IReadOnlyList<(TEnum Value, string Label)> SelectList =
        Values.Select(v => (v, GetLabel(v))).ToArray();

    // ── Accessors ────────────────────────────────────────────────────────────

    internal static string GetCode(TEnum value)
        => _codeByValue.TryGetValue(value, out var code) ? code : value.ToString();

    internal static bool HasCode(TEnum value)
        => _codeByValue.ContainsKey(value);

    internal static bool TryFromCode(string code, StringComparison comparison, out TEnum result)
    {
        // Use the pre-built FrozenDictionary for the two common comparisons.
        // Fall back to the array scan only for exotic comparisons (CurrentCulture etc.).
        var dict = comparison is StringComparison.OrdinalIgnoreCase
                               or StringComparison.InvariantCultureIgnoreCase
            ? _codeToValueIgnoreCase
            : _codeToValueOrdinal;

        if (dict.TryGetValue(code, out result)) return true;

        // Exotic comparison fallback (rare — no dedicated dict for these).
        if (comparison is not StringComparison.Ordinal
                       and not StringComparison.OrdinalIgnoreCase
                       and not StringComparison.InvariantCultureIgnoreCase)
        {
            foreach (var (value, c) in _codePairs)
            {
                if (string.Equals(c, code, comparison))
                {
                    result = value;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    internal static string GetLabel(TEnum value)
        => _labels.TryGetValue(value, out var l) ? l : value.ToString();

    private static (TEnum Value, string Code)[] BuildCodePairs()
    {
        var pairs = new List<(TEnum, string)>(Values.Length);
        foreach (var value in Values)
        {
            var field = typeof(TEnum).GetField(value.ToString());
            var attr = field?.GetCustomAttribute<EnumCodeAttribute>();
            if (attr is not null) pairs.Add((value, attr.Code));
        }
        return [.. pairs];
    }

    private static FrozenDictionary<TEnum, string> BuildLabels()
    {
        var dict = new Dictionary<TEnum, string>(Values.Length);
        foreach (var value in Values)
        {
            var field = typeof(TEnum).GetField(value.ToString());

            // [Display] first — supports ResourceType for .resx localisation.
            var display = field?.GetCustomAttribute<DisplayAttribute>();
            if (display?.GetName() is { } name)
            {
                dict[value] = name;
                continue;
            }

            // [Description] as legacy fallback.
            var desc = field?.GetCustomAttribute<DescriptionAttribute>();
            dict[value] = desc?.Description ?? value.ToString();
        }
        return dict.ToFrozenDictionary();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ATTRIBUTE
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Attaches a string code to an enum member for use as a surrogate key in
/// databases, REST APIs, EDI messages, or any external system that cannot
/// use the integer underlying value.
///
/// <code>
/// public enum TipoEmpresa
/// {
///     [EnumCode("C")]  Cliente,
///     [EnumCode("P")]  Prospect,
///     [EnumCode("F")]  Fornecedor,
/// }
///
/// TipoEmpresa.Cliente.GetCode()         // → "C"
/// EnumExtensions.FromCode&lt;TipoEmpresa&gt;("C")  // → TipoEmpresa.Cliente
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class EnumCodeAttribute(string code) : Attribute
{
    public string Code { get; } = code ?? throw new ArgumentNullException(nameof(code));
}

// ════════════════════════════════════════════════════════════════════════════
// EF CORE VALUE CONVERTER  (place in Infrastructure / Data project)
//
//   modelBuilder.Entity<Empresa>()
//       .Property(e => e.Tipo)
//       .HasConversion(new EnumCodeConverter<TipoEmpresa>())
//       .HasMaxLength(1);
//
//   // Or globally for every property of that enum type:
//   modelBuilder.Properties<TipoEmpresa>()
//       .HaveConversion<EnumCodeConverter<TipoEmpresa>>();
// ════════════════════════════════════════════════════════════════════════════

/*
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class EnumCodeConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public EnumCodeConverter()
        : base(
            v => v.GetCode(),
            s => EnumExtensions.FromCode<TEnum>(s))
    { }
}
*/