using System.Numerics;
using System.Collections.Concurrent;
using YFex.Helpers;

namespace YFex;

/// <summary>
/// A type with a non-boxing <see cref="RawValue"/> form — the single extension point that lets ANY value type take
/// part in validation bounds, rule situations, decision-table cells, JSON rows and the wire (Money, Mass, an app's
/// <c>Temperature</c>, a string-backed <c>Sku</c>…). No library keeps a list of supported types.
/// <code>
/// public readonly record struct Temperature(decimal Celsius) : IRawValue&lt;Temperature&gt;
/// {
///     public static RawType RawType { get; } = RawType.Define&lt;Temperature&gt;("temperature", ValueKind.Decimal);
///     public RawValue ToRaw() => RawValue.Typed(RawType, Celsius);
///     public static bool TryFromRaw(in RawValue raw, out Temperature value)
///     {
///         bool ok = raw.TryRead(RawType, out decimal c);
///         value = new(c);
///         return ok;
///     }
/// }
/// </code>
/// Declare <see cref="RawType"/> as an initialized get-only property (as above) so it is registered when the type
/// is first used.
/// </summary>
public interface IRawValue<TSelf> : IRawValue where TSelf : IRawValue<TSelf>
{
    /// <summary>Stable key + storage kind of the type (see <see cref="YFex.RawType.Define{T}"/>).</summary>
    static abstract RawType RawType { get; }

    RawType IRawValue.RawTypeOf() => TSelf.RawType;

    /// <summary>The value as a typed <see cref="RawValue"/> (<see cref="RawValue.Typed(YFex.RawType, decimal, byte)"/>).</summary>
    RawValue ToRaw();

    /// <summary>
    /// Reads a value of this type. Should accept values of this <see cref="RawType"/> and plain (untyped) values of its
    /// storage kind — <see cref="RawValue.TryRead(YFex.RawType, out decimal)"/> does both.
    /// </summary>
    static abstract bool TryFromRaw(in RawValue raw, out TSelf value);
}

/// <summary>
/// Non-generic base of <see cref="IRawValue{TSelf}"/> (implemented automatically): lets code that only knows <c>T</c>
/// as an unconstrained type parameter reach the type's <see cref="YFex.RawType"/> without reflection.
/// </summary>
public interface IRawValue
{
    /// <summary>The value's <see cref="YFex.RawType"/> (reading it registers the type).</summary>
    RawType RawTypeOf();
}

/// <summary>Formats a typed <see cref="RawValue"/> for messages (culture-aware).</summary>
public delegate bool RawFormatter(in RawValue value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format,
    IFormatProvider? provider);

/// <summary>
/// Identity of a value type inside <see cref="RawValue"/>: a stable <see cref="Key"/> (persisted in JSON rows, schemas
/// and editors), the primitive <see cref="Storage"/> kind and a 32-bit <see cref="Id"/> derived from the key (kept
/// inline in every typed <see cref="RawValue"/>). Primitives (<see cref="Decimal"/>, <see cref="Text"/>…) have
/// <see cref="Id"/> 0.
/// </summary>
public sealed class RawType
{
    private static readonly ConcurrentDictionary<uint, RawType> s_byId = new();
    private static readonly ConcurrentDictionary<string, RawType> s_byKey = new(StringComparer.Ordinal);
    private static readonly RawType[] s_primitives = CreatePrimitives();

    private RawType(string key, ValueKind storage, uint id, Type? clrType, RawFormatter? formatter, RawFormatter? masker = null)
    {
        Key = key;
        Storage = storage;
        Id = id;
        ClrType = clrType;
        Formatter = formatter;
        Masker = masker;
    }

    /// <summary>Stable name (<c>"money"</c>, <c>"mass"</c>, <c>"decimal"</c>).</summary>
    public string Key { get; }

    /// <summary>Primitive kind the value is stored as.</summary>
    public ValueKind Storage { get; }

    /// <summary>Hash of <see cref="Key"/>; 0 for primitives.</summary>
    public uint Id { get; }

    /// <summary>True for the built-in storage primitives (<see cref="Of"/>).</summary>
    public bool IsPrimitive => Id == 0;

    /// <summary>CLR type that declared it (null for primitives).</summary>
    public Type? ClrType { get; }

    /// <summary>True when the storage kind is a number (Int32, Int64, Decimal, Double).</summary>
    public bool IsNumeric => Storage is ValueKind.Int32 or ValueKind.Int64 or ValueKind.Decimal or ValueKind.Double;

    internal RawFormatter? Formatter { get; }

