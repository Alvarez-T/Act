using System.Globalization;
using System.Runtime.CompilerServices;
using YFex.Extensions;
using YFex.Helpers;

namespace YFex;

/// <summary>
/// Non-boxing tagged scalar (32 bytes). Used at every boundary that needs "a value of some
/// scalar kind" without <see cref="object"/>: rule arguments, numeric/date bounds in field
/// constraints, situation field reads by id, decision-table cells, Composer values, AI tool I/O.
/// <para>
/// A value is a primitive <see cref="Kind"/> (storage) plus, for semantic types, the <see cref="TypeId"/> of the
/// <see cref="RawType"/> declared by the type itself (<see cref="IRawValue{TSelf}"/>): Money, Mass, an app's
/// <c>Temperature</c>… are all "Decimal + type". Layout: a 16-byte <see cref="Payload16"/> written with
/// <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/> for long/double/decimal/DateTime/Guid; strings go to the reference
/// slot (Text, and the code of an <see cref="ValueKind.EnumCode"/>); one byte of storage detail (<see cref="Aux"/>,
/// e.g. <see cref="Percentual.DecimalPlaces"/>) and the 32-bit type id use the struct's padding.
/// </para>
/// <code>
/// RawValue.From(80); RawValue.From(new Money(1000m)); RawValue.FromEnum(PaymentMethod.Pix);
/// if (v.TryGet(out decimal d)) …   if (v.TryGet(out Money m)) …   Mass w = v.As&lt;Mass&gt;();
/// </code>
/// Pass by <c>in</c> on hot paths.
/// </summary>
public readonly struct RawValue : IEquatable<RawValue>, IComparable<RawValue>, ISpanFormattable
{
    private readonly Payload16 _raw;
    private readonly string? _text;
    private readonly ValueKind _kind;
    private readonly byte _aux;
    private readonly uint _type;

    private RawValue(ValueKind kind, Payload16 raw, string? text = null, byte aux = 0, uint type = 0)
    {
        _kind = kind;
        _raw = raw;
        _text = text;
        _aux = aux;
        _type = type;
    }

    /// <summary>The storage kind. <see cref="ValueKind.None"/> for <c>default</c>.</summary>
    public ValueKind Kind => _kind;

    /// <summary><see cref="RawType.Id"/> of a typed value; 0 for plain primitives.</summary>
    public uint TypeId => _type;

    /// <summary>True for values of a declared <see cref="RawType"/> (Money, Mass…), false for plain primitives.</summary>
    public bool IsTyped => _type != 0;

    /// <summary>The value's type: the declared one (null when not registered in this process) or the primitive.</summary>
    public RawType? Type => _type == 0 ? RawType.Of(_kind) : RawType.Find(_type);

    /// <summary>One byte of storage detail owned by the type (Percentual keeps its decimal places here).</summary>
    public byte Aux => _aux;

    /// <summary>True for <c>default(RawValue)</c> — "no value" (unset bound, missing argument).</summary>
    public bool IsNone => _kind == ValueKind.None;

    /// <summary>"No value".</summary>
    public static RawValue None => default;

    // ── Parts (serializers) ──────────────────────────────────────────────────

    /// <summary>
    /// Low-level parts for serializers (RPC wire formats, persistence): <see cref="FromParts"/> of the values returned
    /// by <see cref="GetParts"/> rebuilds an identical value for every kind and type.
    /// </summary>
    public void GetParts(out ValueKind kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type)
    {
        kind = _kind;
        low = _raw[0];
        high = _raw[1];
        text = _text;
        aux = _aux;
        type = _type;
    }

    /// <summary>Rebuilds a value from <see cref="GetParts"/> output (no validation — only feed it serialized parts).</summary>
    public static RawValue FromParts(ValueKind kind, ulong low, ulong high, string? text, byte aux, uint type)
    {
        Payload16 raw = default;
        raw[0] = low;
        raw[1] = high;
        return new RawValue(kind, raw, text, aux, type);
    }

    // ── Factories ────────────────────────────────────────────────────────────

    public static RawValue From(bool value) => new(ValueKind.Bool, Payload16.Of(value ? 1L : 0L));
    public static RawValue From(int value) => new(ValueKind.Int32, Payload16.Of((long)value));
    public static RawValue From(long value) => new(ValueKind.Int64, Payload16.Of(value));
    public static RawValue From(decimal value) => new(ValueKind.Decimal, Payload16.Of(value));
    public static RawValue From(double value) => new(ValueKind.Double, Payload16.Of(value));
    public static RawValue From(DateOnly value) => new(ValueKind.Date, Payload16.Of((long)value.DayNumber));
    public static RawValue From(TimeOnly value) => new(ValueKind.Time, Payload16.Of(value.Ticks));
    public static RawValue From(DateTime value) => new(ValueKind.DateTime, Payload16.Of(value));
    public static RawValue From(Guid value) => new(ValueKind.Guid, Payload16.Of(value));

    /// <summary>Text value. <see langword="null"/> gives <see cref="None"/>.</summary>
    public static RawValue From(string? value) => value is null ? default : new(ValueKind.Text, default, value);

    /// <summary>Any <see cref="IRawValue{TSelf}"/> type (Money, Mass, app types) — non-boxing.</summary>
    public static RawValue From<T>(T value) where T : IRawValue<T> => value.ToRaw();

    /// <summary>
    /// Stores the enum's underlying value plus its stable code
    /// (<see cref="EnumExtensions.GetCode{TEnum}"/> — the <c>[EnumCode]</c> if present, otherwise the
    /// member name). Formatting and wire use the code, so renames of members don't break stored data.
    /// </summary>
    public static RawValue FromEnum<TEnum>(TEnum value) where TEnum : unmanaged, Enum
        => new(ValueKind.EnumCode, Payload16.Of(EnumToInt64(value)), value.GetCode());

    // Typed factories — used by IRawValue implementations.

    /// <summary>Decimal-stored value of <paramref name="type"/> (<paramref name="aux"/> = the type's storage detail).</summary>
    public static RawValue Typed(RawType type, decimal value, byte aux = 0)
        => new(ValueKind.Decimal, Payload16.Of(value), null, aux, CheckStorage(type, ValueKind.Decimal));

    /// <summary>Int64-stored value of <paramref name="type"/>.</summary>
    public static RawValue Typed(RawType type, long value)
        => new(ValueKind.Int64, Payload16.Of(value), null, 0, CheckStorage(type, ValueKind.Int64));

    /// <summary>Double-stored value of <paramref name="type"/>.</summary>
    public static RawValue Typed(RawType type, double value)
        => new(ValueKind.Double, Payload16.Of(value), null, 0, CheckStorage(type, ValueKind.Double));

    /// <summary>Text-stored value of <paramref name="type"/> (null gives <see cref="None"/>).</summary>
    public static RawValue Typed(RawType type, string? value)
        => value is null ? default : new(ValueKind.Text, default, value, 0, CheckStorage(type, ValueKind.Text));

    /// <summary>Guid-stored value of <paramref name="type"/>.</summary>
    public static RawValue Typed(RawType type, Guid value)
        => new(ValueKind.Guid, Payload16.Of(value), null, 0, CheckStorage(type, ValueKind.Guid));

    /// <summary>
    /// This value re-tagged as <paramref name="type"/> (plain numbers are converted to its numeric storage; a primitive
    /// type strips the tag). <see cref="None"/> stays None.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value can't be stored as <paramref name="type"/>.</exception>
    public RawValue WithType(RawType type)
    {
        if (_kind == ValueKind.None) return this;
        if (type.Id == _type && type.Storage == _kind) return this;
        if (_type != 0 && !type.IsPrimitive)
            throw new InvalidOperationException($"A {Type?.Key ?? _type.ToString("x8")} value is not a {type.Key}.");
        if (type.Storage == _kind) return new RawValue(_kind, _raw, _text, _aux, type.Id);
        switch (type.Storage)
        {
            case ValueKind.Decimal when TryGetExactDecimal(out decimal d):
                return new RawValue(ValueKind.Decimal, Payload16.Of(d), null, 0, type.Id);
            case ValueKind.Int64 when TryGetInteger(out long l):
                return new RawValue(ValueKind.Int64, Payload16.Of(l), null, 0, type.Id);
            case ValueKind.Double when TryGet(out double f):
                return new RawValue(ValueKind.Double, Payload16.Of(f), null, 0, type.Id);
        }
        throw new InvalidOperationException($"A {_kind} value can't be stored as {type.Key} ({type.Storage}).");
    }

    /// <summary>The same storage value without a type tag.</summary>
    public RawValue Untyped => _type == 0 ? this : new RawValue(_kind, _raw, _text, _aux);

    /// <summary>True when the value is of <paramref name="type"/>, or a plain value it accepts (a number for a numeric type).</summary>
    public bool Fits(RawType type)
    {
        if (_kind == ValueKind.None) return false;
        if (_type != 0) return _type == type.Id;
        return type.IsNumeric ? IsNumeric(_kind) : type.Storage == _kind;
    }

    // ── Typed reads (no boxing) ──────────────────────────────────────────────

    /// <summary>Reads an <see cref="IRawValue{TSelf}"/> type (Money, Mass, app types).</summary>
    public bool TryGet<T>(out T value) where T : IRawValue<T> => T.TryFromRaw(in this, out value);

    /// <summary>
    /// Reads the value as <typeparamref name="T"/> — any <see cref="IRawValue{TSelf}"/> type or primitive
    /// (<c>As&lt;Money&gt;()</c>, <c>As&lt;int&gt;()</c>); non-boxing (<see cref="RawValueConvert.TryGet{T}"/>).
    /// </summary>
    /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
    public T As<T>()
        => RawValueConvert.TryGet(in this, out T value)
            ? value
            : throw new InvalidCastException($"{Describe()} is not a {RawValueConvert.TypeOf<T>()?.Key ?? typeof(T).Name}.");

    /// <summary>Storage of a value that <see cref="Fits"/> <paramref name="type"/>, as decimal (for IRawValue implementations).</summary>
    public bool TryRead(RawType type, out decimal value)
    {
        if (Fits(type) && TryGetExactDecimal(out value)) return true;
        value = 0;
        return false;
    }

    /// <summary>Storage of a value that <see cref="Fits"/> <paramref name="type"/>, as Int64.</summary>
    public bool TryRead(RawType type, out long value)
    {
        if (Fits(type) && TryGetInteger(out value)) return true;
        value = 0;
        return false;
    }

    /// <summary>Storage of a value that <see cref="Fits"/> <paramref name="type"/>, as double.</summary>
    public bool TryRead(RawType type, out double value)
    {
        if (Fits(type) && TryGet(out value)) return true;
        value = 0;
        return false;
    }

    /// <summary>Storage of a value that <see cref="Fits"/> <paramref name="type"/>, as text.</summary>
    public bool TryRead(RawType type, out string? value)
    {
        bool ok = Fits(type) && _kind == ValueKind.Text;
        value = ok ? _text : null;
        return ok;
    }

    /// <summary>Storage of a value that <see cref="Fits"/> <paramref name="type"/>, as Guid.</summary>
    public bool TryRead(RawType type, out Guid value)
    {
        bool ok = Fits(type) && _kind == ValueKind.Guid;
        value = ok ? _raw.Read<Guid>() : default;
        return ok;
    }

    public bool TryGet(out bool value)
    {
        value = _kind == ValueKind.Bool && _raw.Read<long>() != 0;
        return _kind == ValueKind.Bool;
    }

    /// <summary>Reads Int32, or Int64 when it fits.</summary>
    public bool TryGet(out int value)
    {
        if (TryGetInteger(out long l) && l is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)l;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Reads Int32, Int64 or the underlying value of an EnumCode.</summary>
    public bool TryGet(out long value)
    {
        if (TryGetInteger(out value)) return true;
        if (_kind == ValueKind.EnumCode) { value = _raw.Read<long>(); return true; }
        return false;
    }

    /// <summary>Reads any exact numeric storage (integers, Decimal — including typed amounts such as Money or Mass in
    /// grams) and finite in-range Doubles.</summary>
    public bool TryGet(out decimal value)
    {
        if (TryGetExactDecimal(out value)) return true;
        if (_kind == ValueKind.Double)
        {
            double d = _raw.Read<double>();
            if (double.IsFinite(d) && d is >= (double)decimal.MinValue and <= (double)decimal.MaxValue)
            {
                value = (decimal)d;
                return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>Reads Double or converts any numeric kind.</summary>
    public bool TryGet(out double value)
    {
        if (_kind == ValueKind.Double) { value = _raw.Read<double>(); return true; }
        if (TryGetExactDecimal(out decimal m)) { value = (double)m; return true; }
        value = 0;
        return false;
    }

    public bool TryGet(out DateOnly value)
    {
        bool ok = _kind == ValueKind.Date;
        value = ok ? DateOnly.FromDayNumber((int)_raw.Read<long>()) : default;
        return ok;
    }

    public bool TryGet(out TimeOnly value)
    {
        bool ok = _kind == ValueKind.Time;
        value = ok ? new TimeOnly(_raw.Read<long>()) : default;
        return ok;
    }

    public bool TryGet(out DateTime value)
    {
        bool ok = _kind == ValueKind.DateTime;
        value = ok ? _raw.Read<DateTime>() : default;
        return ok;
    }

    public bool TryGet(out Guid value)
    {
        bool ok = _kind == ValueKind.Guid;
        value = ok ? _raw.Read<Guid>() : default;
        return ok;
    }

    /// <summary>Reads Text (plain or typed), or the code of an EnumCode.</summary>
    public bool TryGet(out string? value)
    {
        bool ok = _kind is ValueKind.Text or ValueKind.EnumCode;
        value = ok ? _text : null;
        return ok;
    }

    /// <summary>Reads an EnumCode (or an integer) as <typeparamref name="TEnum"/>.</summary>
    public bool TryGetEnum<TEnum>(out TEnum value) where TEnum : unmanaged, Enum
    {
        if (_kind is ValueKind.EnumCode or ValueKind.Int32 or ValueKind.Int64)
        {
            long l = _raw.Read<long>();
            value = Unsafe.As<long, TEnum>(ref l);   // little-endian: low bytes hold the value
            return true;
        }
        value = default;
        return false;
    }

    // ── Comparison ───────────────────────────────────────────────────────────

    /// <summary>
    /// Compares two values of the same type or numerically compatible kinds: numbers compare by value, and a typed
    /// amount (Money, Mass…) compares with a plain number by amount. Two different declared types never compare.
    /// <see cref="ValueKind.None"/> sorts first. Incompatible values throw <see cref="InvalidOperationException"/> —
    /// generators guarantee compatible kinds at compile time.
    /// </summary>
    public int CompareTo(RawValue other)
        => TryCompareTo(in other, out int result)
            ? result
            : throw new InvalidOperationException($"Cannot compare {Describe()} with {other.Describe()}.");

    /// <summary>
    /// Non-throwing <see cref="CompareTo(RawValue)"/>: false when the values are not comparable.
    /// An EnumCode also compares with Int32/Int64 by its underlying value.
    /// </summary>
    public bool TryCompareTo(in RawValue other, out int result)
    {
        if (_kind == ValueKind.None || other._kind == ValueKind.None)
        {
            result = (_kind == ValueKind.None ? 0 : 1) - (other._kind == ValueKind.None ? 0 : 1);
            return true;
        }

        if (_type != 0 && other._type != 0 && _type != other._type)
        {
            result = 0;
            return false;
        }

        if (_kind == other._kind)
        {
            result = CompareSameKind(in other);
            return true;
        }

        if (IsNumeric(_kind) && IsNumeric(other._kind))
        {
            if (_kind == ValueKind.Double || other._kind == ValueKind.Double)
            {
                TryGet(out double a);
                other.TryGet(out double b);
                result = a.CompareTo(b);
            }
            else
            {
                TryGetExactDecimal(out decimal a);
                other.TryGetExactDecimal(out decimal b);
                result = a.CompareTo(b);
            }
            return true;
        }

        if ((_kind == ValueKind.EnumCode && other._kind is ValueKind.Int32 or ValueKind.Int64)
            || (other._kind == ValueKind.EnumCode && _kind is ValueKind.Int32 or ValueKind.Int64))
        {
            result = _raw.Read<long>().CompareTo(other._raw.Read<long>());
            return true;
        }

        result = 0;
        return false;
    }

    private int CompareSameKind(in RawValue other)
    {
        switch (_kind)
        {
            case ValueKind.Bool:
            case ValueKind.Int32:
            case ValueKind.Int64:
            case ValueKind.Date:
            case ValueKind.Time:
            case ValueKind.EnumCode:
                return _raw.Read<long>().CompareTo(other._raw.Read<long>());
            case ValueKind.Double:
                return _raw.Read<double>().CompareTo(other._raw.Read<double>());
            case ValueKind.Text:
                return string.CompareOrdinal(_text, other._text);
            case ValueKind.DateTime:
                return _raw.Read<DateTime>().CompareTo(other._raw.Read<DateTime>());
            case ValueKind.Guid:
                return _raw.Read<Guid>().CompareTo(other._raw.Read<Guid>());
            default:
                return _raw.Read<decimal>().CompareTo(other._raw.Read<decimal>());
        }
    }

    public static bool operator <(RawValue left, RawValue right) => left.CompareTo(right) < 0;
    public static bool operator >(RawValue left, RawValue right) => left.CompareTo(right) > 0;
    public static bool operator <=(RawValue left, RawValue right) => left.CompareTo(right) <= 0;
    public static bool operator >=(RawValue left, RawValue right) => left.CompareTo(right) >= 0;

    // ── Equality (kind- and type-strict; decimals compare by value, so 1.0m == 1.00m) ──

    public bool Equals(RawValue other)
    {
        if (_kind != other._kind || _type != other._type) return false;
        return _kind switch
        {
            ValueKind.None => true,
            ValueKind.Text => string.Equals(_text, other._text, StringComparison.Ordinal),
            ValueKind.Double => _raw.Read<double>().Equals(other._raw.Read<double>()),
            ValueKind.Decimal => _raw.Read<decimal>() == other._raw.Read<decimal>(),
            ValueKind.EnumCode => _raw[0] == other._raw[0],
            _ => _raw[0] == other._raw[0] && _raw[1] == other._raw[1],
        };
    }

    public override bool Equals(object? obj) => obj is RawValue other && Equals(other);

    public override int GetHashCode() => _kind switch
    {
        ValueKind.None => 0,
        ValueKind.Text => HashCode.Combine(_kind, _type, string.GetHashCode(_text, StringComparison.Ordinal)),
        ValueKind.Double => HashCode.Combine(_kind, _type, _raw.Read<double>()),
        ValueKind.Decimal => HashCode.Combine(_kind, _type, _raw.Read<decimal>()),
        _ => HashCode.Combine(_kind, _type, _raw[0], _raw[1]),
    };

    public static bool operator ==(RawValue left, RawValue right) => left.Equals(right);
    public static bool operator !=(RawValue left, RawValue right) => !left.Equals(right);

    // ── Canonical binary form (fingerprints, cache keys) ─────────────────────

    /// <summary>
    /// Appends a canonical, platform-independent encoding (kind + type + normalized value) to
    /// <paramref name="hash"/>. Numerically equal decimals (1.0 / 1.00) hash the same.
    /// </summary>
    public void AppendTo(ref StableHash64 hash)
    {
        hash.Append((byte)_kind);
        if (_type != 0) hash.Append((long)_type);
        switch (_kind)
        {
            case ValueKind.None:
                break;
            case ValueKind.Text:
                hash.Append(_text);
                break;
            case ValueKind.Double:
                hash.Append(BitConverter.DoubleToInt64Bits(_raw.Read<double>()));
                break;
            case ValueKind.DateTime:
                hash.Append(_raw.Read<DateTime>().Ticks);
                break;
            case ValueKind.Guid:
                Span<byte> g = stackalloc byte[16];
                _raw.Read<Guid>().TryWriteBytes(g);
                hash.Append(g);
                break;
            case ValueKind.Decimal:
                hash.Append(_raw.Read<decimal>());
                break;
            default: // Bool, Int32, Int64, Date, Time, EnumCode (value, not code)
                hash.Append(_raw.Read<long>());
                break;
        }
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Culture-aware formatting for messages ("80", "R$ 1.000,00", "10 %", enum code). Typed values use their
    /// <see cref="RawType"/>'s formatter; <paramref name="format"/> is the type's own format string ("N0", "yyyy-MM-dd"…).
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_type != 0 && RawType.Find(_type)?.Formatter is { } formatter)
            return formatter(in this, destination, out charsWritten, format, provider);

        switch (_kind)
        {
            case ValueKind.None:
                charsWritten = 0;
                return true;
            case ValueKind.Bool:
                return (_raw.Read<long>() != 0).TryFormat(destination, out charsWritten);
            case ValueKind.Int32:
            case ValueKind.Int64:
                return _raw.Read<long>().TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.Decimal:
                return _raw.Read<decimal>().TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.Double:
                return _raw.Read<double>().TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.Date:
                return DateOnly.FromDayNumber((int)_raw.Read<long>()).TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.Time:
                return new TimeOnly(_raw.Read<long>()).TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.DateTime:
                return _raw.Read<DateTime>().TryFormat(destination, out charsWritten, format, provider);
            case ValueKind.Guid:
                return _raw.Read<Guid>().TryFormat(destination, out charsWritten, format);
            default: // Text, EnumCode (code)
                return CopyTo(_text, destination, out charsWritten);
        }
    }

    /// <inheritdoc cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, IFormatProvider? provider = null)
        => TryFormat(destination, out charsWritten, default, provider);

    /// <summary>Allocates; for logs, wire and tests. Messages should use <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>.</summary>
    public string ToString(string? format, IFormatProvider? provider)
    {
        if (_type == 0 && _kind is ValueKind.Text or ValueKind.EnumCode) return _text ?? string.Empty;

        Span<char> buffer = stackalloc char[128];
        if (TryFormat(buffer, out int written, format, provider))
            return new string(buffer[..written]);

        char[] big = new char[4096];
        return TryFormat(big, out written, format, provider) ? new string(big, 0, written) : string.Empty;
    }

    public override string ToString() => ToString(null, CultureInfo.CurrentCulture);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string Describe() => _type == 0 ? $"a {_kind} value" : $"a {Type?.Key ?? _type.ToString("x8")} value";

    private static uint CheckStorage(RawType type, ValueKind storage)
        => type.Storage == storage
            ? type.Id
            : throw new ArgumentException($"Raw type '{type.Key}' is stored as {type.Storage}, not {storage}.", nameof(type));

    private static bool CopyTo(string? text, Span<char> destination, out int charsWritten)
    {
        if (text is null) { charsWritten = 0; return true; }
        if (text.AsSpan().TryCopyTo(destination)) { charsWritten = text.Length; return true; }
        charsWritten = 0;
        return false;
    }

    private bool TryGetInteger(out long value)
    {
        bool ok = _kind is ValueKind.Int32 or ValueKind.Int64;
        value = ok ? _raw.Read<long>() : 0;
        return ok;
    }

    private bool TryGetExactDecimal(out decimal value)
    {
        switch (_kind)
        {
            case ValueKind.Int32:
            case ValueKind.Int64:
                value = _raw.Read<long>();
                return true;
            case ValueKind.Decimal:
                value = _raw.Read<decimal>();
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool IsNumeric(ValueKind k)
        => k is ValueKind.Int32 or ValueKind.Int64 or ValueKind.Decimal or ValueKind.Double;

    private static long EnumToInt64<TEnum>(TEnum value) where TEnum : unmanaged, Enum
    {
        // Type.GetTypeCode on an enum returns its underlying type's code; JIT/AOT fold it per TEnum.
        return System.Type.GetTypeCode(typeof(TEnum)) switch
        {
            TypeCode.SByte => Unsafe.As<TEnum, sbyte>(ref value),
            TypeCode.Byte => Unsafe.As<TEnum, byte>(ref value),
            TypeCode.Int16 => Unsafe.As<TEnum, short>(ref value),
            TypeCode.UInt16 => Unsafe.As<TEnum, ushort>(ref value),
            TypeCode.Int32 => Unsafe.As<TEnum, int>(ref value),
            TypeCode.UInt32 => Unsafe.As<TEnum, uint>(ref value),
            _ => Unsafe.As<TEnum, long>(ref value),
        };
    }
}
