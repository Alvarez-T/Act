using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Validation.Generator;

/// <summary>
/// YFV0040 (10 §3): a rule code written as a literal — <c>w.Add(path, "sale.x")</c> on a
/// ViolationWriter/ViolationCollector, <c>[FieldCode("x")]</c>, or <c>Code = "x"</c> on a rule attribute —
/// without an entry in the module's default-culture messages file. Only active when the project has
/// messages*.json files. Default culture = invariant <c>messages.json</c> plus
/// <c>messages.&lt;YFexDefaultMessageCulture&gt;.json</c> (MSBuild, default <c>pt-BR</c>). Built-in codes are exempt.
/// </summary>
internal static class MissingMessageCheck
{
    public static readonly DiagnosticDescriptor MissingMessage = new(
        "YFV0040", "Rule code has no message",
        "Rule code '{0}' has no entry in the module's default-culture messages file",
        "YFex.Validation", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly HashSet<string> s_builtIn = new(StringComparer.Ordinal)
    {
        "required", "notempty", "len", "len.min", "len.max", "range", "pattern", "email", "digits", "alnum",
    };

    internal sealed record CodeUse(string Code, LocationInfo? Location);

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var addUses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" },
                    ArgumentList.Arguments.Count: >= 2,
                } inv && inv.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax lit
                  && lit.IsKind(SyntaxKind.StringLiteralExpression),
                static (ctx, ct) => FromAdd(ctx, ct))
            .Where(static u => u is not null)
            .Select(static (u, _) => u!);

        var attributeUses = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is AttributeSyntax { ArgumentList: not null },
                static (ctx, ct) => FromAttribute(ctx, ct))
            .SelectMany(static (list, _) => list);

        var defaultCulture = context.AnalyzerConfigOptionsProvider.Select(static (o, _) =>
            o.GlobalOptions.TryGetValue("build_property.YFexDefaultMessageCulture", out var c) && !string.IsNullOrWhiteSpace(c)
                ? c.Trim()
                : "pt-BR");

        var knownCodes = context.AdditionalTextsProvider
            .Where(static t => MessagesGenerator.IsMessagesFile(t.Path))
            .Select(static (t, ct) => MessagesGenerator.Parse(t.Path, t.GetText(ct)))
            .Collect()
            .Combine(defaultCulture)
            .Select(static (pair, _) =>
            {
                var (files, culture) = pair;
                if (files.IsEmpty) return (Active: false, Codes: EquatableArray<string>.Empty);
                var codes = files.Where(f => f.Culture.Length == 0 || string.Equals(f.Culture, culture, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(f => f.Templates.AsSpan().ToArray().Select(e => e.Key))
                    .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();
                return (Active: true, Codes: new EquatableArray<string>(codes));
            });

        var uses = addUses.Collect().Combine(attributeUses.Collect());
        context.RegisterSourceOutput(uses.Combine(knownCodes).Combine(context.CompilationProvider), static (spc, pair) =>
        {
            var (((adds, attrs), known), compilation) = pair;
            if (!known.Active) return;
            var set = new HashSet<string>(known.Codes, StringComparer.Ordinal);
            SourceTrees? trees = null;
            foreach (var use in adds.Concat(attrs))
                if (!s_builtIn.Contains(use.Code) && !set.Contains(use.Code))
                    spc.ReportDiagnostic(Diagnostic.Create(MissingMessage, use.Location?.ToLocation(trees ??= new SourceTrees(compilation)), use.Code));
        });
    }

    private static CodeUse? FromAdd(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var inv = (InvocationExpressionSyntax)ctx.Node;
        var ma = (MemberAccessExpressionSyntax)inv.Expression;
        var type = ctx.SemanticModel.GetTypeInfo(ma.Expression, ct).Type;
        if (type is null || type.ContainingNamespace?.ToDisplayString() != "YFex.Validation"
            || type.Name is not ("ViolationWriter" or "ViolationCollector"))
            return null;
        var lit = (LiteralExpressionSyntax)inv.ArgumentList.Arguments[1].Expression;
        return new CodeUse(lit.Token.ValueText, LocationInfo.From(lit.GetLocation()));
    }

    private static ImmutableArray<CodeUse> FromAttribute(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var attr = (AttributeSyntax)ctx.Node;
        ImmutableArray<CodeUse>.Builder? result = null;
        foreach (var arg in attr.ArgumentList!.Arguments)
        {
            bool isCodeNamed = arg.NameEquals?.Name.Identifier.ValueText == "Code";
            bool isPositional = arg.NameEquals is null && arg.NameColon is null;
            if (!isCodeNamed && !isPositional) continue;
            if (arg.Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;

            if (ctx.SemanticModel.GetSymbolInfo(attr, ct).Symbol is not IMethodSymbol ctor) continue;
            var cls = ctor.ContainingType;
            if (cls.ContainingNamespace.ToDisplayString() != "YFex.Validation") continue;
            bool relevant = isCodeNamed ? InheritsRuleAttribute(cls) : cls.Name == "FieldCodeAttribute";
            if (!relevant) continue;

            (result ??= ImmutableArray.CreateBuilder<CodeUse>()).Add(new CodeUse(lit.Token.ValueText, LocationInfo.From(lit.GetLocation())));
        }
        return result?.ToImmutable() ?? ImmutableArray<CodeUse>.Empty;
    }

    private static bool InheritsRuleAttribute(INamedTypeSymbol cls)
    {
        for (var t = cls; t is not null; t = t.BaseType)
            if (t.Name == "ValidationRuleAttribute" && t.ContainingNamespace.ToDisplayString() == "YFex.Validation") return true;
        return false;
    }
}