    /// <summary>
    /// True when the declaring type holds personal data (<c>[Sensitive]</c>, implemented by the YFex.Privacy generator as
    /// <see cref="YFex.Privacy.IClassifiedData"/>): texts formatted for logs or AI write its mask (localization ⚑LZ25).
    /// </summary>
    public bool IsSensitive => Masker is not null;

    internal RawFormatter? Masker { get; }

    public override string ToString() => Key;

    // ── Primitives ───────────────────────────────────────────────────────────

    public static RawType Bool => s_primitives[(int)ValueKind.Bool];
    public static RawType Int32 => s_primitives[(int)ValueKind.Int32];
    public static RawType Int64 => s_primitives[(int)ValueKind.Int64];
    public static RawType Decimal => s_primitives[(int)ValueKind.Decimal];
    public static RawType Double => s_primitives[(int)ValueKind.Double];
    public static RawType Text => s_primitives[(int)ValueKind.Text];
    public static RawType Date => s_primitives[(int)ValueKind.Date];
    public static RawType Time => s_primitives[(int)ValueKind.Time];
    public static RawType DateTime => s_primitives[(int)ValueKind.DateTime];
    public static RawType Guid => s_primitives[(int)ValueKind.Guid];
    public static RawType EnumCode => s_primitives[(int)ValueKind.EnumCode];

    /// <summary>The primitive type of a storage kind.</summary>
    public static RawType Of(ValueKind kind) => s_primitives[(int)kind];

    // ── Registry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Declares the raw type of <typeparamref name="T"/>. Called once, from <typeparamref name="T"/>'s
    /// <see cref="IRawValue{TSelf}.RawType"/> initializer. <paramref name="formatter"/> defaults to
    /// <see cref="IFormattable"/> of the value when <typeparamref name="T"/> implements it (boxes — pass
    /// <see cref="SpanFormatter{T}"/> for hot paths), else to the storage value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another type already uses <paramref name="key"/> or its hash.</exception>
    public static RawType Define<T>(string key, ValueKind storage, RawFormatter? formatter = null) where T : IRawValue<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (storage == ValueKind.None) throw new ArgumentOutOfRangeException(nameof(storage), "A raw type needs a storage kind.");

