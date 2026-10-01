using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace YFex.Rules.Generator;

/// <summary>[Rule] types + [RuleSet&lt;TS&gt;] classes → generated <c>Set</c>/<c>Evaluate</c>/<c>Catalog</c>/<c>RuleIds</c> (04 §3–4).</summary>
internal static class RuleSetGen
{
    // ── [Rule] ──────────────────────────────────────────────────────────────

    public static RuleModel? TransformRule(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        var a = ctx.Attributes[0];
        var diags = new List<DiagnosticInfo>();
        var loc = type.Locations.FirstOrDefault();
        string code = a.ConstructorArguments.FirstOrDefault().Value as string ?? type.Name;
        string? group = Names.Named<string>(a, "Group");
        if (string.IsNullOrEmpty(group)) diags.Add(DiagnosticInfo.Create("YFR0015", loc, code));
        if (!Names.IsAccessible(type)) diags.Add(DiagnosticInfo.Create("YFR0013", loc, type.Name));

        // IRule<TS> / IRule<TS, TLine>
        INamedTypeSymbol? situation = null, line = null;
        foreach (var i in type.AllInterfaces)
        {
            var def = Names.Full(i);
            if (def == "YFex.Rules.IRule`1") situation = i.TypeArguments[0] as INamedTypeSymbol;
            else if (def == "YFex.Rules.IRule`2") { situation = i.TypeArguments[0] as INamedTypeSymbol; line = i.TypeArguments[1] as INamedTypeSymbol; }
        }
        if (situation is null)
        {
            diags.Add(DiagnosticInfo.Create("YFR0010", loc, code, "implement IRule<TSituation> or IRule<TSituation, TLine>"));
            return Model(type, code, "", null, null, group ?? "", a, diags, false, false, [], [], false, "s", null, loc);
        }

        var (linesMember, _) = Shapes.LinesOf(situation);
        bool isLine = line is not null;
        var applies = FindMethod(type, "Applies", situation, line, isBool: true, out bool appliesCtx);
        var evaluate = FindMethod(type, "Evaluate", situation, line, isBool: false, out bool evaluateCtx);
        if (applies is null)
            diags.Add(DiagnosticInfo.Create("YFR0010", loc, code, $"missing 'public static bool Applies(in {situation.Name} s{(isLine ? ", in " + line!.Name + " line" : "")}[, in RuleContext c])'"));
        if (evaluate is null)
            diags.Add(DiagnosticInfo.Create("YFR0010", loc, code, $"missing 'public static void Evaluate(in {situation.Name} s{(isLine ? ", in " + line!.Name + " line" : "")}[, in RuleContext c], ref DirectiveWriter d)'"));
        if (isLine && linesMember is null)
            diags.Add(DiagnosticInfo.Create("YFR0010", loc, code, $"'{situation.Name}' has no ImmutableArray<{line!.Name}> lines field"));

        var parameters = type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.IsStatic && f.Type is INamedTypeSymbol n && Names.Full(n) == "YFex.Rules.RuleParameter`1")
            .Select(f => f.Name).ToArray();

        var conditions = new List<ConditionModel>();
        bool splittable = false;
        string sParam = "s";
        string? lParam = null;
        if (applies is not null)
        {
            sParam = applies.Parameters[0].Name;
            if (isLine) lParam = applies.Parameters[1].Name;
            var syntax = applies.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
            if (syntax is not null)
            {
                var model = ctx.SemanticModel.Compilation.GetSemanticModel(syntax.SyntaxTree);
                splittable = ConditionSplitter.TrySplit(applies, model, code, out conditions);
                if (!splittable)
                {
                    conditions = [new ConditionModel("custom", "rules." + code + ".cond", null, "default", "true")];
                    diags.Add(DiagnosticInfo.Create("YFR0016", applies.Locations.FirstOrDefault(), code, "rules." + code + ".cond"));
                }
            }
        }

        foreach (var name in new[] { "From", "To" })
            if (Names.Find(type, Names.Effective) is { } eff && Names.Named<string>(eff, name) is { } d && !IsIsoDate(d))
                diags.Add(DiagnosticInfo.Create("YFR0017", loc, code, d));

