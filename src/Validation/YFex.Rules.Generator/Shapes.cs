using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Rules.Generator;

internal static class Names
{
    public const string Situation = "YFex.Rules.SituationAttribute";
    public const string EffectiveDate = "YFex.Rules.RuleEffectiveDateAttribute";
    public const string RuleTier = "YFex.Rules.RuleTierAttribute";
    public const string RawValueInterface = "YFex.IRawValue<TSelf>";
    public const string AmountInterface = "YFex.IAmount<TSelf>";
    public const string Requested = "YFex.Rules.RequestedAttribute";
    public const string Derived = "YFex.Rules.DerivedAttribute";
    public const string Exposed = "YFex.Rules.ExposedAttribute";
    public const string Rule = "YFex.Rules.RuleAttribute";
    public const string Effective = "YFex.Rules.EffectiveAttribute";
    public const string Sealed = "YFex.Rules.SealedAttribute";
    public const string RuleSet = "YFex.Rules.RuleSetAttribute`1";
    public const string DecisionTable = "YFex.Rules.DecisionTableAttribute`1";
    public const string When = "YFex.Rules.WhenAttribute";
    public const string Then = "YFex.Rules.ThenAttribute";
    public const string EnumCode = "YFex.Extensions.EnumCodeAttribute";
    public const string Validatable = "YFex.Validation.ValidatableAttribute";

    public static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static readonly SymbolDisplayFormat FqnNoNullable = SymbolDisplayFormat.FullyQualifiedFormat;

    public static string Of(ITypeSymbol t) => t.ToDisplayString(FqnNoNullable);

    public static bool Is(ITypeSymbol? t, string metadataName)
        => t is not null && t.OriginalDefinition.ToDisplayString() == metadataName;

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

    /// <summary>Enum-typed attribute arguments arrive as their underlying integral type (byte for our enums).</summary>
    public static int EnumValue(TypedConstant c, int fallback = 0)
        => c.Value is null ? fallback : System.Convert.ToInt32(c.Value, System.Globalization.CultureInfo.InvariantCulture);

    public static int NamedEnum(AttributeData a, string name, int fallback = 0)
    {
        foreach (var kv in a.NamedArguments)
            if (kv.Key == name) return EnumValue(kv.Value, fallback);
        return fallback;
    }

    public static bool HasNamed(AttributeData a, string name)
    {
        foreach (var kv in a.NamedArguments) if (kv.Key == name) return true;
        return false;
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

    public static bool IsAccessible(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal) return false;
        return true;
    }

    public static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);
}

/// <summary>Classification of a type as a situation field / table cell value.</summary>
internal readonly record struct TypeClass(FieldShape Shape, string Kind, string ValueTypeFqn, string? InnerMember, bool IsNullable,
    bool IsReferenceNullable, EnumMemberModel[] EnumMembers, string? LineTypeFqn, bool Supported);

internal static class Shapes
{
    public static TypeClass Classify(ITypeSymbol type)
    {
        bool refNullable = type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = Classify(nullable.TypeArguments[0]);
            return inner.Shape == FieldShape.Lines ? Unsupported() : inner with { IsNullable = true };
        }