        uint id = IdOf(key);
        var type = new RawType(key, storage, id, typeof(T), formatter ?? DefaultFormatter<T>(), MaskerOf<T>());
        var existing = s_byId.GetOrAdd(id, type);
        if (!ReferenceEquals(existing, type)
            && (existing.Key != key || existing.Storage != storage || existing.ClrType != typeof(T)))
        {
            throw new InvalidOperationException(
                $"Raw type key '{key}' of {typeof(T)} collides with '{existing.Key}' of {existing.ClrType} (id {id:x8}).");
        }
        s_byKey.TryAdd(key, existing);
        RawConverterCache<T>.Converter ??= new TypedRawConverter<T>();
        return existing;
    }

    /// <summary>A registered type by id (null when unknown in this process — e.g. its type was never touched).</summary>
    public static RawType? Find(uint id) => id == 0 ? null : s_byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>A registered or primitive type by key (null when unknown).</summary>
    public static RawType? Find(string key)
    {
        if (s_byKey.TryGetValue(key, out var t)) return t;
        foreach (var p in s_primitives)
            if (p is not null && p.Key == key) return p;
        return null;
    }

    /// <summary>The id <see cref="Define{T}"/> assigns to <paramref name="key"/> (never 0).</summary>
    public static uint IdOf(string key)
    {
        ulong h = StableHash64.Hash(key.AsSpan());
        uint id = (uint)(h ^ (h >> 32));
        return id == 0 ? 1u : id;
    }

    /// <summary>Non-boxing formatter for <see cref="ISpanFormattable"/> types.</summary>
    public static RawFormatter SpanFormatter<T>() where T : IRawValue<T>, ISpanFormattable
        => static (in RawValue v, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        {
            if (T.TryFromRaw(in v, out var value)) return value.TryFormat(destination, out charsWritten, format, provider);
            charsWritten = 0;
            return false;
        };

    // ⚑LZ25: [Sensitive] value objects (IClassifiedData, generated by YFex.Privacy) write their display mask; a value that cannot
    // be read back writes the hidden form (fail closed). Only formatting for logs / AI goes through it.
    private static RawFormatter? MaskerOf<T>() where T : IRawValue<T>
    {
        if (!typeof(YFex.Privacy.IClassifiedData).IsAssignableFrom(typeof(T))) return null;
        return static (in RawValue v, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
            T.TryFromRaw(in v, out var value) && value is YFex.Privacy.IClassifiedData classified
                ? classified.TryMask(destination, out charsWritten)
                : YFex.Privacy.Masks.TryWriteHidden(destination, out charsWritten);
    }

    private static RawFormatter? DefaultFormatter<T>() where T : IRawValue<T>
    {
        if (!typeof(IFormattable).IsAssignableFrom(typeof(T))) return null;
        return static (in RawValue v, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        {
            charsWritten = 0;
            if (!T.TryFromRaw(in v, out var value) || value is not IFormattable f) return false;
            string text = f.ToString(format.IsEmpty ? null : format.ToString(), provider);
            if (!text.AsSpan().TryCopyTo(destination)) return false;
            charsWritten = text.Length;
            return true;
        };
    }

    private static RawType[] CreatePrimitives()
    {
        var all = new RawType[(int)ValueKind.EnumCode + 1];
        all[0] = new RawType("none", ValueKind.None, 0, null, null);
        all[(int)ValueKind.Bool] = new RawType("bool", ValueKind.Bool, 0, null, null);
        all[(int)ValueKind.Int32] = new RawType("int32", ValueKind.Int32, 0, null, null);
        all[(int)ValueKind.Int64] = new RawType("int64", ValueKind.Int64, 0, null, null);
        all[(int)ValueKind.Decimal] = new RawType("decimal", ValueKind.Decimal, 0, null, null);
        all[(int)ValueKind.Double] = new RawType("double", ValueKind.Double, 0, null, null);
        all[(int)ValueKind.Text] = new RawType("text", ValueKind.Text, 0, null, null);
        all[(int)ValueKind.Date] = new RawType("date", ValueKind.Date, 0, null, null);
        all[(int)ValueKind.Time] = new RawType("time", ValueKind.Time, 0, null, null);
        all[(int)ValueKind.DateTime] = new RawType("datetime", ValueKind.DateTime, 0, null, null);
        all[(int)ValueKind.Guid] = new RawType("guid", ValueKind.Guid, 0, null, null);
        all[(int)ValueKind.EnumCode] = new RawType("enum", ValueKind.EnumCode, 0, null, null);
        return all;
    }
}

/// <summary>
/// An amount the rule engine can accumulate, adjust, cap and split across lines (Money, Mass, Length, Quantity, an
/// app's own unit). Composed of the BCL generic-math operator interfaces — nothing to implement beyond them and
/// <see cref="IRawValue{TSelf}"/> (numeric storage). Plain <see cref="decimal"/>/<see cref="int"/>/<see cref="long"/>/
/// <see cref="double"/> are amounts too (the rules generator handles them without this interface).
/// </summary>
public interface IAmount<TSelf> : IRawValue<TSelf>,
    IAdditionOperators<TSelf, TSelf, TSelf>,
    ISubtractionOperators<TSelf, TSelf, TSelf>,
    IMultiplyOperators<TSelf, decimal, TSelf>,
    IComparisonOperators<TSelf, TSelf, bool>,
    IAdditiveIdentity<TSelf, TSelf>
    where TSelf : IAmount<TSelf>;

// ── Unconstrained bridge (used by RawValueConvert for code that only knows T) ──

internal abstract class RawConverter<T>
{
    public abstract RawType Type { get; }
    public abstract RawValue ToRaw(in T value);
    public abstract bool TryFromRaw(in RawValue raw, out T value);
}

internal sealed class TypedRawConverter<T> : RawConverter<T> where T : IRawValue<T>
{
    public override RawType Type => T.RawType;
    public override RawValue ToRaw(in T value) => value.ToRaw();
    public override bool TryFromRaw(in RawValue raw, out T value) => T.TryFromRaw(in raw, out value);
}

internal static class RawConverterCache<T>
{
    public static RawConverter<T>? Converter;

    /// <summary>
    /// The converter. A value type whose <see cref="IRawValue{TSelf}.RawType"/> was never read yet is registered by
    /// probing <c>default(T)</c> once (one box per type, no reflection — trim/AOT-safe). Reference types register when
    /// their <c>RawType</c> is first read.
    /// </summary>
    public static RawConverter<T>? Get()
    {
        var c = Converter;
        if (c is not null) return c;
        if (typeof(T).IsValueType && default(T) is IRawValue probe) probe.RawTypeOf();
        // The probe writes Converter only as a side effect of T's static initializer (RawType.Define). Native AOT inlines
        // the probe to a static read of a beforefieldinit type and does not treat the class-init call as a write, so a
        // plain re-read reuses the null loaded above (RawValue.From(0m).As<Money>() threw on first use). Force a fresh read.
        return Volatile.Read(ref Converter);
    }
}
