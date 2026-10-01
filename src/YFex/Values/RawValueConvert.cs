using System.Runtime.CompilerServices;

namespace YFex;

/// <summary>
/// Generic, non-boxing bridge between a scalar <typeparamref name="T"/> and <see cref="RawValue"/> for code that only
/// knows <c>T</c> as a type parameter (rule parameters, table cells, limit bounds, generated (de)serializers).
/// Primitives are <c>typeof(T) ==</c> chains (JIT/AOT-time constants, one branch per instantiation); every other type
/// goes through its <see cref="IRawValue{TSelf}"/> implementation — there is no list of supported semantic types.
/// Enums are NOT handled here (they need <c>where T : Enum</c> for their code) — use
/// <see cref="RawValue.FromEnum{TEnum}"/> / <see cref="RawValue.TryGetEnum{TEnum}"/>.
/// </summary>
public static class RawValueConvert
{
    /// <summary><see cref="RawValue"/> of <paramref name="value"/>; <see cref="RawValue.None"/> for unsupported types.</summary>
    public static RawValue From<T>(T value)
    {
        if (typeof(T) == typeof(int)) return RawValue.From(Unsafe.As<T, int>(ref value));
        if (typeof(T) == typeof(long)) return RawValue.From(Unsafe.As<T, long>(ref value));
        if (typeof(T) == typeof(decimal)) return RawValue.From(Unsafe.As<T, decimal>(ref value));
        if (typeof(T) == typeof(double)) return RawValue.From(Unsafe.As<T, double>(ref value));
        if (typeof(T) == typeof(float)) return RawValue.From((double)Unsafe.As<T, float>(ref value));
        if (typeof(T) == typeof(short)) return RawValue.From((int)Unsafe.As<T, short>(ref value));
        if (typeof(T) == typeof(byte)) return RawValue.From((int)Unsafe.As<T, byte>(ref value));
        if (typeof(T) == typeof(bool)) return RawValue.From(Unsafe.As<T, bool>(ref value));
        if (typeof(T) == typeof(DateOnly)) return RawValue.From(Unsafe.As<T, DateOnly>(ref value));
        if (typeof(T) == typeof(TimeOnly)) return RawValue.From(Unsafe.As<T, TimeOnly>(ref value));
        if (typeof(T) == typeof(DateTime)) return RawValue.From(Unsafe.As<T, DateTime>(ref value));
        if (typeof(T) == typeof(Guid)) return RawValue.From(Unsafe.As<T, Guid>(ref value));
        if (typeof(T) == typeof(RawValue)) return Unsafe.As<T, RawValue>(ref value);
        if (typeof(T) == typeof(string)) return RawValue.From(Unsafe.As<T, string?>(ref value));
        return RawConverterCache<T>.Get() is { } c ? c.ToRaw(in value) : RawValue.None;
    }

    /// <summary>True when <typeparamref name="T"/> has a raw form (a primitive or an <see cref="IRawValue{TSelf}"/> type).</summary>
    public static bool IsSupported<T>()
        => typeof(T) == typeof(int) || typeof(T) == typeof(long) || typeof(T) == typeof(decimal) || typeof(T) == typeof(double)
        || typeof(T) == typeof(float) || typeof(T) == typeof(short) || typeof(T) == typeof(byte) || typeof(T) == typeof(bool)
        || typeof(T) == typeof(DateOnly) || typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(DateTime) || typeof(T) == typeof(Guid)
        || typeof(T) == typeof(RawValue) || typeof(T) == typeof(string) || RawConverterCache<T>.Get() is not null;

