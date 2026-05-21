using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Persistence.Generator;

internal static class PersistenceParser
{
    private const string ObservableAttributeName = "ObservableAttribute";
    private const string PersistAttributeName    = "PersistAttribute";
    private const string PersistShortName        = "Persist";
    private const string ObservableShortName     = "Observable";

    // ── Step 1: Cheap syntax predicate ────────────────────────────────────────

    public static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl) return false;

        foreach (var member in classDecl.Members)
        {
            if (member is not PropertyDeclarationSyntax prop) continue;
            foreach (var attrList in prop.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    string name = GetAttributeIdentifier(attr.Name);
                    if (name is PersistShortName or PersistAttributeName) return true;
                }
            }
        }
        return false;
    }

    // ── Step 2: Semantic transform ────────────────────────────────────────────

    public static PersistenceClassModel? Transform(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl     = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        ct.ThrowIfCancellationRequested();

        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        string namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        var properties = new List<PersistencePropertyModel>();

        foreach (var member in classDecl.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (member is not PropertyDeclarationSyntax propDecl) continue;

            if (semanticModel.GetDeclaredSymbol(propDecl, ct) is not IPropertySymbol propSymbol)
                continue;

            bool hasPersist   = false;
            bool hasObservable = false;
            string? customKey = null;

            foreach (var attr in propSymbol.GetAttributes())
            {
                string shortName = attr.AttributeClass?.Name ?? string.Empty;
                if (shortName is PersistAttributeName)
                {
                    hasPersist = true;
                    foreach (var arg in attr.NamedArguments)
                        if (arg.Key == "Key") customKey = arg.Value.Value as string;
                }
                else if (shortName is ObservableAttributeName)
                {
                    hasObservable = true;
                }
            }

            if (!hasPersist || !hasObservable) continue;

            string typeFqn           = propSymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            bool   isKnownSerializable = IsKnownSerializable(propSymbol.Type);

            properties.Add(new PersistencePropertyModel(
                propSymbol.Name,
                typeFqn,
                isKnownSerializable,
                customKey));
        }

        if (properties.Count == 0) return null;

        return new PersistenceClassModel(
            namespaceName,
            classSymbol.Name,
            new EquatableArray<PersistencePropertyModel>(properties.ToArray()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetAttributeIdentifier(NameSyntax name) => name switch
    {
        SimpleNameSyntax sn   => sn.Identifier.ValueText,
        QualifiedNameSyntax { Right: SimpleNameSyntax s } => s.Identifier.ValueText,
        _ => string.Empty
    };

    private static bool IsKnownSerializable(ITypeSymbol type)
    {
        // Primitives and common types that MemoryPack handles without source gen
        string fqn = type.SpecialType switch
        {
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte    => "byte",
            SpecialType.System_SByte   => "sbyte",
            SpecialType.System_Char    => "char",
            SpecialType.System_Int16   => "short",
            SpecialType.System_UInt16  => "ushort",
            SpecialType.System_Int32   => "int",
            SpecialType.System_UInt32  => "uint",
            SpecialType.System_Int64   => "long",
            SpecialType.System_UInt64  => "ulong",
            SpecialType.System_Single  => "float",
            SpecialType.System_Double  => "double",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_String  => "string",
            _ => string.Empty
        };

        if (fqn.Length > 0) return true;

        // Nullable<T> of a primitive is also fine
        if (type is INamedTypeSymbol named && named.IsGenericType
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return IsKnownSerializable(named.TypeArguments[0]);

        // Check for [MemoryPackable] attribute
        foreach (var attr in type.GetAttributes())
        {
            string n = attr.AttributeClass?.Name ?? string.Empty;
            if (n is "MemoryPackableAttribute" or "MemoryPackable") return true;
        }

        // Guid, DateTime, DateTimeOffset, TimeSpan — all natively supported by MemoryPack
        string ns   = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        string name = type.Name;
        if (ns == "System" && name is "Guid" or "DateTime" or "DateTimeOffset" or "TimeSpan"
            or "Uri" or "Version") return true;

        return false;
    }
}
