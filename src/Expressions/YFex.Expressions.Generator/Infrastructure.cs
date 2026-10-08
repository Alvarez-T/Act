using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Expressions.Generator;

// Copies of the small helpers YFex.Rules.Generator uses (generators can't share code at runtime; keep them in sync by hand).

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    /// <summary>In-source location when the file is in the compilation (so <c>#pragma warning disable</c> applies).</summary>
    public Location ToLocation(SourceTrees? trees = null)
        => trees?.Find(FilePath) is { } tree && Span.End <= tree.Length
            ? Location.Create(tree, Span)
            : Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
        => location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic(SourceTrees? trees = null)
        => Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(trees), Args.AsSpan().ToArray());

    public static DiagnosticInfo Create(string id, Location? location, params string[] args)
        => new(id, LocationInfo.From(location), new EquatableArray<string>(args));
}

internal sealed class SourceTrees(Compilation compilation)
{
    private Dictionary<string, SyntaxTree>? _byPath;

    public SyntaxTree? Find(string path)
    {
        if (_byPath is null)
        {
            _byPath = new Dictionary<string, SyntaxTree>(System.StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
                if (!string.IsNullOrEmpty(tree.FilePath)) _byPath[tree.FilePath] = tree;
        }
        return _byPath.TryGetValue(path, out var t) ? t : null;
    }
}

internal sealed record ContainingType(string Keyword, string Name);

internal static class Names
{
    public const string RawValueInterface = "YFex.IRawValue<TSelf>";

    public static readonly SymbolDisplayFormat FqnNoNullable = SymbolDisplayFormat.FullyQualifiedFormat;

    public static string Of(ITypeSymbol t) => t.ToDisplayString(FqnNoNullable);

    public static AttributeData? Find(ISymbol symbol, string metadataName)
    {
        foreach (var a in symbol.GetAttributes())
            if (a.AttributeClass is { } c && Full(c) == metadataName) return a;
        return null;
    }

    /// <summary>Metadata-style full name (generic attributes as Name`1).</summary>
    public static string Full(INamedTypeSymbol c)
    {
        var d = c.OriginalDefinition;
        var ns = d.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        var outer = "";
        for (var t = d.ContainingType; t is not null; t = t.ContainingType) outer = t.Name + "." + outer;
        return ns + outer + d.MetadataName;
    }

    public static T? Named<T>(AttributeData a, string name)
    {
        foreach (var kv in a.NamedArguments)
            if (kv.Key == name && kv.Value.Value is T v) return v;
        return default;
    }

    public static EquatableArray<ContainingType> Containing(INamedTypeSymbol type)
    {
        var list = new List<ContainingType>();
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            list.Insert(0, new ContainingType(Keyword(t), t.Name));
        return new EquatableArray<ContainingType>(list.ToArray());
    }

    public static string Keyword(INamedTypeSymbol t)
    {
        string k = t.IsRecord ? (t.TypeKind == TypeKind.Struct ? "record struct" : "record") : t.TypeKind == TypeKind.Struct ? "struct" : "class";
        return (t.IsStatic ? "static " : "") + k;
    }

    public static bool IsPartial(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            bool partial = false;
            foreach (var r in t.DeclaringSyntaxReferences)
                if (r.GetSyntax() is TypeDeclarationSyntax tds && tds.Modifiers.Any(SyntaxKind.PartialKeyword)) partial = true;
            if (!partial) return false;
        }
        return true;
    }

    public static string Namespace(INamedTypeSymbol type)
        => type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    public static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    /// <summary>"global::A.B&lt;C&gt;" → a hint-name-safe "A.B[C]".</summary>
    public static string Hint(string fqn) => fqn.Replace("global::", "").Replace('<', '[').Replace('>', ']');

    /// <summary>True for the scalar types an expression value can hold (primitives, dates, Guid, <c>IRawValue</c> types).</summary>
    public static bool IsScalar(ITypeSymbol t)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }
        return t.ToDisplayString(FqnNoNullable) switch
        {
            "global::System.DateOnly" or "global::System.TimeOnly" or "global::System.Guid" => true,
            _ => t.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == RawValueInterface),
        };
    }
}
