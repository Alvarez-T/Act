using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace YFex.Expressions.Generator;

internal sealed record FunctionParamModel(string Name, string TypeFqn, bool IsEnum);

internal sealed record FunctionModel(string Name, string Method, string Description, bool Deterministic, string ReturnFqn, bool ReturnEnum,
    EquatableArray<FunctionParamModel> Parameters);

internal sealed record FunctionSetModel(string Namespace, string TypeName, string FullyQualified, EquatableArray<ContainingType> Containing,
    string Keyword, bool IsInterface, string? Category, EquatableArray<FunctionModel> Functions, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
/// <c>[ExprFunctions]</c> containers: a static class of <c>[ExprFunction]</c> methods → <c>All</c>; an interface of
/// <c>[ExprFunction] static abstract</c> methods → <c>Contracts</c> + <c>Implement&lt;TImpl&gt;()</c>. Adapters convert
/// <see cref="T:YFex.RawValue"/> arguments with ExprFunctionArgs — typed static calls, no reflection. (Moved from
/// YFex.Rules.Generator in Phase X; same output with the renamed runtime types.)
/// </summary>
internal static class FunctionGen
{
    public const string ContainerAttribute = "YFex.Expressions.ExprFunctionsAttribute";
    public const string FunctionAttribute = "YFex.Expressions.ExprFunctionAttribute";
    private const string Ns = "global::YFex.Expressions.";

    public static FunctionSetModel? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        var diags = new List<DiagnosticInfo>();
        var loc = type.Locations.FirstOrDefault();
        bool isInterface = type.TypeKind == TypeKind.Interface;
        if (!Names.IsPartial(type) || (!isInterface && !type.IsStatic)) diags.Add(DiagnosticInfo.Create("YFE0001", loc, type.Name));

        var functions = new List<FunctionModel>();
        var seen = new HashSet<string>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            if (Names.Find(method, FunctionAttribute) is not { } attr) continue;
            var mloc = method.Locations.FirstOrDefault();
            bool shapeOk = isInterface ? method.IsStatic && method.IsAbstract : method.IsStatic;
            if (!shapeOk || method.ReturnsVoid || method.IsGenericMethod || method.Parameters.Any(p => p.RefKind is RefKind.Ref or RefKind.Out))
            {
                diags.Add(DiagnosticInfo.Create("YFE0003", mloc, type.Name, method.Name,
                    isInterface ? "a static abstract method returning a value" : "a static method returning a value"));
                continue;
            }
            string name = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string n && n.Length > 0
                ? n : char.ToLowerInvariant(method.Name[0]) + method.Name.Substring(1);
            if (!seen.Add(name)) { diags.Add(DiagnosticInfo.Create("YFE0004", mloc, name, type.Name)); continue; }
            string description = Names.Named<string>(attr, "Description") ?? "";
            if (description.Length == 0) diags.Add(DiagnosticInfo.Create("YFE0005", mloc, name));
            bool deterministic = !attr.NamedArguments.Any(kv => kv.Key == "Deterministic" && kv.Value.Value is false);

            bool ok = true;
            var parameters = new List<FunctionParamModel>();
            foreach (var p in method.Parameters)
            {
                if (!Supported(p.Type)) { diags.Add(DiagnosticInfo.Create("YFE0002", p.Locations.FirstOrDefault() ?? mloc, name, p.Name, p.Type.ToDisplayString())); ok = false; continue; }
                parameters.Add(new(p.Name, Names.Of(p.Type), p.Type.TypeKind == TypeKind.Enum));
            }
            if (!Supported(method.ReturnType)) { diags.Add(DiagnosticInfo.Create("YFE0002", mloc, name, "return", method.ReturnType.ToDisplayString())); ok = false; }
            if (!ok) continue;
            functions.Add(new(name, method.Name, description, deterministic, Names.Of(method.ReturnType), method.ReturnType.TypeKind == TypeKind.Enum,
                new EquatableArray<FunctionParamModel>(parameters.ToArray())));
        }

        var container = Names.Find(type, ContainerAttribute);
        string keyword = isInterface ? "interface" : Names.Keyword(type);
        return new FunctionSetModel(Names.Namespace(type), type.Name, Names.Of(type), Names.Containing(type), keyword, isInterface,
            container is null ? null : Names.Named<string>(container, "Category"),
            new EquatableArray<FunctionModel>(functions.ToArray()), new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    private static bool Supported(ITypeSymbol t) => t.TypeKind == TypeKind.Enum || Names.IsScalar(t);

    public static void Emit(SourceProductionContext spc, FunctionSetModel m)
    {
        if (m.Functions.Length == 0 && m.Diagnostics.Length > 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> YFex.Expressions.Generator — [ExprFunctions]");
        sb.AppendLine("#nullable enable");
        if (m.Namespace.Length > 0) sb.Append("namespace ").Append(m.Namespace).AppendLine(";");
        foreach (var c in m.Containing) sb.Append(Partial(c.Keyword)).Append(' ').Append(c.Name).AppendLine(" {");
        sb.Append(Partial(m.Keyword)).Append(' ').AppendLine(m.TypeName);
        sb.AppendLine("{");
        string list = "global::System.Collections.Immutable.ImmutableArray<" + Ns + "ExprFunction>";
        string category = m.Category is null ? "null" : Names.Literal(m.Category);
        if (m.IsInterface)
        {
            sb.AppendLine("    /// <summary>The abstract functions declared here (signatures + descriptions) — author against them; the app implements them.</summary>");
            sb.Append("    public static ").Append(list).AppendLine(" Contracts { get; } =");
            sb.AppendLine("    [");
            foreach (var f in m.Functions)
                sb.Append("        ").Append(Ns).Append("ExprFunction.Contract(").Append(Names.Literal(f.Name)).Append(", ").Append(ExprType(f.ReturnFqn, f.ReturnEnum))
                  .Append(", ").Append(Params(f)).Append(", ").Append(Names.Literal(f.Description)).Append(", category: ").Append(category)
                  .Append(Deterministic(f)).AppendLine("),");
            sb.AppendLine("    ];");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>The same functions implemented by <typeparamref name=\"TImpl\"/> (static dispatch, no reflection).</summary>");
            sb.Append("    public static ").Append(list).Append(" Implement<TImpl>() where TImpl : ").Append(m.FullyQualified).AppendLine(" =>");
            sb.AppendLine("    [");
            foreach (var f in m.Functions) AppendCreate(sb, f, "TImpl", category);
            sb.AppendLine("    ];");
        }
        else
        {
            sb.AppendLine("    /// <summary>The expression functions of this class, for <c>FunctionCatalog.With(…)</c>.</summary>");
            sb.Append("    public static ").Append(list).AppendLine(" All { get; } =");
            sb.AppendLine("    [");
            foreach (var f in m.Functions) AppendCreate(sb, f, m.FullyQualified, category);
            sb.AppendLine("    ];");
        }
        sb.AppendLine("}");
        foreach (var _ in m.Containing) sb.AppendLine("}");
        spc.AddSource(Names.Hint(m.FullyQualified) + ".ExprFunctions.g.cs", sb.ToString());
    }

    private static void AppendCreate(StringBuilder sb, FunctionModel f, string owner, string category)
    {
        var call = new StringBuilder(owner).Append('.').Append(f.Method).Append('(');
        for (int i = 0; i < f.Parameters.Length; i++)
        {
            var p = f.Parameters[i];
            if (i > 0) call.Append(", ");
            call.Append(Ns).Append(p.IsEnum ? "ExprFunctionArgs.GetEnum<" : "ExprFunctionArgs.Get<").Append(p.TypeFqn).Append(">(a[").Append(i).Append("])");
        }
        call.Append(')');
        string result = (f.ReturnEnum ? Ns + "ExprFunctionArgs.ResultEnum(" : Ns + "ExprFunctionArgs.Result(") + call + ")";
        sb.Append("        ").Append(Ns).Append("ExprFunction.Create(").Append(Names.Literal(f.Name)).Append(", ").Append(ExprType(f.ReturnFqn, f.ReturnEnum))
          .Append(", ").Append(Params(f)).Append(", static a => ").Append(result).Append(", ").Append(Names.Literal(f.Description))
          .Append(", category: ").Append(category).Append(Deterministic(f)).AppendLine("),");
    }

    private static string Deterministic(FunctionModel f) => f.Deterministic ? "" : ", deterministic: false";

    /// <summary>"static class" → "static partial class" (the modifier order C# requires).</summary>
    private static string Partial(string keyword)
        => keyword.StartsWith("static ", System.StringComparison.Ordinal) ? "static partial " + keyword.Substring(7) : "partial " + keyword;

    private static string Params(FunctionModel f)
    {
        if (f.Parameters.Length == 0) return "[]";
        var sb = new StringBuilder("[");
        for (int i = 0; i < f.Parameters.Length; i++)
        {
            var p = f.Parameters[i];
            if (i > 0) sb.Append(", ");
            sb.Append("new ").Append(Ns).Append("ExprFunctionParameter(").Append(Names.Literal(p.Name)).Append(", ").Append(ExprType(p.TypeFqn, p.IsEnum)).Append(')');
        }
        return sb.Append(']').ToString();
    }

    private static string ExprType(string fqn, bool isEnum)
        => isEnum ? $"{Ns}ExprType.OfEnum<{fqn}>()" : $"{Ns}ExprType.Of<{fqn}>()";
}
