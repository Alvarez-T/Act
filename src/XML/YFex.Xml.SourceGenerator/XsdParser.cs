using System.Xml.Linq;

namespace YFex.Xml.SourceGenerator;

/// <summary>
/// Deterministic XSD reader. Recursively registers every complexType (named and
/// anonymous, at any depth) and simpleType into a shared <see cref="SchemaSet"/>.
/// Multiple files are merged (xs:include/xs:import resolved by feeding the
/// referenced files). Pure: no network, no AI.
/// </summary>
internal static class XsdParser
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    /// <summary>Pass 1: parse one file into the shared set, registering all types.</summary>
    public static void ParseInto(string xsdText, SchemaSet set)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xsdText); }
        catch { return; }

        var schema = doc.Root;
        if (schema is null || schema.Name != Xs + "schema") return;

        foreach (var el in schema.Elements())
        {
            if (el.Name == Xs + "simpleType")
            {
                var name = el.Attribute("name")?.Value;
                if (name is null || set.SimpleTypes.ContainsKey(name)) continue;
                var st = ParseSimpleType(el, name);
                if (st != null) { set.SimpleTypes[name] = st; set.ReserveExact(name); }
            }
            else if (el.Name == Xs + "complexType")
            {
                var name = el.Attribute("name")?.Value;
                if (name is null || set.ComplexTypes.ContainsKey(name)) continue;
                set.ReserveExact(name);
                var ct = new XsdComplexType { Name = name, OwnerFile = name };
                set.ComplexTypes[name] = ct;
                FillComplexType(el, ct, set, ownerFile: name);
            }
            else if (el.Name == Xs + "element")
            {
                var rootEl = new XsdElement { XmlName = el.Attribute("name")?.Value ?? "" };
                rootEl.TypeRef = LocalName(el.Attribute("type")?.Value);
                var inlineCt = el.Element(Xs + "complexType");
                if (inlineCt != null && rootEl.XmlName.Length > 0)
                {
                    var typeName = set.ReserveName(rootEl.XmlName);
                    var ct = new XsdComplexType { Name = typeName, OwnerFile = typeName };
                    set.ComplexTypes[typeName] = ct;
                    FillComplexType(inlineCt, ct, set, ownerFile: typeName);
                    rootEl.InlineComplexName = typeName;
                }
                if (rootEl.XmlName.Length > 0 && (rootEl.TypeRef != null || rootEl.InlineComplexName != null))
                    set.RootElements.Add(rootEl);
            }
        }
    }

    /// <summary>Pass 2: resolve every pending type/ref against the merged registry.</summary>
    public static void Resolve(SchemaSet set)
    {
        foreach (var ct in set.ComplexTypes.Values)
        {
            foreach (var m in ct.Members)
                if (m.PendingRef is { } r) ResolveRef(r, set, out m.TypeName, out m.Kind);
            foreach (var a in ct.Attributes)
                if (a.PendingRef is { } r) ResolveRef(r, set, out a.TypeName, out a.Kind);
        }
    }

    private static void ResolveRef(string local, SchemaSet set, out string typeName, out MemberKind kind)
    {
        if (set.ComplexTypes.ContainsKey(local)) { typeName = local; kind = MemberKind.ComplexClass; return; }
        if (set.SimpleTypes.TryGetValue(local, out var st))
        {
            if (st.HasFacets) { typeName = local; kind = MemberKind.FacetStruct; return; }
            typeName = st.BasePrimitive; kind = MemberKind.Primitive; return;
        }
        var prim = MapPrimitiveOrNull(local);
        if (prim != null) { typeName = prim; kind = MemberKind.Primitive; return; }
        // Unknown (e.g. imported ds:Signature) → external stub class.
        set.ExternalRefs.Add(local);
        typeName = local; kind = MemberKind.ExternalStub;
    }

    // ── recursive complex-type filling ───────────────────────────────────────

    private static void FillComplexType(XElement ctEl, XsdComplexType ct, SchemaSet set, string ownerFile)
    {
        foreach (var particle in ctEl.Elements())
            WalkParticle(particle, ct, set, ownerFile, choiceGroup: null);
    }

    private static void WalkParticle(XElement particle, XsdComplexType ct, SchemaSet set,
        string ownerFile, string? choiceGroup)
    {
        var ln = particle.Name.LocalName;
        if (ln is "sequence" or "all")
        {
            foreach (var child in particle.Elements())
                WalkParticle(child, ct, set, ownerFile, choiceGroup);
        }
        else if (ln == "choice")
        {
            var grp = choiceGroup ?? ct.Name + "Choice";
            foreach (var child in particle.Elements())
                WalkParticle(child, ct, set, ownerFile, grp);
        }
        else if (ln == "element")
        {
            ct.Members.Add(ParseMember(particle, set, ownerFile, choiceGroup));
        }
        else if (ln == "attribute")
        {
            AddAttribute(particle, ct, set, ownerFile);
        }
        // xs:group/xs:any are absent in the NFe leiaute; ignored if present.
    }

    private static XsdMember ParseMember(XElement el, SchemaSet set, string ownerFile, string? choiceGroup)
    {
        var name = el.Attribute("name")?.Value ?? LocalName(el.Attribute("ref")?.Value) ?? "";
        var m = new XsdMember { XmlName = name, ChoiceGroup = choiceGroup };

        var min = el.Attribute("minOccurs")?.Value;
        if (min != null && int.TryParse(min, out var mn)) m.MinOccurs = mn;
        var max = el.Attribute("maxOccurs")?.Value;
        if (max == "unbounded") m.Unbounded = true;
        else if (max != null && int.TryParse(max, out var mx)) m.MaxOccurs = mx;

        var inlineComplex = el.Element(Xs + "complexType");
        if (inlineComplex != null)
        {
            var typeName = set.ReserveName(name);
            var nested = new XsdComplexType { Name = typeName, OwnerFile = ownerFile };
            set.ComplexTypes[typeName] = nested;
            FillComplexType(inlineComplex, nested, set, ownerFile);
            m.Kind = MemberKind.ComplexClass;
            m.TypeName = typeName;
            return m;
        }

        var inlineSimple = el.Element(Xs + "simpleType");
        if (inlineSimple != null)
        {
            var stName = set.ReserveName(name + "Type");
            var st = ParseSimpleType(inlineSimple, stName);
            if (st != null && st.HasFacets)
            {
                set.SimpleTypes[stName] = st;
                m.Kind = MemberKind.FacetStruct;
                m.TypeName = stName;
            }
            else
            {
                m.Kind = MemberKind.Primitive;
                m.TypeName = st?.BasePrimitive ?? "string";
            }
            return m;
        }

        // type= or ref= → resolve in pass 2.
        var typeRef = LocalName(el.Attribute("type")?.Value) ?? LocalName(el.Attribute("ref")?.Value);
        m.PendingRef = typeRef ?? "string";
        return m;
    }

    private static void AddAttribute(XElement el, XsdComplexType ct, SchemaSet set, string ownerFile)
    {
        var a = new XsdAttribute
        {
            XmlName = el.Attribute("name")?.Value ?? "",
            Required = el.Attribute("use")?.Value == "required",
        };
        if (a.XmlName.Length == 0) return;

        var inlineSimple = el.Element(Xs + "simpleType");
        if (inlineSimple != null)
        {
            var stName = set.ReserveName(a.XmlName + "AttrType");
            var st = ParseSimpleType(inlineSimple, stName);
            if (st != null && st.HasFacets) { set.SimpleTypes[stName] = st; a.Kind = MemberKind.FacetStruct; a.TypeName = stName; }
            else { a.Kind = MemberKind.Primitive; a.TypeName = st?.BasePrimitive ?? "string"; }
        }
        else
        {
            a.PendingRef = LocalName(el.Attribute("type")?.Value) ?? "string";
        }
        ct.Attributes.Add(a);
    }

    private static XsdSimpleType? ParseSimpleType(XElement el, string name)
    {
        var restriction = el.Element(Xs + "restriction");
        if (restriction is null) return null; // union/list simple types are skipped

        var st = new XsdSimpleType
        {
            Name = name,
            BasePrimitive = MapPrimitive(LocalName(restriction.Attribute("base")?.Value)),
        };
        foreach (var f in restriction.Elements())
        {
            var v = f.Attribute("value")?.Value;
            switch (f.Name.LocalName)
            {
                case "pattern": st.Pattern = v; break;
                case "length": st.Length = ParseInt(v); break;
                case "minLength": st.MinLength = ParseInt(v); break;
                case "maxLength": st.MaxLength = ParseInt(v); break;
                case "totalDigits": st.TotalDigits = ParseInt(v); break;
                case "fractionDigits": st.FractionDigits = ParseInt(v); break;
                case "whiteSpace": st.Whitespace = Capitalize(v); break;
                case "enumeration": if (v != null) st.Enumeration.Add(v); break;
            }
        }
        return st;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? LocalName(string? qname)
    {
        if (qname is null) return null;
        int colon = qname.IndexOf(':');
        return colon >= 0 ? qname.Substring(colon + 1) : qname;
    }

    private static int? ParseInt(string? v) => int.TryParse(v, out var n) ? n : (int?)null;

    private static string Capitalize(string? v) =>
        string.IsNullOrEmpty(v) ? "Collapse" : char.ToUpperInvariant(v![0]) + v.Substring(1);

    internal static string MapPrimitive(string? xsdType) => MapPrimitiveOrNull(xsdType) ?? "string";

    private static string? MapPrimitiveOrNull(string? xsdType) => xsdType switch
    {
        "string" or "normalizedString" or "token" or "anyURI" or "ID" or "IDREF"
            or "language" or "NMTOKEN" => "string",
        "decimal" => "decimal",
        "int" or "integer" or "nonNegativeInteger" or "positiveInteger" or "short" or "unsignedShort" => "int",
        "long" or "unsignedLong" => "long",
        "boolean" => "bool",
        "base64Binary" or "hexBinary" => "string",
        "dateTime" or "date" or "time" or "gYear" or "gMonth" or "gDay" => "string",
        _ => null,
    };
}
