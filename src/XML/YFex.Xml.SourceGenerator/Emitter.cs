using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YFex.Xml.SourceGenerator;

/// <summary>Turns a merged <see cref="SchemaSet"/> into C# source files.</summary>
internal sealed class Emitter
{
    private readonly SchemaSet _set;
    private readonly string _ns;
    private readonly bool _emitStructs;

    public Emitter(SchemaSet set, string ns, bool emitStructs)
    {
        _set = set;
        _ns = ns;
        _emitStructs = emitStructs;
    }

    /// <summary>All output files as (hintName, source).</summary>
    public IEnumerable<(string Hint, string Source)> Emit()
    {
        // 1) Faceted simple-type structs (one file).
        if (_emitStructs)
        {
            var faceted = _set.SimpleTypes.Values.Where(s => s.HasFacets)
                .OrderBy(s => s.Name).ToList();
            if (faceted.Count > 0)
                yield return ("_FacetTypes", EmitFacetFile(faceted));
        }

        // 2) Complex types grouped by their top-level owner file.
        var groups = _set.ComplexTypes.Values
            .GroupBy(c => string.IsNullOrEmpty(c.OwnerFile) ? c.Name : c.OwnerFile)
            .OrderBy(g => g.Key);
        foreach (var g in groups)
            yield return (g.Key, EmitComplexFile(g.Key, g.OrderBy(c => c.Name)));

        // 3) Stubs for unresolved imported types (e.g. ds:Signature).
        var stubs = _set.ExternalRefs.Where(r => !_set.ComplexTypes.ContainsKey(r)).OrderBy(r => r).ToList();
        if (stubs.Count > 0)
            yield return ("_ExternalStubs", EmitStubFile(stubs));
    }

    private string EmitFacetFile(List<XsdSimpleType> faceted)
    {
        var sb = Header();
        sb.AppendLine("using YFex.Xml;");
        sb.AppendLine("using YFex.Xml.Facets;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_ns};");
        sb.AppendLine();
        foreach (var st in faceted) EmitFacetStruct(sb, st);
        return sb.ToString();
    }

