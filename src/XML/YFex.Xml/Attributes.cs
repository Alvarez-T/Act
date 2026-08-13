namespace YFex.Xml;

/// <summary>
/// Stamped by the generator onto a generated POCO whose C# type name differs
/// from its XML element/complexType name, or to carry the XML group name.
/// Emitted only when the C# name cannot equal the XML name.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class XmlGroupAttribute(string name) : Attribute
{
    /// <summary>The literal XML element / complexType name.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// Stamped onto a generated property whose C# name differs from the XML
/// element/attribute name it maps to. Absent when the names are identical.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class XmlFieldAttribute(string name) : Attribute
{
    /// <summary>The literal XML element/attribute name.</summary>
    public string Name { get; } = name;

    /// <summary>True when this field maps to an XML attribute rather than a child element.</summary>
    public bool IsAttribute { get; init; }
}

/// <summary>
/// Marks a generated discriminator member emitted alongside the literal nullable
/// members of an <c>xs:choice</c>, so the model layer can collapse the choice
/// into a union without re-parsing the schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class XmlChoiceAttribute(string groupName) : Attribute
{
    /// <summary>The logical choice-group name (owning element + "Choice").</summary>
    public string GroupName { get; } = groupName;
}
