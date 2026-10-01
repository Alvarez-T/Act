using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace YFex.Validation.Generator;

/// <summary>
/// YFex.Validation incremental generator: emits IValidatable&lt;T&gt; (rules, constraints, paths, ids)
/// for types with validation attributes / invariants, a module initializer that fills
/// ValidationSlot&lt;T&gt;, and message catalogs from <c>messages*.json</c> additional files.
/// Attributes live in YFex.Validation.dll (not injected).
/// </summary>
[Generator]
public sealed class ValidationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(Parser.IsCandidate, Parser.Transform)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .WithTrackingName(TrackingNames.Models);

        // ⚑P5: Required inference is opt-in via MSBuild <YFexValidationInferRequired>true</…>.
        var inferRequired = context.AnalyzerConfigOptionsProvider.Select(static (o, _) =>
            o.GlobalOptions.TryGetValue("build_property.YFexValidationInferRequired", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

        // Fluent `.Validation(v => …)` configurations (03 §4, D27): parsed per call, merged into the models.
        var fluent = context.SyntaxProvider
            .CreateSyntaxProvider(Parser.IsFluentCandidate, Parser.TransformFluent)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .WithTrackingName(TrackingNames.Fluent);

        // Diagnostics are reported from nodes combined with the compilation (in-source locations → #pragma works);
        // they are empty almost always, so re-running them per edit is cheap and emission stays cached.
        context.RegisterSourceOutput(
            fluent.Select(static (c, _) => c.Diagnostics).Collect().Combine(context.CompilationProvider),
            static (spc, pair) => Report(spc, pair.Left, pair.Right));

        var finalModels = models.Collect()
            .Combine(fluent.Collect())
            .Combine(context.CompilationProvider)
            .SelectMany(static (pair, ct) => pair.Left.Right.IsDefaultOrEmpty
                ? pair.Left.Left
                : FluentMerge.Merge(pair.Left.Left, pair.Left.Right, pair.Right, ct))
            .Combine(inferRequired)
            .Select(static (pair, _) => pair.Right ? InferRequired(pair.Left) : pair.Left)
            .WithTrackingName(TrackingNames.FinalModels);

        context.RegisterSourceOutput(finalModels, static (spc, model) => Emitter.Emit(spc, model));
        context.RegisterSourceOutput(
            finalModels.Select(static (m, _) => m.Diagnostics).Collect().Combine(context.CompilationProvider),
            static (spc, pair) => Report(spc, pair.Left, pair.Right));

        var all = finalModels.Collect();
        context.RegisterSourceOutput(all, static (spc, list) => Emitter.EmitModuleInitializer(spc, list));
        context.RegisterSourceOutput(all.Combine(context.CompilationProvider),
            static (spc, pair) => GraphChecks.Report(spc, pair.Left, new SourceTrees(pair.Right)));

        MessagesGenerator.Initialize(context);
        MissingMessageCheck.Initialize(context);
    }

    private static void Report(SourceProductionContext spc, ImmutableArray<EquatableArray<DiagnosticInfo>> groups, Compilation compilation)
    {
        SourceTrees? trees = null;
        foreach (var group in groups)
            foreach (var d in group)
                spc.ReportDiagnostic(d.ToDiagnostic(trees ??= new SourceTrees(compilation)));
    }

    private static TypeModel InferRequired(TypeModel model)
    {
        var inferred = new List<string>();
        var members = new MemberModel[model.Members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            var m = model.Members[i];
            if (m.CanInferRequired && m.Each is null)
            {
                var required = new RuleModel(RuleKind.Required, Parser.Literal("required"), true,
                    "global::YFex.Validation.Severity.Error", null, null, 0, 0, null, null, null, null, null, null,
                    null, false, false, null, null, false, null, null);
                var rules = new RuleModel[m.Rules.Length + 1];
                rules[0] = required;
                m.Rules.AsSpan().CopyTo(rules.AsSpan(1));
                m = m with { Rules = new EquatableArray<RuleModel>(rules), CanInferRequired = false };
                inferred.Add(m.Name);
            }
            members[i] = m;
        }
        if (inferred.Count == 0) return model;

        var diagnostics = model.Diagnostics.AsSpan().ToArray().ToList();
        diagnostics.Add(new DiagnosticInfo("YFV0030", model.Location,
            new EquatableArray<string>(new[] { model.FullyQualifiedName.Replace("global::", "") + ": " + string.Join(", ", inferred) })));
        return model with
        {
            Members = new EquatableArray<MemberModel>(members),
            Diagnostics = new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()),
        };
    }
}

