namespace YFex.Validation;

/// <summary>JSON type of a member (JSON Schema <c>type</c> + <c>format</c>).</summary>
public enum JsonKind : byte
{
    Any = 0,
    Bool,
    Integer,
    Number,
    String,
    Date,
    DateTime,
    Time,
    Guid,
    /// <summary>An enum, written as its stable codes (<c>[EnumCode]</c> or member names).</summary>
    Enum,
    /// <summary>A nested validatable object (<see cref="MemberShape.Nested"/>).</summary>
    Object,
    /// <summary>A collection (<see cref="MemberShape.Element"/>).</summary>
    Array,
}

/// <summary>An enum option: stable code (what JSON carries) + underlying value (what <see cref="OptionSet"/> holds).</summary>
public readonly record struct EnumMember(string Code, long Value);

/// <summary>
/// Generated description of one member of a validatable type, for JSON Schema export (AI tool parameters, forms,
/// docs). Constraints are NOT here — they come from <c>T.TryGetConstraints(path)</c> (static) or an
/// <see cref="IValidationSource"/> (static + live decision), so the same shape serves both.
/// </summary>
public sealed class MemberShape(string name, JsonKind kind, bool nullable = false, RawType? rawType = null, EnumMember[]? enumMembers = null,
    Func<TypeShape?>? nested = null, MemberShape? element = null, Type? clrType = null)
{
    private readonly EnumMember[] _enumMembers = enumMembers ?? [];

    public string Name { get; } = name;
    public JsonKind Kind { get; } = kind;
    public bool Nullable { get; } = nullable;
    /// <summary>Value type of an app type (Money, Mass, Sku…) — written as <c>x-type</c> so tools know the unit.</summary>
    public RawType? RawType { get; } = rawType;
    public ReadOnlySpan<EnumMember> EnumMembers => _enumMembers;
    /// <summary>Lazily, so recursive object graphs are fine.</summary>
    public Func<TypeShape?>? Nested { get; } = nested;
    public MemberShape? Element { get; } = element;
    /// <summary>Declared CLR type (<c>typeof</c>, generated — no reflection) for hosts that list parameters by type.</summary>
    public Type? ClrType { get; } = clrType;

    /// <summary>A member of an app value type (Money, Mass, Sku…): JSON kind from its storage, <c>x-type</c> = its key.</summary>
    public static MemberShape ForRaw(string name, RawType type, bool nullable = false, Type? clrType = null) => new(name, type.Storage switch
    {
        ValueKind.Bool => JsonKind.Bool,
        ValueKind.Int32 or ValueKind.Int64 => JsonKind.Integer,
        ValueKind.Decimal or ValueKind.Double => JsonKind.Number,
        ValueKind.Date => JsonKind.Date,
        ValueKind.DateTime => JsonKind.DateTime,
        ValueKind.Time => JsonKind.Time,
        ValueKind.Guid => JsonKind.Guid,
        _ => JsonKind.String,
    }, nullable, type, clrType: clrType);
}

/// <summary>Generated shape of a validatable type (<c>IValidatable&lt;T&gt;.Shape</c>).</summary>
public sealed class TypeShape(string name, MemberShape[] members, Func<TypeShape?>? baseShape = null)
{
    private readonly MemberShape[] _members = members;

    /// <summary>Type name (also the message-catalog key prefix of field display names, 10 §5).</summary>
    public string Name { get; } = name;
    /// <summary>Own members (base type's members: <see cref="Base"/>).</summary>
    public ReadOnlySpan<MemberShape> Members => _members;
    public Func<TypeShape?>? Base { get; } = baseShape;
}
