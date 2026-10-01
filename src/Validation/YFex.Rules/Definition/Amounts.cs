namespace YFex.Rules;

/// <summary>
/// Bridge between a rule set's amount type (any <see cref="IAmount{TSelf}"/> — Money, Mass, Length… — or a plain
/// decimal/int/long/double) and the engine, which computes in <see cref="decimal"/> (exact, deterministic, one rounding
/// policy). Used by generated code and the engine; apps read results as typed <see cref="RawValue"/>s
/// (<c>decision.Totals.Final.As&lt;Mass&gt;()</c>).
/// </summary>
public static class Amounts
{
    /// <summary>Decimal storage of <paramref name="amount"/> (Money → its amount, Mass → grams, int → the number).</summary>
    public static decimal ToDecimal<T>(T amount)
        => RawValueConvert.From(amount).TryGet(out decimal d)
            ? d
            : throw new InvalidOperationException($"{typeof(T).Name} is not an amount (an IAmount<T> type or a number).");

    /// <summary>The <see cref="RawType"/> of amount type <typeparamref name="T"/> (numeric storage required).</summary>
    public static RawType TypeOf<T>()
    {
        var t = RawValueConvert.TypeOf<T>();
        return t is { IsNumeric: true }
            ? t
            : throw new InvalidOperationException($"{typeof(T).Name} is not an amount (an IAmount<T> type or a number).");
    }

    /// <summary><paramref name="value"/> as a value of amount type <paramref name="type"/>.</summary>
    public static RawValue ToRaw(decimal value, RawType type) => type.Storage switch
    {
        ValueKind.Decimal => type.IsPrimitive ? RawValue.From(value) : RawValue.Typed(type, value),
        ValueKind.Int64 => type.IsPrimitive ? RawValue.From((long)value) : RawValue.Typed(type, (long)value),
        ValueKind.Int32 => RawValue.From((int)value).WithType(type),
        ValueKind.Double => type.IsPrimitive ? RawValue.From((double)value) : RawValue.Typed(type, (double)value),
        _ => throw new InvalidOperationException($"Raw type '{type.Key}' is not numeric."),
    };

    /// <summary>Typed amount from engine decimal storage.</summary>
    public static T FromDecimal<T>(decimal value, RawType type)
    {
        var raw = ToRaw(value, type);
        return RawValueConvert.TryGet(in raw, out T typed)
            ? typed
            : throw new InvalidOperationException($"The rule set's amount is {type.Key}, not {typeof(T).Name}.");
    }

    /// <summary>Integer amount types round to whole units whatever the rule set's policy says.</summary>
    internal static RoundingPolicy EffectiveRounding(RawType type, RoundingPolicy rounding)
        => type.Storage is ValueKind.Int32 or ValueKind.Int64 && rounding.Decimals != 0 ? new RoundingPolicy(0, rounding.Mode) : rounding;
}
