namespace YFex;

/// <summary>
/// Storage kind of a <see cref="RawValue"/> — primitives only. Semantic types (Money, Mass, an app's
/// <c>Temperature</c> or <c>Sku</c>…) are a storage kind plus a <see cref="RawType"/> declared by the type itself
/// through <see cref="IRawValue{TSelf}"/>; the set of semantic types is open, this enum is not.
/// </summary>
public enum ValueKind : byte
{
    None = 0,
    Bool,
    Int32,
    Int64,
    Decimal,
    Double,
    Text,
    Date,
    Time,
    DateTime,
    Guid,
    EnumCode,
}
