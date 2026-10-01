using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Validation.Generator;

// Fluent configuration as a source (03 §4, D14/D23/D27): `.Validation(v => { v.Field(c => c.Name).Required()… })`.
// Every rule whose arguments are compile-time constants becomes the same RuleModel an attribute would produce and is
// merged into the message's model; its key (runtime FluentRuleKey format) is listed in CompiledFluentRules<T> so the
// runtime ValidationBuilder<T> skips it. Everything else (non-constant arguments, non-trivial When lambdas, Invariant
// lambdas, messages that are not partial) stays runtime-only.
internal static partial class Parser
{
    // ── Syntax predicate ─────────────────────────────────────────────────────────

    public static bool IsFluentCandidate(SyntaxNode node, CancellationToken ct)
        => node is InvocationExpressionSyntax
           {
               Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Validation" },
               ArgumentList.Arguments.Count: 1,
           } invocation
           && invocation.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax;

    // ── Semantic transform ───────────────────────────────────────────────────────

    public static FluentConfigModel? TransformFluent(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var semantic = ctx.SemanticModel;
        if (Resolve(semantic.GetSymbolInfo(invocation, ct)) is not IMethodSymbol method || method.Parameters.Length != 1) return null;
        if (method.Parameters[0].Type is not INamedTypeSymbol { Name: "Action", TypeArguments.Length: 1 } action
            || action.TypeArguments[0] is not INamedTypeSymbol { Name: "ValidationBuilder", TypeArguments.Length: 1 } builder
            || builder.ContainingNamespace.ToDisplayString() != Ns
            || builder.TypeArguments[0] is not INamedTypeSymbol target)
            return null;

        var lambda = (LambdaExpressionSyntax)invocation.ArgumentList.Arguments[0].Expression;
        string? parameter = LambdaParameter(lambda);
        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(invocation.GetLocation());
        string fqn = target.ToDisplayString(PlainTypeFormat);
        string metadataName = MetadataNameOf(target);

        if (!IsFluentEmittable(target))
        {
            diagnostics.Add(Diag("YFV0003", location, target.ToDisplayString()));
            return new FluentConfigModel(fqn, metadataName, false, EquatableArray<FluentMemberModel>.Empty,
                EquatableArray<string>.Empty, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        var boolMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in target.GetMembers())
            if (m is IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean } or IFieldSymbol { Type.SpecialType: SpecialType.System_Boolean })
                boolMembers.Add(m.Name);

        var members = new Dictionary<string, (List<RuleModel> Rules, bool Each)>(StringComparer.Ordinal);
        var order = new List<string>();
        var keys = new List<string>();

        IEnumerable<ExpressionSyntax> statements = lambda.Body switch
        {
            BlockSyntax block => block.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression),
            ExpressionSyntax e => new[] { e },
            _ => Array.Empty<ExpressionSyntax>(),
        };

        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            if (parameter is null || !TryUnwindChain(statement, parameter, out var calls)) continue;

            string root = NameOf(calls[0]);
            if (root == "Invariant") continue;   // lambdas always run at runtime (03 §4.3)

            var member = ResolveMemberLambda(calls[0], target, semantic, ct);
            if (member is null)
            {
                diagnostics.Add(Diag("YFV0004", LocationInfo.From(calls[0].GetLocation()), calls[0].ArgumentList.ToString()));
                continue;
            }
            var (symbol, memberType) = member.Value;
            if (!members.TryGetValue(symbol.Name, out var entry))
            {
                entry = (new List<RuleModel>(), false);
                order.Add(symbol.Name);
            }

            if (root == "Each")
            {
                members[symbol.Name] = (entry.Rules, true);
                keys.Add(symbol.Name + "|each");
                continue;
            }
            if (root != "Field") continue;