    private void EmitFacetStruct(StringBuilder sb, XsdSimpleType st)
    {
        string id = Ident(st.Name);
        sb.AppendLine($"/// <summary>Validating value for XSD simpleType <c>{Xml(st.Name)}</c>.</summary>");
        sb.AppendLine($"public readonly partial struct {id} : IXsdValue<{id}>");
        sb.AppendLine("{");
        sb.AppendLine("    public string Value { get; }");
        sb.AppendLine($"    private {id}(string value) => Value = value;");
        sb.AppendLine();
        sb.AppendLine("    public static XsdFacets Facets { get; } = new()");
        sb.AppendLine("    {");
        sb.AppendLine($"        Whitespace = XsdWhitespace.{Whitespace(st.Whitespace)},");
        if (st.Length is { } l) sb.AppendLine($"        Length = {l},");
        if (st.MinLength is { } mn) sb.AppendLine($"        MinLength = {mn},");
        if (st.MaxLength is { } mx) sb.AppendLine($"        MaxLength = {mx},");
        if (st.TotalDigits is { } td) sb.AppendLine($"        TotalDigits = {td},");
        if (st.FractionDigits is { } fd) sb.AppendLine($"        FractionDigits = {fd},");
        if (st.Pattern != null) sb.AppendLine($"        Pattern = {Verbatim(st.Pattern)},");
        if (st.Enumeration.Count > 0)
        {
            var items = string.Join(", ", st.Enumeration.Select(Verbatim));
            sb.AppendLine($"        Enumeration = new[] {{ {items} }}.ToFrozenSet(),");
        }
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine($"    public static bool TryParse(string? value, out {id} result)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (Facets.ValidateString(value, out var normalized).IsValid)");
        sb.AppendLine($"        {{ result = new {id}(normalized); return true; }}");
        sb.AppendLine("        result = default; return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public static {id} Parse(string value) =>");
        sb.AppendLine($"        TryParse(value, out var r) ? r : throw new System.FormatException($\"Invalid {Xml(st.Name)}: {{value}}\");");
        sb.AppendLine("    public override string ToString() => Value;");
        sb.AppendLine($"    public static implicit operator string({id} v) => v.Value;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private string EmitComplexFile(string owner, IEnumerable<XsdComplexType> types)
    {
        var sb = Header();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using YFex.Xml;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_ns};");
        sb.AppendLine();
        foreach (var ct in types) EmitComplexClass(sb, ct);
        return sb.ToString();
    }

    private void EmitComplexClass(StringBuilder sb, XsdComplexType ct)
    {
        string id = Ident(ct.Name);
        sb.AppendLine($"/// <summary>Generated POCO for XML group <c>{Xml(ct.Name)}</c>. Internal wire type.</summary>");
        sb.AppendLine($"[XmlGroup(\"{Xml(ct.Name)}\")]");
        sb.AppendLine($"internal partial class {id}");
        sb.AppendLine("{");

        // A schema may legally repeat an element name across choice branches; the
        // wire POCO needs each property once. Track emitted identifiers to dedupe.
        var emitted = new HashSet<string>(System.StringComparer.Ordinal);

        var choiceGroups = ct.Members.Where(m => m.ChoiceGroup != null)
                                     .GroupBy(m => m.ChoiceGroup!).ToList();

        foreach (var m in ct.Members)
        {
            string propId = Ident(m.XmlName);
            if (!emitted.Add(propId)) continue; // duplicate element name → keep first
            string csType = m.TypeName.Length == 0 ? "string" : (IsGenerated(m.Kind) ? Ident(m.TypeName) : m.TypeName);
            bool nullable = m.IsOptional || m.ChoiceGroup != null;
            if (propId != m.XmlName) sb.AppendLine($"    [XmlField(\"{Xml(m.XmlName)}\")]");

            if (m.IsList)
                sb.AppendLine($"    public List<{csType}> {propId} {{ get; set; }} = new();");
            else if (nullable)
                sb.AppendLine($"    public {csType}? {propId} {{ get; set; }}");
            else
                sb.AppendLine($"    public {csType} {propId} {{ get; set; }}");
        }

        foreach (var a in ct.Attributes)
        {
            string propId = Ident(a.XmlName);
            if (!emitted.Add(propId)) continue;
            string csType = IsGenerated(a.Kind) ? Ident(a.TypeName) : a.TypeName;
            sb.AppendLine($"    [XmlField(\"{Xml(a.XmlName)}\", IsAttribute = true)]");
            sb.AppendLine(a.Required
                ? $"    public {csType} {propId} {{ get; set; }}"
                : $"    public {csType}? {propId} {{ get; set; }}");
        }

        foreach (var grp in choiceGroups)
        {
            string choiceProp = Ident(grp.Key);
            if (!emitted.Add(choiceProp)) continue; // avoid colliding with a real member
            string enumId = choiceProp + "Case";
            var cases = grp.Select(m => Ident(m.XmlName)).Distinct().ToList();
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Discriminator for xs:choice group <c>{Xml(grp.Key)}</c>.</summary>");
            sb.AppendLine($"    public enum {enumId} {{ None, {string.Join(", ", cases)} }}");
            sb.AppendLine($"    [XmlChoice(\"{Xml(grp.Key)}\")]");
            sb.AppendLine($"    public {enumId} {Ident(grp.Key)}");
            sb.AppendLine("    {");
            sb.AppendLine("        get");
            sb.AppendLine("        {");
            foreach (var c in cases)
                sb.AppendLine($"            if ({c} is not null) return {enumId}.{c};");
            sb.AppendLine($"            return {enumId}.None;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private string EmitStubFile(List<string> stubs)
    {
        var sb = Header();
        sb.AppendLine($"namespace {_ns};");
        sb.AppendLine();
        sb.AppendLine("// Opaque stubs for types imported from other schemas (e.g. XML-DSig Signature).");
        sb.AppendLine("// The raw element is preserved as XML for downstream signing/verification.");
        foreach (var s in stubs)
        {
            sb.AppendLine($"/// <summary>Imported type <c>{Xml(s)}</c> — opaque passthrough.</summary>");
            sb.AppendLine($"internal partial class {Ident(s)} {{ public string? OuterXml {{ get; set; }} }}");
        }
        return sb.ToString();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsGenerated(MemberKind k) =>
        k is MemberKind.ComplexClass or MemberKind.FacetStruct or MemberKind.ExternalStub;

    private StringBuilder Header()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> — produced by YFex.Xml.SourceGenerator from official XSD. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS8981, CS8618, CS1591");
        sb.AppendLine("using System.Collections.Frozen;");
        return sb;
    }

    private static string Verbatim(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";
    private static string Xml(string s) => s.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Whitespace(string w) => w is "Preserve" or "Replace" or "Collapse" ? w : "Collapse";

    private static readonly HashSet<string> CsKeywords = new(System.StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
        "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
        "internal","is","lock","long","namespace","new","null","object","operator","out","override","params",
        "private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc",
        "static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
        "unsafe","ushort","using","virtual","void","volatile","while",
    };

    /// <summary>Sanitizes an XML name into a valid C# identifier; escapes keywords with @.</summary>
    internal static string Ident(string xmlName)
    {
        if (string.IsNullOrEmpty(xmlName)) return "_";
        var sb = new StringBuilder(xmlName.Length);
        foreach (char c in xmlName)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        var id = sb.ToString();
        return CsKeywords.Contains(id) ? "@" + id : id;
    }
}