    /// <summary>The <see cref="RawType"/> of <typeparamref name="T"/> (null when unsupported).</summary>
    public static RawType? TypeOf<T>()
    {
        if (typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte)) return RawType.Int32;
        if (typeof(T) == typeof(long)) return RawType.Int64;
        if (typeof(T) == typeof(decimal)) return RawType.Decimal;
        if (typeof(T) == typeof(double) || typeof(T) == typeof(float)) return RawType.Double;
        if (typeof(T) == typeof(bool)) return RawType.Bool;
        if (typeof(T) == typeof(DateOnly)) return RawType.Date;
        if (typeof(T) == typeof(TimeOnly)) return RawType.Time;
        if (typeof(T) == typeof(DateTime)) return RawType.DateTime;
        if (typeof(T) == typeof(Guid)) return RawType.Guid;
        if (typeof(T) == typeof(string)) return RawType.Text;
        return RawConverterCache<T>.Get()?.Type;
    }

    /// <summary>Typed read of <paramref name="raw"/> as <typeparamref name="T"/> (numeric widening as in the typed <c>TryGet</c>s).</summary>
    public static bool TryGet<T>(in RawValue raw, out T value)
    {
        value = default!;
        if (typeof(T) == typeof(int)) return raw.TryGet(out Unsafe.As<T, int>(ref value));
        if (typeof(T) == typeof(long)) return raw.TryGet(out Unsafe.As<T, long>(ref value));
        if (typeof(T) == typeof(decimal)) return raw.TryGet(out Unsafe.As<T, decimal>(ref value));
        if (typeof(T) == typeof(double)) return raw.TryGet(out Unsafe.As<T, double>(ref value));
        if (typeof(T) == typeof(float))
        {
            if (!raw.TryGet(out double d)) return false;
            Unsafe.As<T, float>(ref value) = (float)d;
            return true;
        }
        if (typeof(T) == typeof(short))
        {
            if (!raw.TryGet(out int i) || i < short.MinValue || i > short.MaxValue) return false;
            Unsafe.As<T, short>(ref value) = (short)i;
            return true;
        }
        if (typeof(T) == typeof(byte))
        {
            if (!raw.TryGet(out int i) || i < byte.MinValue || i > byte.MaxValue) return false;
            Unsafe.As<T, byte>(ref value) = (byte)i;
            return true;
        }
        if (typeof(T) == typeof(bool)) return raw.TryGet(out Unsafe.As<T, bool>(ref value));
        if (typeof(T) == typeof(DateOnly)) return raw.TryGet(out Unsafe.As<T, DateOnly>(ref value));
        if (typeof(T) == typeof(TimeOnly)) return raw.TryGet(out Unsafe.As<T, TimeOnly>(ref value));
        if (typeof(T) == typeof(DateTime)) return raw.TryGet(out Unsafe.As<T, DateTime>(ref value));
        if (typeof(T) == typeof(Guid)) return raw.TryGet(out Unsafe.As<T, Guid>(ref value));
        if (typeof(T) == typeof(RawValue)) { Unsafe.As<T, RawValue>(ref value) = raw; return true; }
        if (typeof(T) == typeof(string)) return raw.TryGet(out Unsafe.As<T, string?>(ref value));
        return RawConverterCache<T>.Get() is { } c && c.TryFromRaw(in raw, out value);
    }

    /// <summary><see cref="TryGet{T}"/> or <c>default</c>.</summary>
    public static T? To<T>(in RawValue raw) => TryGet(in raw, out T value) ? value : default;
}

/// <summary>
/// Reads parts serialized before semantic types moved to <see cref="RawType"/> (format of 2026-09-30 and earlier:
/// kinds 11–15 were Money/Percentual/Quantity/Mass/Length, 16 EnumCode). For persisted-data/wire readers only.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class RawValueLegacy
{
    public static RawValue FromV1Parts(byte kind, ulong low, ulong high, string? text, byte aux)
    {
        RawType? unit = kind switch
        {
            11 => Money.RawType,
            12 => Percentual.RawType,
            13 => Quantity.RawType,
            14 => Mass.RawType,
            15 => Length.RawType,
            _ => null,
        };
        if (unit is not null) return RawValue.FromParts(ValueKind.Decimal, low, high, null, aux, unit.Id);
        if (kind == 16) return RawValue.FromParts(ValueKind.EnumCode, low, high, text, aux, 0);
        if (kind > 16) return RawValue.None;                       // v1 Custom values have no v2 equivalent
        return RawValue.FromParts((ValueKind)kind, low, high, text, aux, 0);
    }
}