            string? fieldCode = FieldCodeOf(symbol);
            var parsed = ParseChain(calls, target, boolMembers, semantic, ct);
            var info = TypeInfo.Of(memberType);
            foreach (var p in parsed)
            {
                if (!p.Compilable)
                {
                    diagnostics.Add(Diag("YFV0004", LocationInfo.From(p.Call.GetLocation()), symbol.Name));
                    continue;
                }
                var named = new List<KeyValuePair<string, ArgValue>>(p.Named);
                if (p.WhenMember is not null) named.Add(new("When", ArgValue.FromConstant(p.WhenMember)));
                if (p.Profile is not null) named.Add(new("Profile", ArgValue.FromConstant(p.Profile)));
                var input = new RuleInput { Args = p.Args.ToArray(), Named = named.ToArray(), RuleType = p.RuleType };

                var rule = BuildRule(input, p.KindName, false, info, symbol.Name, fieldCode, boolMembers,
                    LocationInfo.From(p.Call.GetLocation()), diagnostics);
                if (rule is null) continue;
                if (!entry.Rules.Contains(rule)) entry.Rules.Add(rule);
                keys.Add(FullKey(symbol.Name, p.KeyCore, p.WhenExpr, p.Profile));
            }
            members[symbol.Name] = entry;
        }

        var memberModels = order
            .Select(n => new FluentMemberModel(n, new EquatableArray<RuleModel>(members[n].Rules.ToArray()), members[n].Each))
            .ToArray();
        return new FluentConfigModel(fqn, metadataName, true,
            new EquatableArray<FluentMemberModel>(memberModels),
            new EquatableArray<string>(keys.Distinct(StringComparer.Ordinal).ToArray()),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }

    private static bool IsFluentEmittable(INamedTypeSymbol target)
    {
        if (!target.Locations.Any(l => l.IsInSource) || target.IsRefLikeType || IsStateObject(target)) return false;
        if (target.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        for (INamedTypeSymbol? t = target; t is not null; t = t.ContainingType)
            if (!IsPartial(t) || t.TypeParameters.Length > 0) return false;
        return true;
    }

    private static string? LambdaParameter(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.ValueText,
        ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } p => p.ParameterList.Parameters[0].Identifier.ValueText,
        _ => null,
    };

    /// <summary><c>v.Field(x).Required().Length(2, 80)</c> → [Field, Required, Length] (root first).</summary>
    private static bool TryUnwindChain(ExpressionSyntax expression, string parameter, out List<InvocationExpressionSyntax> calls)
    {
        calls = new List<InvocationExpressionSyntax>();
        var e = expression;
        while (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call)
        {
            calls.Add(call);
            e = access.Expression;
        }
        calls.Reverse();
        return calls.Count > 0 && e is IdentifierNameSyntax id && id.Identifier.ValueText == parameter;
    }

    /// <summary>
    /// The generator sees the compilation BEFORE its own output: e.g. <c>Each&lt;TItem&gt;() where TItem : IValidatable&lt;TItem&gt;</c>
    /// fails when TItem becomes validatable only through this generator, and the whole lambda does not bind. A single
    /// candidate is still the method the final compilation resolves to.
    /// </summary>
    private static ISymbol? Resolve(SymbolInfo info)
        => info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);

    private static string NameOf(InvocationExpressionSyntax call)
        => ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;

    /// <summary><c>Field(c =&gt; c.Name)</c> → the target's own public instance member (else runtime-only).</summary>
    private static (ISymbol Symbol, ITypeSymbol Type)? ResolveMemberLambda(InvocationExpressionSyntax call,
        INamedTypeSymbol target, SemanticModel semantic, CancellationToken ct)
    {
        if (call.ArgumentList.Arguments.Count < 1 || call.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return null;
        string? p = LambdaParameter(lambda);
        if (p is null || lambda.Body is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } access
            || receiver.Identifier.ValueText != p)
            return null;

        var symbol = Resolve(semantic.GetSymbolInfo(access, ct));
        if (symbol is null || symbol.IsStatic || symbol.DeclaredAccessibility != Accessibility.Public
            || !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, target))
            return null;
        return symbol switch
        {
            IPropertySymbol { IsIndexer: false } prop => (prop, prop.Type),
            IFieldSymbol { IsConst: false } field => (field, field.Type),
            _ => null,
        };
    }

    private static string? FieldCodeOf(ISymbol member)
    {
        foreach (var a in member.GetAttributes())
            if (Is(a, Ns, "FieldCodeAttribute") && a.ConstructorArguments.Length == 1)
                return a.ConstructorArguments[0].Value as string;
        return null;
    }

    // ── Chain parsing ────────────────────────────────────────────────────────────

    private sealed class ParsedRule
    {
        public ParsedRule(InvocationExpressionSyntax call, string kindName) { Call = call; KindName = kindName; }

        public InvocationExpressionSyntax Call { get; }
        public string KindName { get; }
        public string KeyCore { get; set; } = "";
        public bool Compilable { get; set; } = true;
        public List<ArgValue> Args { get; } = new();
        public List<KeyValuePair<string, ArgValue>> Named { get; } = new();
        public INamedTypeSymbol? RuleType { get; set; }
        public string? WhenMember { get; set; }
        public string? WhenExpr { get; set; }
        public string? Profile { get; set; }
    }

    private static List<ParsedRule> ParseChain(List<InvocationExpressionSyntax> calls, INamedTypeSymbol target,
        HashSet<string> boolMembers, SemanticModel semantic, CancellationToken ct)
    {
        var rules = new List<ParsedRule>();
        for (int i = 1; i < calls.Count; i++)
        {
            var call = calls[i];
            var args = call.ArgumentList.Arguments;
            switch (NameOf(call))
            {
                case "Required": rules.Add(new ParsedRule(call, "required") { KeyCore = "required" }); break;
                case "NotEmpty": rules.Add(new ParsedRule(call, "notempty") { KeyCore = "notempty" }); break;
                case "Email": rules.Add(new ParsedRule(call, "email") { KeyCore = "email" }); break;

                case "Length":
                {
                    var r = new ParsedRule(call, "len");
                    var c = Constants(args, semantic, ct);
                    if (c is null || c.Count is < 1 or > 2 || !c.All(x => x.Value is int)) { r.Compilable = false; rules.Add(r); break; }
                    int min = c.Count == 2 ? (int)c[0].Value! : 0, max = (int)c[c.Count - 1].Value!;
                    r.Args.Add(ArgValue.FromConstant(min));
                    r.Args.Add(ArgValue.FromConstant(max));
                    r.KeyCore = string.Format(CultureInfo.InvariantCulture, "len:{0}:{1}", min, max);
                    rules.Add(r);
                    break;
                }

                case "Range":
                {
                    var r = new ParsedRule(call, "range");
                    ArgValue? min = null, max = null;
                    bool coerce = false, ok = true;
                    for (int a = 0; a < args.Count; a++)
                    {
                        string? name = args[a].NameColon?.Name.Identifier.ValueText;
                        var cv = semantic.GetConstantValue(args[a].Expression, ct);
                        if (!cv.HasValue) { ok = false; break; }
                        if (name == "coerce" || (name is null && a == 2)) { coerce = cv.Value is true; continue; }
                        if (name == "min" || (name is null && a == 0)) min = ArgValue.FromConstant(cv.Value);
                        else if (name == "max" || (name is null && a == 1)) max = ArgValue.FromConstant(cv.Value);
                    }
                    if (!ok || min is null || max is null) { r.Compilable = false; rules.Add(r); break; }
                    r.Args.Add(min.Value);
                    r.Args.Add(max.Value);
                    if (coerce) r.Named.Add(new("Coerce", ArgValue.FromConstant(true)));
                    r.KeyCore = "range:" + FormatKey(min.Value.Value) + ":" + FormatKey(max.Value.Value) + (coerce ? ":1" : ":0");
                    rules.Add(r);
                    break;
                }

                case "Pattern":
                {
                    var r = new ParsedRule(call, "pattern");
                    var c = Constants(args, semantic, ct);
                    if (c is null || c.Count != 1 || c[0].Value is not string pattern) { r.Compilable = false; rules.Add(r); break; }
                    r.Args.Add(c[0]);
                    r.KeyCore = "pattern:" + pattern;
                    rules.Add(r);
                    break;
                }

                case "With":
                {
                    var r = new ParsedRule(call, "with");
                    if (Resolve(semantic.GetSymbolInfo(call, ct)) is not IMethodSymbol { TypeArguments.Length: >= 1 } m
                        || m.TypeArguments[0] is not INamedTypeSymbol ruleType)
                    {
                        r.Compilable = false; rules.Add(r); break;
                    }
                    r.RuleType = ruleType;
                    string key = "with:" + MetadataNameOf(ruleType);
                    if (args.Count == 1)
                    {
                        var c = Constants(args, semantic, ct);
                        if (c is null) { r.Compilable = false; rules.Add(r); break; }
                        r.Args.Add(c[0]);
                        key += ":" + FormatKey(c[0].Value);
                    }
                    // Runtime keys use typeof(TRule).FullName: closed generic rule types cannot match → not compiled.
                    if (ruleType.IsGenericType) r.Compilable = false;
                    r.KeyCore = key;
                    rules.Add(r);
                    break;
                }

                // ── Modifiers ──
                case "Severity":
                case "Code":
                {
                    if (rules.Count == 0) break;
                    var last = rules[rules.Count - 1];
                    var c = Constants(args, semantic, ct);
                    if (c is null || c.Count != 1) { last.Compilable = false; break; }
                    last.Named.Add(new(NameOf(call), c[0]));
                    break;
                }

                case "Profile":
                {
                    var c = Constants(args, semantic, ct);
                    foreach (var r in rules)
                    {
                        if (r.Profile is not null) continue;
                        if (c is null || c.Count != 1 || c[0].Value is not string profile) { r.Compilable = false; r.Profile = "?"; continue; }
                        r.Profile = profile;
                    }
                    break;
                }

                case "When":
                {
                    if (args.Count < 1) break;
                    var condition = args[0].Expression;
                    string text = condition.ToString();
                    string? member = BoolMemberOf(condition, target, boolMembers, semantic, ct);
                    foreach (var r in rules)
                    {
                        if (r.WhenExpr is not null) continue;
                        r.WhenExpr = text;
                        if (member is null) r.Compilable = false;   // arbitrary lambda → runtime (03 §4.3)
                        else r.WhenMember = member;
                    }
                    break;
                }

                default:
                    // Unknown call (extension, future rule): be safe — nothing after it is compiled.
                    foreach (var r in rules) r.Compilable = false;
                    break;
            }
        }
        return rules;
    }

    /// <summary><c>c =&gt; c.IsCompany</c> on a bool member of the target → member name (attribute When semantics).</summary>
    private static string? BoolMemberOf(ExpressionSyntax condition, INamedTypeSymbol target, HashSet<string> boolMembers,
        SemanticModel semantic, CancellationToken ct)
    {
        if (condition is not LambdaExpressionSyntax lambda || LambdaParameter(lambda) is not { } p) return null;
        if (lambda.Body is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } access
            || receiver.Identifier.ValueText != p)
            return null;
        var symbol = Resolve(semantic.GetSymbolInfo(access, ct));
        return symbol is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, target)
               && boolMembers.Contains(symbol.Name)
            ? symbol.Name
            : null;
    }

    private static List<ArgValue>? Constants(SeparatedSyntaxList<ArgumentSyntax> args, SemanticModel semantic, CancellationToken ct)
    {
        var list = new List<ArgValue>(args.Count);
        foreach (var a in args)
        {
            var cv = semantic.GetConstantValue(a.Expression, ct);
            if (!cv.HasValue) return null;
            list.Add(ArgValue.FromConstant(cv.Value));
        }
        return list;
    }

    // ── Keys: must match YFex.Validation.FluentRuleKey exactly ───────────────────

    private static string FullKey(string path, string rule, string? whenExpr, string? profile)
    {
        string key = path + "|" + rule;
        if (whenExpr is not null) key += "|when:" + whenExpr;
        if (profile is not null) key += "|profile:" + profile;
        return key;
    }

    private static string FormatKey(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>
/// Merges fluent configurations into the attribute models (03 §4.4): each emittable fluent target is rebuilt from its
/// symbol with the union of every <c>.Validation</c> call's rules; view models whose <c>[ValidatesAs]</c> contract is
/// only fluent-validatable are rebuilt so they borrow its rules (G8).
/// </summary>
internal static class FluentMerge
{
    public static ImmutableArray<TypeModel> Merge(ImmutableArray<TypeModel> models, ImmutableArray<FluentConfigModel> fluent,
        Compilation compilation, CancellationToken ct)
    {
        var byTarget = new Dictionary<string, List<FluentConfigModel>>(StringComparer.Ordinal);
        foreach (var f in fluent)
        {
            if (!f.TargetEmittable) continue;
            if (!byTarget.TryGetValue(f.TargetFqn, out var list)) byTarget[f.TargetFqn] = list = new List<FluentConfigModel>();
            list.Add(f);
        }
        if (byTarget.Count == 0) return models;

        var previous = Parser.t_fluentTargets;
        Parser.t_fluentTargets = new HashSet<string>(byTarget.Keys, StringComparer.Ordinal);
        try
        {
            var rebuilt = new SortedDictionary<string, TypeModel>(StringComparer.Ordinal);
            foreach (var kv in byTarget)
            {
                ct.ThrowIfCancellationRequested();
                if (compilation.GetTypeByMetadataName(kv.Value[0].TargetMetadataName) is not { } symbol) continue;

                var members = new Dictionary<string, (List<RuleModel> Rules, bool Each)>(StringComparer.Ordinal);
                var keys = new List<string>();
                foreach (var cfg in kv.Value)
                {
                    foreach (var m in cfg.Members)
                    {
                        if (!members.TryGetValue(m.Name, out var entry)) entry = (new List<RuleModel>(), false);
                        foreach (var r in m.Rules)
                            if (!entry.Rules.Contains(r)) entry.Rules.Add(r);
                        members[m.Name] = (entry.Rules, entry.Each || m.Each);
                    }
                    keys.AddRange(cfg.CompiledKeys);
                }
                var fluentMembers = members.ToDictionary(
                    x => x.Key,
                    x => new FluentMemberModel(x.Key, new EquatableArray<RuleModel>(x.Value.Rules.ToArray()), x.Value.Each),
                    StringComparer.Ordinal);

                // Pass 1 finds the members actually discovered; list only keys whose rules landed in the model
                // (a key without its compiled rule would make the runtime skip a rule nobody runs).
                var model = Parser.BuildModel(symbol, compilation, ct, fluentMembers);
                var present = new HashSet<string>(model.Members.Select(m => m.Name), StringComparer.Ordinal);
                var landed = keys.Where(k => present.Contains(k.Substring(0, k.IndexOf('|')))).Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal).ToArray();
                rebuilt[model.FullyQualifiedName] = model with { CompiledFluentKeys = new EquatableArray<string>(landed) };
            }

            var result = new List<TypeModel>(models.Length + rebuilt.Count);
            foreach (var m in models)
            {
                if (rebuilt.ContainsKey(m.FullyQualifiedName)) continue;
                if (m.Vm is { } vm && NeedsRebuild(vm, rebuilt) && m.MetadataName is { } name
                    && compilation.GetTypeByMetadataName(name) is { } vmSymbol)
                    result.Add(Parser.BuildVmModel(vmSymbol, compilation, ct));
                else
                    result.Add(m);
            }
            result.AddRange(rebuilt.Values);
            return result.ToImmutableArray();
        }
        finally
        {
            Parser.t_fluentTargets = previous;
        }
    }

    private static bool NeedsRebuild(VmModel vm, SortedDictionary<string, TypeModel> fluentTargets)
    {
        foreach (var c in vm.Contracts)
            if (!c.IsValidatable && fluentTargets.ContainsKey(c.Fqn)) return true;
        return false;
    }
}
