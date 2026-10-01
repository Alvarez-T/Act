using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace YFex.Rules.Generator;

/// <summary>
/// YFex.Rules incremental generator (docs/internal/validation/04–06): <c>[Situation]</c> → ISituation&lt;T&gt;;
/// <c>[RuleSet&lt;TS&gt;]</c> + <c>[Rule]</c> + <c>[DecisionTable&lt;TS&gt;]</c> → the rule set's <c>Set</c>/<c>Evaluate</c>/<c>Catalog</c>,
/// rule adapters, "why not" explain bodies and table implementations. Attributes live in YFex.Rules.dll.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RulesGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var situations = context.SyntaxProvider
            .ForAttributeWithMetadataName(Names.Situation, static (n, _) => true, SituationGen.Transform)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.Situations");
        context.RegisterSourceOutput(situations, SituationGen.Emit);
        Report(context, situations.Select(static (m, _) => m.Diagnostics));

        var rules = context.SyntaxProvider
            .ForAttributeWithMetadataName(Names.Rule, static (n, _) => true, RuleSetGen.TransformRule)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.Rules");
        Report(context, rules.Select(static (m, _) => m.Diagnostics));

        var sets = context.SyntaxProvider
            .ForAttributeWithMetadataName(Names.RuleSet, static (n, _) => true, RuleSetGen.TransformSet)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.RuleSets");
        Report(context, sets.Select(static (m, _) => m.Diagnostics));

        var tables = context.SyntaxProvider
            .ForAttributeWithMetadataName(Names.DecisionTable, static (n, _) => true, TableGen.Transform)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.Tables");
        Report(context, tables.Select(static (m, _) => m.Diagnostics));

        var sources = context.SyntaxProvider
            .ForAttributeWithMetadataName(SituationSourceGen.AttributeName, static (n, _) => true, SituationSourceGen.Transform)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.SituationSources");
        context.RegisterSourceOutput(sources, SituationSourceGen.Emit);
        Report(context, sources.Select(static (m, _) => m.Diagnostics));

        var functions = context.SyntaxProvider
            .ForAttributeWithMetadataName(FunctionGen.ContainerAttribute, static (n, _) => true, FunctionGen.Transform)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexRules.Functions");
        context.RegisterSourceOutput(functions, FunctionGen.Emit);
        Report(context, functions.Select(static (m, _) => m.Diagnostics));

        var assembled = sets.Collect().Combine(rules.Collect()).Combine(tables.Collect())
            .Select(static (pair, _) => Assemble(pair.Left.Left, pair.Left.Right, pair.Right))
            .WithTrackingName("YFexRules.Assembled");

        context.RegisterSourceOutput(assembled.SelectMany(static (a, _) => a.Sets), RuleSetGen.Emit);
        context.RegisterSourceOutput(assembled.SelectMany(static (a, _) => a.Tables), TableGen.Emit);
        Report(context, assembled.Select(static (a, _) => a.Diagnostics));
    }

    private sealed record Result(EquatableArray<RuleSetGen.Assembled> Sets, EquatableArray<TableModel> Tables, EquatableArray<DiagnosticInfo> Diagnostics);

    private static Result Assemble(ImmutableArray<RuleSetModel> sets, ImmutableArray<RuleModel> rules, ImmutableArray<TableModel> tables)
    {
        var assembled = RuleSetGen.Assemble(sets, rules, tables, out var diags);
        foreach (var a in assembled) diags.AddRange(a.Diagnostics);

        // Tables: resolve derived-field Then columns against the owning rule set (or report them).
        var owner = new Dictionary<string, RuleSetModel>();
        foreach (var a in assembled)
            foreach (var t in a.Tables) owner[t.FullyQualified] = a.Set;
        var resolved = new List<TableModel>(tables.Length);
        foreach (var t in tables)
            resolved.Add(TableGen.Resolve(t, owner.TryGetValue(t.FullyQualified, out var o) ? o : null, diags));

        // Rule sets carry the resolved tables.
        var finalSets = assembled.Select(a => a with
        {
            Tables = new EquatableArray<TableModel>(a.Tables.AsSpan().ToArray()
                .Select(t => resolved.First(r => r.FullyQualified == t.FullyQualified)).ToArray()),
        }).ToArray();
        return new Result(new EquatableArray<RuleSetGen.Assembled>(finalSets), new EquatableArray<TableModel>(resolved.ToArray()),
            new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    private static void Report(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> diagnostics)
        => context.RegisterSourceOutput(diagnostics.Collect().Combine(context.CompilationProvider), static (spc, pair) =>
        {
            SourceTrees? trees = null;
            foreach (var group in pair.Left)
                foreach (var d in group) spc.ReportDiagnostic(d.ToDiagnostic(trees ??= new SourceTrees(pair.Right)));
        });

    private static void Report(IncrementalGeneratorInitializationContext context, IncrementalValueProvider<EquatableArray<DiagnosticInfo>> diagnostics)
        => context.RegisterSourceOutput(diagnostics.Combine(context.CompilationProvider), static (spc, pair) =>
        {
            SourceTrees? trees = null;
            foreach (var d in pair.Left) spc.ReportDiagnostic(d.ToDiagnostic(trees ??= new SourceTrees(pair.Right)));
        });
}