        if (ScalarKind(type) is { } kind)
            return new(FieldShape.Scalar, kind, Names.Of(type), null, false, refNullable, [], null, true);

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol e)
            return new(FieldShape.Enum, "EnumCode", Names.Of(type), null, false, false, EnumMembers(e), null, true);

        if (type is INamedTypeSymbol named && Names.Is(named, "System.Collections.Immutable.ImmutableArray<T>")
            && named.TypeArguments[0] is INamedTypeSymbol line && Names.Find(line, Names.Situation) is not null)
            return new(FieldShape.Lines, "None", Names.Of(type), null, false, false, [], Names.Of(line), true);

        // Id wrapping one scalar/enum: exactly one public instance property + a constructor taking it.
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Struct } w)
        {
            var props = w.GetMembers().OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.Name != "EqualityContract")
                .ToArray();
            if (props.Length == 1)
            {
                var p = props[0];
                bool hasCtor = w.InstanceConstructors.Any(c => c.Parameters.Length == 1
                    && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, p.Type));
                if (hasCtor)
                {
                    if (ScalarKind(p.Type) is { } k)
                        return new(FieldShape.Wrapper, k, Names.Of(p.Type), p.Name, false, false, [], null, true);
                    if (p.Type.TypeKind == TypeKind.Enum && p.Type is INamedTypeSymbol pe)
                        return new(FieldShape.WrapperEnum, "EnumCode", Names.Of(p.Type), p.Name, false, false, EnumMembers(pe), null, true);
                }
            }
        }
        return Unsupported();

        static TypeClass Unsupported() => new(FieldShape.Scalar, "None", "", null, false, false, [], null, false);
    }

    /// <summary>
    /// Emission expression of the <c>YFex.RawType</c> of a classified kind: a primitive (<c>RawType.Int32</c>…) or, for
    /// <c>IRawValue</c> types (whose kind IS the expression), the type's own <c>RawType</c>.
    /// </summary>
    public static string RawTypeOf(string kind)
        => kind.StartsWith("global::", System.StringComparison.Ordinal) ? kind
            : kind == "None" ? "global::YFex.RawType.Of(global::YFex.ValueKind.None)"
            : "global::YFex.RawType." + kind;

    /// <summary>True when <paramref name="t"/> implements <c>YFex.IRawValue&lt;T&gt;</c> (any type with a raw form).</summary>
    public static bool IsRawValue(ITypeSymbol t) => Implements(t, Names.RawValueInterface);

    /// <summary>True when <paramref name="t"/> implements <c>YFex.IAmount&lt;T&gt;</c>.</summary>
    public static bool IsAmount(ITypeSymbol t) => Implements(t, Names.AmountInterface);

    /// <summary>A rule set amount: an <c>IAmount</c> type or a plain number (decimal/int/long/double).</summary>
    public static bool IsAmountType(ITypeSymbol t)
        => t.SpecialType is SpecialType.System_Decimal or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double
           || IsAmount(t);

    private static bool Implements(ITypeSymbol t, string iface)
    {
        foreach (var i in t.AllInterfaces)
            if (i.OriginalDefinition.ToDisplayString() == iface) return true;
        return false;
    }

    /// <summary>
    /// Kind of a scalar field/cell type: a primitive <c>ValueKind</c> name, or — for <c>IRawValue</c> types (Money, Mass,
    /// app types) — the <c>RawType</c> expression itself. Null when unsupported.
    /// </summary>
    public static string? ScalarKind(ITypeSymbol t)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return "Bool";
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_Int32: return "Int32";
            case SpecialType.System_Int64: return "Int64";
            case SpecialType.System_Decimal: return "Decimal";
            case SpecialType.System_Single:
            case SpecialType.System_Double: return "Double";
            case SpecialType.System_String: return "Text";
            case SpecialType.System_DateTime: return "DateTime";
        }
        return t.ToDisplayString(Names.FqnNoNullable) switch
        {
            "global::System.DateOnly" => "Date",
            "global::System.TimeOnly" => "Time",
            "global::System.Guid" => "Guid",
            _ => IsRawValue(t) ? Names.Of(t) + ".RawType" : null,
        };
    }

    public static EnumMemberModel[] EnumMembers(INamedTypeSymbol e)
    {
        var list = new List<EnumMemberModel>();
        foreach (var m in e.GetMembers().OfType<IFieldSymbol>())
        {
            if (!m.HasConstantValue) continue;
            string code = Names.Find(m, Names.EnumCode) is { ConstructorArguments.Length: 1 } a && a.ConstructorArguments[0].Value is string s ? s : m.Name;
            list.Add(new EnumMemberModel(m.Name, code, System.Convert.ToInt64(m.ConstantValue)));
        }
        return list.ToArray();
    }

    /// <summary>Expression turning a value of the classified type into a RawValue.</summary>
    public static string ToRaw(TypeClass c, string expr)
    {
        string core(string e) => c.Shape switch
        {
            FieldShape.Enum => $"global::YFex.RawValue.FromEnum({e})",
            FieldShape.Wrapper => c.Kind == "Text" ? $"global::YFex.RawValue.From({e}.{c.InnerMember})" : $"global::YFex.RawValue.From({e}.{c.InnerMember})",
            FieldShape.WrapperEnum => $"global::YFex.RawValue.FromEnum({e}.{c.InnerMember})",
            _ => c.Kind == "Double" || c.ValueTypeFqn is "short" or "byte" or "float"
                ? $"global::YFex.RawValueConvert.From({e})"
                : $"global::YFex.RawValue.From({e})",
        };
        return c.IsNullable ? $"({expr} is {{ }} __v ? {core("__v")} : default)" : core(expr);
    }

    /// <summary>
    /// Fields of a situation in id order: record primary-constructor parameters, then <c>[Derived]</c> properties (source);
    /// for metadata types, the generated <c>FieldIds</c> constants.
    /// </summary>
    public static List<IPropertySymbol> FieldsOf(INamedTypeSymbol type, out bool hasPrimaryConstructor)
    {
        hasPrimaryConstructor = false;
        var result = new List<IPropertySymbol>();
        var props = type.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer).ToList();

        // Metadata (other assembly): generated FieldIds are authoritative.
        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            if (type.GetTypeMembers("FieldIds").FirstOrDefault() is { } ids)
            {
                foreach (var f in ids.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).OrderBy(f => System.Convert.ToInt32(f.ConstantValue)))
                    if (props.FirstOrDefault(p => p.Name == f.Name) is { } p) result.Add(p);
                hasPrimaryConstructor = type.IsRecord;
            }
            return result;
        }

        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is TypeDeclarationSyntax { ParameterList: { } pl })
            {
                hasPrimaryConstructor = type.IsRecord;
                foreach (var p in pl.Parameters)
                    if (props.FirstOrDefault(x => x.Name == p.Identifier.ValueText) is { } prop) result.Add(prop);
            }
        }

        if (!hasPrimaryConstructor)
        {
            // Non-record: public instance properties in declaration order.
            foreach (var p in OrderedBySyntax(props))
                if (p.DeclaredAccessibility == Accessibility.Public && p.GetMethod is not null && p.Name != "EqualityContract") result.Add(p);
            return result;
        }

        foreach (var p in OrderedBySyntax(props))
            if (Names.Find(p, Names.Derived) is not null && !result.Contains(p, SymbolEqualityComparer.Default)) result.Add(p);
        return result;
    }

    private static IEnumerable<IPropertySymbol> OrderedBySyntax(List<IPropertySymbol> props)
        => props.Where(p => p.DeclaringSyntaxReferences.Length > 0)
                .OrderBy(p => p.DeclaringSyntaxReferences[0].SyntaxTree.FilePath, System.StringComparer.Ordinal)
                .ThenBy(p => p.DeclaringSyntaxReferences[0].Span.Start);

    /// <summary>The (single) lines member of a situation and its line type.</summary>
    public static (string? Member, INamedTypeSymbol? Line) LinesOf(INamedTypeSymbol situation)
    {
        foreach (var p in FieldsOf(situation, out _))
            if (p.Type is INamedTypeSymbol n && Names.Is(n, "System.Collections.Immutable.ImmutableArray<T>")
                && n.TypeArguments[0] is INamedTypeSymbol line && Names.Find(line, Names.Situation) is not null)
                return (p.Name, line);
        return (null, null);
    }
}
