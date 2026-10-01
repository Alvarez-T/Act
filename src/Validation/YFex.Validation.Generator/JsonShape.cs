using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace YFex.Validation.Generator;

internal sealed record JsonEnumMember(string Code, long Value);

/// <summary>JSON shape of a member (Phase 6: JSON Schema export for AI tools / forms).</summary>
internal sealed record JsonShapeModel(string Kind, string? RawTypeExpr, bool Nullable, EquatableArray<JsonEnumMember> Enum,
    string? NestedFqn, JsonShapeModel? Element, string? TypeofFqn = null);

internal static class JsonShape
{
    private const string Ns = "global::YFex.Validation.";

    /// <summary>Describes <paramref name="declared"/>: scalar kind, app value type (IRawValue), enum codes, nested validatable, array element.</summary>
    public static JsonShapeModel Of(ITypeSymbol declared, System.Func<ITypeSymbol, bool> isValidatable, int depth = 0)
        => Describe(declared, isValidatable, depth) with
        {
            TypeofFqn = declared.TypeKind == TypeKind.TypeParameter ? null
                : declared.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        };

    private static JsonShapeModel Describe(ITypeSymbol declared, System.Func<ITypeSymbol, bool> isValidatable, int depth)
    {
        bool nullable = declared.NullableAnnotation == NullableAnnotation.Annotated;
        var t = declared;
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            nullable = true;
            t = n.TypeArguments[0];
        }
        var none = new EquatableArray<JsonEnumMember>(System.Array.Empty<JsonEnumMember>());

        if (t.TypeKind == TypeKind.Enum)
        {
            var members = new List<JsonEnumMember>();
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
            {
                string code = f.Name;
                foreach (var a in f.GetAttributes())
                    if (a.AttributeClass?.ToDisplayString() == "YFex.Extensions.EnumCodeAttribute" && a.ConstructorArguments.Length > 0
                        && a.ConstructorArguments[0].Value is string c) code = c;
                members.Add(new(code, System.Convert.ToInt64(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)));
            }
            return new("Enum", null, nullable, new EquatableArray<JsonEnumMember>(members.ToArray()), null, null);
        }

        string? kind = t.SpecialType switch
        {
            SpecialType.System_Boolean => "Bool",
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 => "Integer",
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => "Number",
            SpecialType.System_String or SpecialType.System_Char => "String",
            SpecialType.System_DateTime => "DateTime",
            _ => t.ToDisplayString() switch
            {
                "System.DateOnly" => "Date",
                "System.TimeOnly" => "Time",
                "System.Guid" => "Guid",
                "System.DateTimeOffset" => "DateTime",
                _ => null,
            },
        };
        if (kind is not null) return new(kind, null, nullable, none, null, null);

        if (t.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "YFex.IRawValue<TSelf>"))
            return new("Raw", t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".RawType", nullable, none, null, null);

        if (depth < 4 && ElementOf(t) is { } element)
            return new("Array", null, nullable, none, null, Of(element, isValidatable, depth + 1));

        if (isValidatable(t))
            return new("Object", null, nullable, none, t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), null);

        return new("Any", null, nullable, none, null, null);
    }

    private static ITypeSymbol? ElementOf(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return a.ElementType;
        if (t.SpecialType == SpecialType.System_String) return null;
        foreach (var i in t.AllInterfaces.Concat(t is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface ? new[] { nt } : System.Array.Empty<INamedTypeSymbol>()))
            if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) return i.TypeArguments[0];
        return null;
    }

    /// <summary>C# expression building the runtime <c>MemberShape</c>.</summary>
    public static string Expression(string name, JsonShapeModel s)
    {
        string nullable = s.Nullable ? "true" : "false";
        string lit = Parser.Literal(name);
        string clr = s.TypeofFqn is { } t ? $", clrType: typeof({t})" : "";
        switch (s.Kind)
        {
            case "Raw":
                return $"{Ns}MemberShape.ForRaw({lit}, {s.RawTypeExpr}, {nullable}{clr})";
            case "Enum":
            {
                var sb = new StringBuilder();
                foreach (var e in s.Enum) sb.Append(sb.Length > 0 ? ", " : "").Append($"new({Parser.Literal(e.Code)}, {e.Value}L)");
                return $"new {Ns}MemberShape({lit}, {Ns}JsonKind.Enum, {nullable}, enumMembers: [{sb}]{clr})";
            }
            case "Object":
                return $"new {Ns}MemberShape({lit}, {Ns}JsonKind.Object, {nullable}, nested: static () => {s.NestedFqn}.Shape{clr})";
            case "Array":
                return $"new {Ns}MemberShape({lit}, {Ns}JsonKind.Array, {nullable}, element: {Expression("", s.Element!)}{clr})";
            default:
                return $"new {Ns}MemberShape({lit}, {Ns}JsonKind.{s.Kind}, {nullable}{clr})";
        }
    }
}
