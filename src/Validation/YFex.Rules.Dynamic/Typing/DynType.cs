namespace YFex.Rules.Dynamic;

/// <summary>
/// Static type of a dynamic-rule expression: a <see cref="RawType"/> (primitives — Bool, Int64, Decimal, Text, Date… — or an
/// app value type such as Money, Mass, Percentual) plus, for enums, the field that defines the options. Values at run time
/// are <see cref="RawValue"/>s of exactly this type, so the binder can prove every operation before publishing.
/// </summary>
public readonly record struct DynType(RawType Raw, SituationFieldInfo? EnumOf = null, bool IsNumberLiteral = false)
{
    public static DynType Null { get; } = new(RawType.Of(ValueKind.None));
    public static DynType Bool { get; } = new(RawType.Bool);
    public static DynType Int { get; } = new(RawType.Int64);
    public static DynType Decimal { get; } = new(RawType.Decimal);
    public static DynType Text { get; } = new(RawType.Text);
    public static DynType Date { get; } = new(RawType.Date);

    /// <summary>Type of an app value type / primitive (<c>DynType.Of&lt;Money&gt;()</c>). Enums: <see cref="RawType.EnumCode"/>.</summary>
    public static DynType Of<T>() => new(RawValueConvert.TypeOf<T>() ?? throw new ArgumentException($"{typeof(T).Name} has no raw form."));

    /// <summary>An enum parameter/return (matches any enum field; the function validates the value it gets).</summary>
    public static DynType OfEnum<TEnum>() where TEnum : unmanaged, Enum => new(RawType.EnumCode);

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

    /// <summary>Values of the two types can be compared with &lt; &gt; (numbers with numbers/amounts of one type, dates, text).</summary>
    public bool IsComparable => IsNumeric || IsDate || IsText || Raw.Storage == ValueKind.Time;

    /// <summary>Same type (enums: same CLR enum).</summary>
    public bool SameAs(in DynType other)
    {
        if (!ReferenceEquals(Raw, other.Raw) && Raw.Key != other.Raw.Key) return false;
        return !IsEnum || EnumOf is null || other.EnumOf is null || EnumOf.ClrType == other.EnumOf.ClrType;
    }

    public override string ToString() => IsEnum && EnumOf is not null ? $"enum {EnumOf.ClrType ?? EnumOf.Name}" : Raw.Key;
}
