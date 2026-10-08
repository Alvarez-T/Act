using System.Collections.Immutable;

namespace YFex.Expressions;

/// <summary>
/// Enum options of an enum-typed field: lets the binder turn a literal (<c>"Pix"</c>) into the enum value and lets editors
/// offer the options. <see cref="ClrType"/> distinguishes two different enums stored with the same <see cref="RawType.EnumCode"/>.
/// </summary>
public sealed record ExprEnumDomain(string Name, string? ClrType, ImmutableArray<EnumOption> Options);

/// <summary>
/// Static type of an expression: a <see cref="RawType"/> (primitives — Bool, Int64, Decimal, Text, Date… — or an app value type
/// such as Money, Mass, Percentual) plus, for enums, the domain that defines the options. Values at run time are
/// <see cref="RawValue"/>s of exactly this type, so the binder can prove every operation before anything runs.
/// </summary>
public readonly record struct ExprType(RawType Raw, ExprEnumDomain? EnumOf = null, bool IsNumberLiteral = false)
{
    public static ExprType Null { get; } = new(RawType.Of(ValueKind.None));
    public static ExprType Bool { get; } = new(RawType.Bool);
    public static ExprType Int { get; } = new(RawType.Int64);
    public static ExprType Decimal { get; } = new(RawType.Decimal);
    public static ExprType Text { get; } = new(RawType.Text);
    public static ExprType Date { get; } = new(RawType.Date);

    /// <summary>Type of an app value type / primitive (<c>ExprType.Of&lt;Money&gt;()</c>). Enums: <see cref="OfEnum{TEnum}"/>.</summary>
    public static ExprType Of<T>() => new(RawValueConvert.TypeOf<T>() ?? throw new ArgumentException($"{typeof(T).Name} has no raw form."));

    /// <summary>An enum parameter/return (matches any enum field; the function validates the value it gets).</summary>
    public static ExprType OfEnum<TEnum>() where TEnum : unmanaged, Enum => new(RawType.EnumCode);

    public bool IsNull => Raw.Storage == ValueKind.None;
    public bool IsBool => Raw.Storage == ValueKind.Bool;
    /// <summary>Plain number (Int32/Int64/Decimal/Double primitive).</summary>
    public bool IsNumber => Raw.IsPrimitive && Raw.IsNumeric;
    /// <summary>Numeric app type (Money, Mass, Percentual…).</summary>
    public bool IsAmount => !Raw.IsPrimitive && Raw.IsNumeric;
    public bool IsNumeric => Raw.IsNumeric;
    public bool IsText => Raw.Storage == ValueKind.Text;
    public bool IsEnum => Raw.Storage == ValueKind.EnumCode;
    public bool IsDate => Raw.Storage is ValueKind.Date or ValueKind.DateTime;
    public bool IsIntegral => Raw.IsPrimitive && Raw.Storage is ValueKind.Int32 or ValueKind.Int64;

    /// <summary>Values of the type can be compared with &lt; &gt; (numbers with numbers/amounts of one type, dates, text).</summary>
    public bool IsComparable => IsNumeric || IsDate || IsText || Raw.Storage == ValueKind.Time;

    /// <summary>Same type (enums: same CLR enum when both sides know it).</summary>
    public bool SameAs(in ExprType other)
    {
        if (!ReferenceEquals(Raw, other.Raw) && Raw.Key != other.Raw.Key) return false;
        return !IsEnum || EnumOf is null || other.EnumOf is null || EnumOf.ClrType == other.EnumOf.ClrType;
    }

    public override string ToString() => IsEnum && EnumOf is not null ? $"enum {EnumOf.ClrType ?? EnumOf.Name}" : Raw.Key;
}