/// <summary>Step names for incremental-caching tests.</summary>
internal static class TrackingNames
{
    public const string Models = "YFexValidation.Models";
    public const string FinalModels = "YFexValidation.FinalModels";
    public const string Fluent = "YFexValidation.Fluent";
}

/// <summary>Checks that need the whole set of validatable types (YFV0012, YFV0014).</summary>
internal static class GraphChecks
{
    private const int MaxDepth = 4;

    public static void Report(SourceProductionContext spc, ImmutableArray<TypeModel> models, SourceTrees trees)
    {
        var byName = new Dictionary<string, TypeModel>(StringComparer.Ordinal);
        foreach (var m in models) byName[m.FullyQualifiedName] = m;

        // YFV0012: cycle through non-nullable nested (non-collection) members.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in models)
        {
            var path = new List<string> { Short(m.FullyQualifiedName) };
            if (FindCycle(m, m.FullyQualifiedName, byName, path, new HashSet<string>(StringComparer.Ordinal))
                && reported.Add(m.FullyQualifiedName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NestedCycle, m.Location?.ToLocation(trees), string.Join(" → ", path)));
            }
        }

        // YFV0014: more than 4 collection levels reachable from a type.
        foreach (var m in models)
        {
            var trail = new List<string>();
            int depth = Depth(m, byName, new HashSet<string>(StringComparer.Ordinal), trail);
            if (depth > MaxDepth)
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.TooDeep, m.Location?.ToLocation(trees), string.Join(".", trail)));
        }
    }

    private static bool FindCycle(TypeModel current, string root, Dictionary<string, TypeModel> byName,
        List<string> path, HashSet<string> visiting)
    {
        if (!visiting.Add(current.FullyQualifiedName)) return false;
        foreach (var mem in current.Members)
        {
            if (mem.NestedValidatableFqn is not { } next) continue;
            bool nonNullable = !mem.IsNullableValue && !mem.TypeFqn.EndsWith("?", StringComparison.Ordinal);
            if (!nonNullable) continue;
            path.Add(mem.Name);
            if (next == root) return true;
            if (byName.TryGetValue(next, out var nm) && FindCycle(nm, root, byName, path, visiting)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private static int Depth(TypeModel m, Dictionary<string, TypeModel> byName, HashSet<string> visiting, List<string> trail)
    {
        if (!visiting.Add(m.FullyQualifiedName)) return 0;
        int best = 0;
        List<string>? bestTrail = null;
        foreach (var mem in m.Members)
        {
            int d;
            var sub = new List<string>();
            if (mem.Each is { } each)
            {
                d = 1;
                if (each.ElementValidatableFqn is { } e && byName.TryGetValue(e, out var em))
                    d += Depth(em, byName, visiting, sub);
                sub.Insert(0, mem.Name + "[]");
            }
            else if (mem.NestedValidatableFqn is { } n && byName.TryGetValue(n, out var nm))
            {
                d = Depth(nm, byName, visiting, sub);
                sub.Insert(0, mem.Name);
            }
            else continue;

            if (d > best) { best = d; bestTrail = sub; }
        }
        visiting.Remove(m.FullyQualifiedName);
        if (bestTrail is not null) trail.AddRange(bestTrail);
        return best;
    }

    private static string Short(string fqn) => fqn.Replace("global::", "");
}
