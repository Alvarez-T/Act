using System.Collections.Generic;

namespace YFex.Xml.SourceGenerator;

/// <summary>
/// A merged symbol table built from ALL fed XSD files (xs:include/xs:import are
/// resolved by feeding the referenced files as AdditionalFiles). Named types are
/// global; anonymous inline complexTypes are registered with a generated unique
/// name and tagged with the top-level group they descend from.
/// </summary>
internal sealed class SchemaSet
{
    public Dictionary<string, XsdSimpleType> SimpleTypes { get; } = new();
    public Dictionary<string, XsdComplexType> ComplexTypes { get; } = new();
    public List<XsdElement> RootElements { get; } = new();
    /// <summary>Type names referenced but never defined (e.g. imported ds:Signature) → emit stubs.</summary>
    public HashSet<string> ExternalRefs { get; } = new();

    private readonly HashSet<string> _usedNames = new();

    /// <summary>Reserves a unique type name, disambiguating with a numeric suffix.</summary>
    public string ReserveName(string preferred)
    {
        var name = string.IsNullOrEmpty(preferred) ? "Anonimo" : preferred;
        if (_usedNames.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = name + "_" + i;
            if (_usedNames.Add(candidate)) return candidate;
        }
    }

    public bool NameTaken(string name) => _usedNames.Contains(name);
    public void ReserveExact(string name) => _usedNames.Add(name);
}

internal sealed class XsdSimpleType
{
    public string Name = "";
    public string BasePrimitive = "string";
    public string? Pattern;
    public int? Length, MinLength, MaxLength, TotalDigits, FractionDigits;
    public string Whitespace = "Collapse";
    public List<string> Enumeration { get; } = new();

    public bool HasFacets =>
        Pattern != null || Length != null || MinLength != null || MaxLength != null
        || TotalDigits != null || FractionDigits != null || Enumeration.Count > 0;
}

internal sealed class XsdComplexType
{
    public string Name = "";
    /// <summary>Top-level named type / root element this descends from (the output file).</summary>
    public string OwnerFile = "";
    public List<XsdMember> Members { get; } = new();
    public List<XsdAttribute> Attributes { get; } = new();
}

/// <summary>How a member's type is realized in C#.</summary>
internal enum MemberKind { Primitive, FacetStruct, ComplexClass, ExternalStub }

internal sealed class XsdMember
{
    public string XmlName = "";
    /// <summary>Resolved C# type or registry key (set during parse for inline, pass-2 for refs).</summary>
    public string TypeName = "string";
    public MemberKind Kind = MemberKind.Primitive;
    /// <summary>Raw local type/ref name pending pass-2 resolution (null when inline-resolved).</summary>
    public string? PendingRef;

    public int MinOccurs = 1;
    public bool Unbounded;
    public int MaxOccurs = 1;
    public string? ChoiceGroup;

    public bool IsList => Unbounded || MaxOccurs > 1;
    public bool IsOptional => MinOccurs == 0;
}

internal sealed class XsdAttribute
{
    public string XmlName = "";
    public string TypeName = "string";
    public MemberKind Kind = MemberKind.Primitive;
    public string? PendingRef;
    public bool Required;
}

internal sealed class XsdElement
{
    public string XmlName = "";
    public string? TypeRef;
    public string? InlineComplexName; // registered name when the root element has an inline complexType
}
