using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Rules.Generator;

/// <summary>How one situation field is sourced by a VM.</summary>
internal enum SourceKind { UserMethod, UserPartialMethod, GeneratedPartial, Member, Lines, LinesDirect }

internal sealed record SourceFieldModel(string Field, string TypeFqn, SourceKind Kind, string? Member, string? LineTypeFqn);

internal sealed record SituationSourceModel(
    string Namespace, string TypeName, string FullyQualified, EquatableArray<ContainingType> Containing, string Keyword,
    string SituationFqn, EquatableArray<SourceFieldModel> Fields, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>[SituationSource&lt;TS&gt;] → <c>BuildSituation()</c> on a view model (D31).</summary>
internal static class SituationSourceGen
{
    public const string AttributeName = "YFex.Rules.SituationSourceAttribute`1";

    public static SituationSourceModel? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        var diags = new List<DiagnosticInfo>();
        var loc = type.Locations.FirstOrDefault();
        if (!Names.IsPartial(type)) diags.Add(DiagnosticInfo.Create("YFR0030", loc, type.Name));
        if (ctx.Attributes[0].AttributeClass!.TypeArguments[0] is not INamedTypeSymbol situation) return null;
        var compilation = ctx.SemanticModel.Compilation;

        var fields = new List<SourceFieldModel>();
        foreach (var p in Shapes.FieldsOf(situation, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (Names.Find(p, Names.Derived) is not null) continue;
            string field = p.Name;
            string typeFqn = p.Type.ToDisplayString(Names.Fqn);

            // 1. user method __Situation_<Field>()
            var method = type.GetMembers("__Situation_" + field).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0);
            if (method is not null)
            {
                bool partial = method.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is MethodDeclarationSyntax md
                    && md.Modifiers.Any(SyntaxKind.PartialKeyword));
                fields.Add(new(field, typeFqn, partial ? SourceKind.UserPartialMethod : SourceKind.UserMethod, null, null));
                continue;
            }

            // 2. same-named member (observable field-backed members expose the derived property name)
            ISymbol? member = null;
            ITypeSymbol? memberType = null;
            foreach (var m in AllMembers(type))
            {
                string name = m is IFieldSymbol f && f.Name.StartsWith("_", System.StringComparison.Ordinal) && HasObservable(m) ? Derive(f.Name) : m.Name;
                if (name != field) continue;
                memberType = m switch { IPropertySymbol pp => pp.Type, IFieldSymbol ff => ff.Type, _ => null };
                if (memberType is null) continue;
                member = m;
                break;
            }
            if (member is not null && memberType is not null)
            {
                string memberName = member is IFieldSymbol mf && HasObservable(member) ? Derive(mf.Name) : member.Name;
                var line = Shapes.Classify(p.Type) is { Shape: FieldShape.Lines } lc ? lc : (TypeClass?)null;
                if (line is { } l)
                {
                    if (SymbolEqualityComparer.Default.Equals(memberType, p.Type))
                        fields.Add(new(field, typeFqn, SourceKind.LinesDirect, memberName, l.LineTypeFqn));
                    else if (ElementSource(memberType, compilation) is { } element && element == l.LineTypeFqn)
                        fields.Add(new(field, typeFqn, SourceKind.Lines, memberName, l.LineTypeFqn));
                    else
                        diags.Add(DiagnosticInfo.Create("YFR0031", member.Locations.FirstOrDefault() ?? loc, type.Name, memberName,
                            $"lines need ImmutableArray<{l.LineTypeFqn!.Replace("global::", "")}> or a collection of view models with [SituationSource<{l.LineTypeFqn!.Replace("global::", "")}>]"));
                    continue;
                }
                var conversion = compilation.ClassifyConversion(memberType, p.Type);
                if (conversion.IsImplicit)
                    fields.Add(new(field, typeFqn, SourceKind.Member, memberName, null));
                else
                    diags.Add(DiagnosticInfo.Create("YFR0031", member.Locations.FirstOrDefault() ?? loc, type.Name, memberName,
                        $"'{memberType.ToDisplayString()}' does not convert implicitly to '{p.Type.ToDisplayString()}' — declare __Situation_{field}()"));
                continue;
            }

            // 3. partial method the VM must implement
            fields.Add(new(field, typeFqn, SourceKind.GeneratedPartial, null, null));
        }

        return new SituationSourceModel(Names.Namespace(type), type.Name, Names.Of(type), Names.Containing(type), Names.Keyword(type),
            Names.Of(situation), new EquatableArray<SourceFieldModel>(fields.ToArray()), new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    private static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var m in t.GetMembers())
                if (!m.IsStatic && m is IPropertySymbol { IsIndexer: false } or IFieldSymbol { IsImplicitlyDeclared: false }) yield return m;
    }

    private static bool HasObservable(ISymbol m) => m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "YFex.State.ObservableAttribute");

    private static string Derive(string fieldName)
    {
        string s = fieldName.TrimStart('_');
        return s.Length == 0 ? fieldName : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>Line type produced by the element VM of a collection (its own [SituationSource&lt;TLine&gt;]).</summary>
    private static string? ElementSource(ITypeSymbol collection, Compilation compilation)
    {
        var enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        ITypeSymbol? element = null;
        foreach (var i in collection.AllInterfaces.Concat(collection is INamedTypeSymbol n ? [n] : []))
            if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, enumerable) && i is INamedTypeSymbol ni) { element = ni.TypeArguments[0]; break; }
        if (element is not INamedTypeSymbol e) return null;
        var attr = e.GetAttributes().FirstOrDefault(a => a.AttributeClass is { } c && Names.Full(c) == AttributeName);
        return attr?.AttributeClass!.TypeArguments[0] is INamedTypeSymbol line ? Names.Of(line) : null;
    }

    public static void Emit(SourceProductionContext spc, SituationSourceModel m)
    {
        if (m.Diagnostics.Length > 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> YFex.Rules.Generator — situation source (D31)");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        if (m.Namespace.Length > 0) sb.Append("namespace ").Append(m.Namespace).AppendLine(";");
        foreach (var c in m.Containing) sb.Append("partial ").Append(c.Keyword).Append(' ').Append(c.Name).AppendLine(" {");
        sb.Append("partial ").Append(m.Keyword.Replace("static ", "")).Append(' ').Append(m.TypeName).AppendLine();
        sb.AppendLine("{");
        sb.Append("    /// <summary>Snapshot of this view model's state for the rules (generated by [SituationSource]).</summary>").AppendLine();
        sb.Append("    public ").Append(m.SituationFqn).AppendLine(" BuildSituation()");
        sb.AppendLine("    {");
        var args = new List<string>();
        foreach (var f in m.Fields)
        {
            switch (f.Kind)
            {
                case SourceKind.Member: args.Add($"{f.Field}: this.{f.Member}"); break;
                case SourceKind.LinesDirect: args.Add($"{f.Field}: this.{f.Member}"); break;
                case SourceKind.Lines:
                    sb.Append("        var __").Append(f.Field).Append(" = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<").Append(f.LineTypeFqn).AppendLine(">();");
                    sb.Append("        if (this.").Append(f.Member).Append(" is { } __src").Append(f.Field).Append(") foreach (var __item in __src").Append(f.Field)
                      .Append(") __").Append(f.Field).AppendLine(".Add(__item.BuildSituation());");
                    args.Add($"{f.Field}: __{f.Field}.ToImmutable()");
                    break;
                default: args.Add($"{f.Field}: __Situation_{f.Field}()"); break;
            }
        }
        sb.Append("        return new ").Append(m.SituationFqn).Append('(').Append(string.Join(", ", args)).AppendLine(");");
        sb.AppendLine("    }");
        foreach (var f in m.Fields)
            if (f.Kind is SourceKind.GeneratedPartial or SourceKind.UserPartialMethod)
                sb.Append("    private partial ").Append(f.TypeFqn).Append(" __Situation_").Append(f.Field).AppendLine("();");
        sb.AppendLine("}");
        foreach (var _ in m.Containing) sb.AppendLine("}");
        spc.AddSource(SituationGen.Hint(m.FullyQualified) + ".SituationSource.g.cs", sb.ToString());
    }
}