        return Model(type, code, Names.Of(situation), line is null ? null : Names.Of(line), linesMember, group ?? "", a, diags,
            appliesCtx, evaluateCtx, parameters, conditions, splittable, sParam, lParam, loc);
    }

    private static RuleModel Model(INamedTypeSymbol type, string code, string situation, string? line, string? linesMember, string group,
        AttributeData a, List<DiagnosticInfo> diags, bool appliesCtx, bool evaluateCtx, string[] parameters, List<ConditionModel> conditions,
        bool splittable, string sParam, string? lParam, Location? loc)
    {
        var eff = Names.Find(type, Names.Effective);
        return new RuleModel(code, Names.Of(type), type.Name, situation, line, linesMember, group, Names.Named<string>(a, "RuleSet"),
            Names.Named<int>(a, "Priority"), Names.HasNamed(a, "Version") ? Names.Named<int>(a, "Version") : 1,
            Names.Find(type, Names.Sealed) is not null,
            eff is null ? null : Names.Named<string>(eff, "From"), eff is null ? null : Names.Named<string>(eff, "To"),
            appliesCtx, evaluateCtx, new EquatableArray<string>(parameters), new EquatableArray<ConditionModel>(conditions.ToArray()),
            splittable, sParam, lParam, LocationInfo.From(loc), new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    private static bool IsIsoDate(string s)
        => s.Length == 10 && System.DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    /// <summary>Finds <c>static Name(in TS s[, in TLine l][, in RuleContext c][, ref DirectiveWriter d])</c>.</summary>
    private static IMethodSymbol? FindMethod(INamedTypeSymbol type, string name, INamedTypeSymbol s, INamedTypeSymbol? line, bool isBool, out bool hasContext)
    {
        hasContext = false;
        foreach (var m in type.GetMembers(name).OfType<IMethodSymbol>())
        {
            if (!m.IsStatic) continue;
            if (isBool ? m.ReturnType.SpecialType != SpecialType.System_Boolean : !m.ReturnsVoid) continue;
            var ps = m.Parameters;
            int i = 0;
            if (ps.Length <= i || !SymbolEqualityComparer.Default.Equals(ps[i].Type, s)) continue;
            i++;
            if (line is not null)
            {
                if (ps.Length <= i || !SymbolEqualityComparer.Default.Equals(ps[i].Type, line)) continue;
                i++;
            }
            bool ctx = ps.Length > i && Names.Is(ps[i].Type, "YFex.Rules.RuleContext");
            if (ctx) i++;
            if (!isBool)
            {
                if (ps.Length <= i || !Names.Is(ps[i].Type, "YFex.Rules.DirectiveWriter") || ps[i].RefKind != RefKind.Ref) continue;
                i++;
            }
            if (ps.Length != i) continue;
            hasContext = ctx;
            return m;
        }
        return null;
    }

    // ── [RuleSet<TS>] ───────────────────────────────────────────────────────

    public static RuleSetModel? TransformSet(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        var a = ctx.Attributes[0];
        var diags = new List<DiagnosticInfo>();
        var loc = type.Locations.FirstOrDefault();
        if (!type.IsStatic || !Names.IsPartial(type)) diags.Add(DiagnosticInfo.Create("YFR0020", loc, type.Name));
        var situation = a.AttributeClass!.TypeArguments[0] as INamedTypeSymbol;
        string setName = a.ConstructorArguments.FirstOrDefault().Value as string ?? type.Name;

        var groups = new List<string>();
        var derived = new List<DerivedFieldModel>();
        bool rounding = false, allocation = false, hasBase = false;
        string? lineBase = null;
        string? amountFqn = null;
        void Amount(IMethodSymbol method)
        {
            if (!Shapes.IsAmountType(method.ReturnType))
                diags.Add(DiagnosticInfo.Create("YFR0022", method.Locations.FirstOrDefault() ?? loc, type.Name, method.Name, method.ReturnType.ToDisplayString()));
            else if (amountFqn is not null && amountFqn != Names.Of(method.ReturnType))
                diags.Add(DiagnosticInfo.Create("YFR0023", method.Locations.FirstOrDefault() ?? loc, type.Name));
            else amountFqn = Names.Of(method.ReturnType);
        }
        var (linesMember, line) = situation is null ? (null, null) : Shapes.LinesOf(situation);
        foreach (var m in type.GetMembers().OrderBy(m => m.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0))
        {
            switch (m)
            {
                case IFieldSymbol { IsStatic: true } f when f.Type is INamedTypeSymbol ft && Names.Full(ft) == "YFex.Rules.RuleGroup":
                    groups.Add(f.Name);
                    break;
                case IFieldSymbol { IsStatic: true } f when f.Type is INamedTypeSymbol ft && Names.Full(ft) == "YFex.Rules.DerivedField`2":
                    derived.Add(new DerivedFieldModel(f.Name, DerivedName(f, ct) ?? f.Name, Names.Find(f, Names.Requested) is not null));
                    break;
                case IPropertySymbol { IsStatic: true, Name: "Rounding" } p when Names.Is(p.Type, "YFex.RoundingPolicy"):
                case IFieldSymbol { IsStatic: true, Name: "Rounding" } rf when Names.Is(rf.Type, "YFex.RoundingPolicy"):
                    rounding = true;
                    break;
                case IPropertySymbol { IsStatic: true, Name: "Allocation" } p when Names.Is(p.Type, "YFex.Rules.Allocation"):
                case IFieldSymbol { IsStatic: true, Name: "Allocation" } af when Names.Is(af.Type, "YFex.Rules.Allocation"):
                    allocation = true;
                    break;
                case IMethodSymbol { IsStatic: true, Name: "LineBase" } lb when line is not null && lb.Parameters.Length == 2
                    && SymbolEqualityComparer.Default.Equals(lb.Parameters[1].Type, line):
                    lineBase = "line";
                    Amount(lb);
                    break;
                case IMethodSymbol { IsStatic: true, Name: "Base" } b when b.Parameters.Length == 1:
                    hasBase = true;
                    Amount(b);
                    break;
            }
        }
        if (groups.Count == 0) diags.Add(DiagnosticInfo.Create("YFR0021", loc, type.Name));

        return new RuleSetModel(Names.Namespace(type), type.Name, Names.Of(type), Names.Containing(type), setName,
            situation is null ? "" : Names.Of(situation), line is null ? null : Names.Of(line), linesMember,
            new EquatableArray<string>(groups.ToArray()), new EquatableArray<DerivedFieldModel>(derived.ToArray()),
            rounding, allocation, lineBase, hasBase, amountFqn, new EquatableArray<DiagnosticInfo>(diags.ToArray()));
    }

    /// <summary>Name argument of <c>DerivedField.After(stage, "Name", …)</c> when it is a literal/nameof.</summary>
    private static string? DerivedName(IFieldSymbol f, CancellationToken ct)
    {
        foreach (var r in f.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                {
                    Initializer.Value: Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax { ArgumentList.Arguments.Count: >= 2 } inv
                }
                && inv.ArgumentList.Arguments[1].Expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax lit)
                return lit.Token.ValueText;
        }
        return null;
    }

    // ── Emission ────────────────────────────────────────────────────────────

    /// <summary>Rules/tables grouped under the rule set they join (by situation + group name).</summary>
    internal sealed record Assembled(RuleSetModel Set, EquatableArray<RuleModel> Rules, EquatableArray<TableModel> Tables, EquatableArray<DiagnosticInfo> Diagnostics);

    public static ImmutableArray<Assembled> Assemble(ImmutableArray<RuleSetModel> sets, ImmutableArray<RuleModel> rules, ImmutableArray<TableModel> tables,
        out List<DiagnosticInfo> orphans)
    {
        orphans = new List<DiagnosticInfo>();
        var rulesBySet = sets.ToDictionary(s => s, _ => new List<RuleModel>());
        var tablesBySet = sets.ToDictionary(s => s, _ => new List<TableModel>());

        foreach (var r in rules)
        {
            if (r.SituationFqn.Length == 0 || r.Group.Length == 0) continue;
            var target = Resolve(sets, r.SituationFqn, r.Group, r.RuleSet, r.Code, r.Location, orphans);
            if (target is not null) rulesBySet[target].Add(r);
        }
        foreach (var t in tables)
        {
            if (t.Group is null) continue;
            var target = Resolve(sets, t.SituationFqn, t.Group, t.RuleSet, t.Code, t.Location, orphans);
            if (target is not null) tablesBySet[target].Add(t);
        }

        var result = ImmutableArray.CreateBuilder<Assembled>(sets.Length);
        foreach (var s in sets)
        {
            var diags = new List<DiagnosticInfo>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var r in rulesBySet[s]) if (!seen.Add(r.Code)) diags.Add(new DiagnosticInfo("YFR0014", r.Location, new EquatableArray<string>([r.Code, s.SetName])));
            foreach (var t in tablesBySet[s]) if (!seen.Add(t.Code)) diags.Add(new DiagnosticInfo("YFR0014", t.Location, new EquatableArray<string>([t.Code, s.SetName])));
            result.Add(new Assembled(s,
                new EquatableArray<RuleModel>(rulesBySet[s].OrderBy(r => r.Code, System.StringComparer.Ordinal).ToArray()),
                new EquatableArray<TableModel>(tablesBySet[s].OrderBy(t => t.Code, System.StringComparer.Ordinal).ToArray()),
                new EquatableArray<DiagnosticInfo>(diags.ToArray())));
        }
        return result.MoveToImmutable();
    }

    private static RuleSetModel? Resolve(ImmutableArray<RuleSetModel> sets, string situation, string group, string? ruleSet, string code,
        LocationInfo? loc, List<DiagnosticInfo> diags)
    {
        var matches = sets.Where(s => s.SituationFqn == situation && s.Groups.AsSpan().ToArray().Contains(group)
            && (ruleSet is null || s.SetName == ruleSet)).ToArray();
        if (matches.Length == 1) return matches[0];
        string sName = situation.Substring(situation.LastIndexOf('.') + 1);
        diags.Add(matches.Length == 0
            ? new DiagnosticInfo("YFR0011", loc, new EquatableArray<string>([code, group, sName]))
            : new DiagnosticInfo("YFR0012", loc, new EquatableArray<string>([code, group, sName, string.Join(", ", matches.Select(m => m.SetName))])));
        return null;
    }

    public static void Emit(SourceProductionContext spc, Assembled a)
    {
        var s = a.Set;
        foreach (var d in s.Diagnostics) if (d.Id == "YFR0020") return;
        if (s.SituationFqn.Length == 0) return;
        string ts = s.SituationFqn;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> YFex.Rules.Generator — rule set");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591, CS0162");
        if (s.Namespace.Length > 0) sb.Append("namespace ").Append(s.Namespace).AppendLine(";");
        foreach (var c in s.Containing) sb.Append("partial ").Append(c.Keyword).Append(' ').Append(c.Name).AppendLine(" {");
        sb.Append("static partial class ").Append(s.TypeName).AppendLine();
        sb.AppendLine("{");

        var rules = a.Rules.AsSpan().ToArray().Where(r => r.Diagnostics.AsSpan().ToArray().All(d => d.Id is "YFR0016")).ToArray();

        // Rule ids (index in the definition = violation origin owner id).
        sb.AppendLine("    /// <summary>Generated ids of the rules (definition index) and tables (rule count + table index).</summary>");
        sb.AppendLine("    public static class RuleIds");
        sb.AppendLine("    {");
        for (int i = 0; i < rules.Length; i++) sb.Append("        public const ushort ").Append(rules[i].TypeName).Append(" = ").Append(i).AppendLine(";");
        for (int i = 0; i < a.Tables.Length; i++) sb.Append("        public const ushort ").Append(a.Tables[i].TypeName).Append(" = ").Append(rules.Length + i).AppendLine(";");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    private static readonly global::System.Lazy<global::YFex.Rules.RuleSetDefinition<").Append(ts).Append(">> __set = new(__Build);").AppendLine();
        sb.AppendLine("    /// <summary>Frozen definition (code rules + table bindings + groups). Built once.</summary>");
        sb.Append("    public static global::YFex.Rules.RuleSetDefinition<").Append(ts).AppendLine("> Set => __set.Value;");
        sb.AppendLine("    /// <summary>Evaluates with the code-only compiled set (no published data).</summary>");
        sb.Append("    public static global::YFex.Rules.Decision<").Append(ts).Append("> Evaluate(in ").Append(ts)
          .AppendLine(" situation, in global::YFex.Rules.EvaluateOptions options = default) => Set.Evaluate(in situation, in options);");
        sb.AppendLine("    /// <summary>Evaluates through a host (published tables/parameters/overrides, as-of).</summary>");
        sb.Append("    public static global::YFex.Rules.Decision<").Append(ts).Append("> Evaluate(global::YFex.Rules.RuleSetHost<").Append(ts).Append("> host, in ").Append(ts)
          .AppendLine(" situation, in global::YFex.Rules.EvaluateOptions options = default) => host.Evaluate(in situation, in options);");
        sb.Append("    public static global::YFex.Rules.RuleSetHost<").Append(ts).Append("> CreateHost(global::YFex.Rules.RuleData? data = null, int keepVersions = 16) => new(Set, data, keepVersions);").AppendLine();
        sb.AppendLine("    /// <summary>Rule catalog: id → code, group, stage, editability, version, conditions.</summary>");
        sb.AppendLine("    public static global::YFex.Rules.RuleCatalog Catalog => Set.Catalog;");
        sb.AppendLine();

        // Build
        sb.Append("    private static global::YFex.Rules.RuleSetDefinition<").Append(ts).AppendLine("> __Build()");
        sb.Append("        => new global::YFex.Rules.RuleSetDefinitionBuilder<").Append(ts).Append(">(").Append(Names.Literal(s.SetName)).Append(", 0x")
          .Append(CodeVersion(rules, a.Tables).ToString("X16")).AppendLine("UL)");
        foreach (var g in s.Groups) sb.Append("            .Group(").Append(Names.Literal(g)).Append(", ").Append(g).AppendLine(")");
        if (s.HasRounding) sb.AppendLine("            .Rounding(Rounding)");
        if (s.HasAllocation) sb.AppendLine("            .Allocation(Allocation)");
        if (s.AmountFqn is not null) sb.Append("            .Amount<").Append(s.AmountFqn).AppendLine(">()");
        if (s.LineBaseKind == "line" && s.LinesMember is not null && s.AmountFqn is not null)
            sb.Append("            .LineBase(static (in ").Append(ts).Append(" s, int i) => global::YFex.Rules.Amounts.ToDecimal(LineBase(in s, in s.")
              .Append(s.LinesMember).AppendLine(".ItemRef(i))))");
        if (s.HasBase && s.AmountFqn is not null)
            sb.Append("            .Base(static (in ").Append(ts).AppendLine(" s) => global::YFex.Rules.Amounts.ToDecimal(Base(in s)))");
        foreach (var d in s.Derived)
            sb.Append("            .Derived(").Append(d.Member).Append(d.Requested ? ", requested: true" : "").AppendLine(")");
        for (int i = 0; i < rules.Length; i++) EmitRule(sb, ts, rules[i], i);
        foreach (var t in a.Tables)
            sb.Append("            .Table(new global::YFex.Rules.TableBinding<").Append(ts).Append(", ").Append(t.FullyQualified).Append(", ").Append(t.RowFqn)
              .Append(">(").Append(Names.Literal(t.Group!)).Append(", priority: ").Append(t.Priority).AppendLine("))");
        sb.AppendLine("            .Build();");

        // Explain bodies (06 §4)
        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            if (!r.Splittable) continue;
            sb.AppendLine();
            sb.Append("    private static byte __Explain_").Append(i).Append("(in ").Append(ts).Append(' ').Append(r.SituationParam).AppendLine(", int __line)");
            sb.AppendLine("    {");
            if (r.LineParam is not null && r.LinesMember is not null)
                sb.Append("        ref readonly var ").Append(r.LineParam).Append(" = ref ").Append(r.SituationParam).Append('.').Append(r.LinesMember).AppendLine(".ItemRef(__line);");
            for (int k = 0; k < r.Conditions.Length; k++)
                sb.Append("        if (!(").Append(r.Conditions[k].CheckExpr).Append(")) return ").Append(k).AppendLine(";");
            sb.AppendLine("        return byte.MaxValue;");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        foreach (var _ in s.Containing) sb.AppendLine("}");
        spc.AddSource(SituationGen.Hint(s.FullyQualified) + ".RuleSet.g.cs", sb.ToString());
    }

    private static void EmitRule(StringBuilder sb, string ts, RuleModel r, int index)
    {
        bool line = r.LineTypeFqn is not null;
        string item = line ? $"in s.{r.LinesMember}.ItemRef(i), " : "";
        string appliesArgs = "in s, " + item + (r.AppliesHasContext ? "in c" : "");
        string evaluateArgs = "in s, " + item + (r.EvaluateHasContext ? "in c, " : "") + "ref d";
        appliesArgs = appliesArgs.TrimEnd(' ', ',');

        sb.Append("            .Rule(global::YFex.Rules.RuleDescriptor<").Append(ts).Append(">.").Append(line ? "CreateLine" : "Create").Append('(')
          .Append(Names.Literal(r.Code)).Append(", ").Append(Names.Literal(r.Group)).AppendLine(",");
        if (line)
        {
            sb.Append("                static (in ").Append(ts).Append(" s, int i, in global::YFex.Rules.RuleContext c) => ").Append(r.TypeFqn).Append(".Applies(").Append(appliesArgs).AppendLine("),");
            sb.Append("                static (in ").Append(ts).Append(" s, int i, in global::YFex.Rules.RuleContext c, ref global::YFex.Rules.DirectiveWriter d) => ")
              .Append(r.TypeFqn).Append(".Evaluate(").Append(evaluateArgs).AppendLine("),");
        }
        else
        {
            sb.Append("                static (in ").Append(ts).Append(" s, in global::YFex.Rules.RuleContext c) => ").Append(r.TypeFqn).Append(".Applies(").Append(appliesArgs).AppendLine("),");
            sb.Append("                static (in ").Append(ts).Append(" s, in global::YFex.Rules.RuleContext c, ref global::YFex.Rules.DirectiveWriter d) => ")
              .Append(r.TypeFqn).Append(".Evaluate(").Append(evaluateArgs).AppendLine("),");
        }
        sb.Append("                priority: ").Append(r.Priority).Append(", version: ").Append(r.Version);
        if (r.IsSealed) sb.Append(", isSealed: true");
        if (r.EffectiveFrom is { } from) sb.Append(", effectiveFrom: global::System.DateOnly.ParseExact(").Append(Names.Literal(from)).Append(", \"yyyy-MM-dd\")");
        if (r.EffectiveTo is { } to) sb.Append(", effectiveTo: global::System.DateOnly.ParseExact(").Append(Names.Literal(to)).Append(", \"yyyy-MM-dd\")");
        if (r.Splittable) sb.Append(", explain: static (in ").Append(ts).Append(" s, int i) => __Explain_").Append(index).Append("(in s, i)");
        if (r.Conditions.Length > 0)
        {
            sb.AppendLine(",");
            sb.Append("                conditions: [");
            for (int k = 0; k < r.Conditions.Length; k++)
            {
                var c = r.Conditions[k];
                if (k > 0) sb.Append(", ");
                sb.Append("new(").Append(k).Append(", ").Append(Names.Literal(c.Text)).Append(", ").Append(Names.Literal(c.MessageKey)).Append(", ")
                  .Append(c.Field is null ? "null" : Names.Literal(c.Field)).Append(", ").Append(c.ValueExpr).Append(')');
            }
            sb.Append(']');
        }
        if (r.Parameters.Length > 0)
        {
            sb.Append(", parameters: [");
            for (int k = 0; k < r.Parameters.Length; k++) sb.Append(k > 0 ? ", " : "").Append(r.TypeFqn).Append('.').Append(r.Parameters[k]);
            sb.Append(']');
        }
        sb.Append(", typeName: ").Append(Names.Literal(r.TypeName)).AppendLine("))");
    }

    /// <summary>FNV-1a 64 over rule/table codes + versions — the code part of <c>RulesetVersion</c>.</summary>
    private static ulong CodeVersion(RuleModel[] rules, EquatableArray<TableModel> tables)
    {
        ulong h = 14695981039346656037UL;
        void Mix(string s) { foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; } h ^= 0xFF; h *= 1099511628211UL; }
        foreach (var r in rules) { Mix(r.Code); Mix(r.Version.ToString()); }
        foreach (var t in tables) { Mix(t.Code); foreach (var c in t.Columns) Mix(c.Member + ":" + c.Field + ":" + c.Directive); }
        return h;
    }
}
