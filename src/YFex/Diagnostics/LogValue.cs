using System.Runtime.CompilerServices;
using YFex.Extensions;

namespace YFex.Diagnostics;

/// <summary>
/// How a value object writes itself into a record. Normally generated from <see cref="LogValueAttribute"/> on the struct;
/// hand-written for full control (log a CNPJ root only). Static abstract: no boxing, no virtual call.
/// <para>
/// Sensitivity is deliberately not part of this contract: it is a property of the data owned by YFex.Privacy
/// (<c>[Sensitive]</c> / <c>IClassifiedData&lt;T&gt;</c>), which the logging generator reads (logging L14).
/// </para>
/// </summary>
public interface ILogValue<TSelf> where TSelf : ILogValue<TSelf>
{
    /// <summary>Writes <paramref name="value"/> as field <paramref name="field"/>.</summary>
    static abstract void Write(in TSelf value, ref LogWriter writer, ushort field);

    /// <summary>True for ids the log server indexes for search (<c>VendaId</c>, <c>ChaveNFe</c>; logging 13 §5).</summary>
    static virtual bool IsSearchKey => false;
}

/// <summary>
/// Placed once on a value object (never on the members that use it): the logging generator implements
/// <see cref="ILogValue{TSelf}"/> for it (formatted straight into the record when it is span-formattable).
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class LogValueAttribute : Attribute
{
    /// <summary>Index the value on the log server: "find everything that touched V-88123".</summary>
    public bool SearchKey { get; init; }

    /// <summary>Format string passed to <c>TryFormat</c> (log-specific, e.g. <c>"R"</c>).</summary>
    public string? Format { get; init; }
}

/// <summary>Writes the members of a record/class, each with its typed writer (generated log shape, logging 03 §1.5).</summary>
public delegate void LogShapeWriter<T>(in T value, ref LogWriter writer);

/// <summary>
/// Static generic slot holding the generated log shape of <typeparamref name="T"/>: one static load, no dictionary, no
/// reflection. Filled by the logging generator's module initializers.
/// </summary>
public static class LogShapeFor<T>
{
    /// <summary>The shape writer; null when <typeparamref name="T"/> has no generated shape.</summary>
    public static LogShapeWriter<T>? Write { get; private set; }

    /// <summary>Member names, indexed by the field ids the shape writes.</summary>
    public static string[]? Names { get; private set; }

    /// <summary>Registers the shape. Called from generated module initializers; the last registration wins.</summary>
    public static void Register(string[] names, LogShapeWriter<T> write)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(write);
        Names = names;
        Write = write;
    }
}

/// <summary>Consumer-side name table of an enum type, referenced by <see cref="LogValueKind.Enum"/> entries.</summary>
public abstract class LogEnumNames
{
    /// <summary>The enum type's name (for diagnostics).</summary>
    public abstract string TypeName { get; }

    /// <summary>Stable code of a value (<c>[EnumCode]</c> or member name); the number for undefined values.</summary>
    public abstract string NameOf(long value);
}

/// <summary>Name table of <typeparamref name="TEnum"/>; one shared instance per enum type.</summary>
public sealed class LogEnumNames<TEnum> : LogEnumNames where TEnum : unmanaged, Enum
{
    private static readonly bool s_signed = IsSigned();

    /// <summary>The shared instance stored in record reference tables.</summary>
    public static readonly LogEnumNames<TEnum> Instance = new();

    private LogEnumNames() { }

    public override string TypeName => typeof(TEnum).Name;

    public override string NameOf(long value)
    {
        TEnum typed = FromInt64(value);
        return typed.IsDefined() ? typed.GetCode() : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The underlying value widened to <see cref="long"/> without boxing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToInt64(TEnum value)
    {
        if (Unsafe.SizeOf<TEnum>() == 1) return s_signed ? Unsafe.As<TEnum, sbyte>(ref value) : Unsafe.As<TEnum, byte>(ref value);
        if (Unsafe.SizeOf<TEnum>() == 2) return s_signed ? Unsafe.As<TEnum, short>(ref value) : Unsafe.As<TEnum, ushort>(ref value);
        if (Unsafe.SizeOf<TEnum>() == 4) return s_signed ? Unsafe.As<TEnum, int>(ref value) : Unsafe.As<TEnum, uint>(ref value);
        return Unsafe.As<TEnum, long>(ref value);
    }

    private static TEnum FromInt64(long value)
    {
        if (Unsafe.SizeOf<TEnum>() == 1) { byte b = unchecked((byte)value); return Unsafe.As<byte, TEnum>(ref b); }
        if (Unsafe.SizeOf<TEnum>() == 2) { ushort s = unchecked((ushort)value); return Unsafe.As<ushort, TEnum>(ref s); }
        if (Unsafe.SizeOf<TEnum>() == 4) { uint i = unchecked((uint)value); return Unsafe.As<uint, TEnum>(ref i); }
        return Unsafe.As<long, TEnum>(ref value);
    }

    private static bool IsSigned()
    {
        TEnum minusOne = FromInt64(-1);
        // For signed underlying types, -1 compares below zero.
        return Comparer<TEnum>.Default.Compare(minusOne, default) < 0;
    }
}
