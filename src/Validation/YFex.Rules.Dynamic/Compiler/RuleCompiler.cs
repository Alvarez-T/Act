using YFex.Validation;

namespace YFex.Rules.Dynamic;

/// <summary>
/// Spec → <see cref="CompiledRule{TS}"/>: placement (group + policy), size limits, binding/type checks of every expression,
/// directive checks (families allowed, fields, caps, amount type), then compilation. Errors carry JSON paths.
/// </summary>
internal static class RuleCompiler<TS> where TS : ISituation<TS>
{
    public static CompiledRule<TS>? Compile(RuleSetDefinition<TS> set, FunctionCatalog functions, RuleSpec spec, List<Violation> errors)
    {
        var binder = new Binder<TS>(set, functions, errors);
        var group = set.FindGroup(spec.Group);
        if (group is null) binder.Error("group", DynamicRuleCodes.UnknownGroup, $"rule set '{set.Name}' has no group '{spec.Group}'");
        else if (group.Dynamic is null) binder.Error("group", DynamicRuleCodes.GroupClosed, $"group '{spec.Group}' does not accept runtime rules");
        var policy = group?.Dynamic;

        int nodes = (spec.When?.CountNodes() ?? 0);
        foreach (var d in spec.Then)
        {
            if (!d.Args.IsDefault) foreach (var a in d.Args) nodes += a.CountNodes();
            nodes += (d.Min?.CountNodes() ?? 0) + (d.Max?.CountNodes() ?? 0) + (d.Percent?.CountNodes() ?? 0) + (d.Amount?.CountNodes() ?? 0);
        }
        if (nodes > Binder<TS>.MaxNodes) binder.Error("", DynamicRuleCodes.TooLarge, $"{nodes} expression nodes (max {Binder<TS>.MaxNodes})");
        if (spec.When is { } w && w.Depth() > Binder<TS>.MaxDepth) binder.Error("when", DynamicRuleCodes.TooLarge, $"nesting deeper than {Binder<TS>.MaxDepth}");
        if (spec.Then.IsDefaultOrEmpty) binder.Error("then", DynamicRuleCodes.Missing, "a rule needs at least one directive");
        else if (spec.Then.Length > Binder<TS>.MaxDirectives) binder.Error("then", DynamicRuleCodes.TooLarge, $"more than {Binder<TS>.MaxDirectives} directives");

        // when: each top-level `and` operand is one condition (trace "why not").
        Expr[] operands = spec.When is null ? [] : spec.When.Kind == ExprKind.And ? spec.When.Args.ToArray() : [spec.When];
        var bound = new Bound[operands.Length];
        for (int i = 0; i < operands.Length; i++)
        {
            string path = spec.When!.Kind == ExprKind.And ? $"when.and[{i}]" : "when";
            bound[i] = binder.Bind(operands[i], path);
            if (!bound[i].Type.IsBool && !bound[i].Type.IsNull) binder.Error(path, DynamicRuleCodes.Type, $"a condition must be true/false, got {bound[i].Type}");
        }

        var directives = new List<(DirectiveSpec Spec, Bound[] Args, Bound? Min, Bound? Max, Bound? Percent, Bound? Amount, SituationFieldInfo? Field, OptionSet Options)>();
        for (int i = 0; i < (spec.Then.IsDefault ? 0 : spec.Then.Length); i++)
            directives.Add(BindDirective(set, spec, spec.Then[i], $"then[{i}]", binder, policy));

        if (binder.HasErrors || policy is null) return null;

        var conditions = new DynProgram<TS>[bound.Length];
        var infos = new ConditionInfo[bound.Length];
        for (int i = 0; i < bound.Length; i++)
        {
            conditions[i] = Emitter<TS>.Compile(bound[i]);
            infos[i] = Info((byte)i, bound[i]);
        }
        var compiled = new CompiledDirective<TS>[directives.Count];
        for (int i = 0; i < compiled.Length; i++)
        {
            var (d, args, min, max, percent, amount, field, options) = directives[i];
            var argPrograms = new DynProgram<TS>[args.Length];
            for (int a = 0; a < args.Length; a++) argPrograms[a] = Emitter<TS>.Compile(args[a]);
            compiled[i] = new CompiledDirective<TS>
            {
                Kind = d.Kind,
                Field = field is null ? default : field.Ref,
                Path = string.IsNullOrEmpty(d.Path) ? FieldPath.Empty : FieldPath.Of(d.Path),
                Code = new RuleCode(d.Code ?? (d.Kind == DirectiveSpecKind.Approval ? spec.Code + ".approval" : spec.Code)),
                Args = argPrograms,
                Min = min is null ? null : Emitter<TS>.Compile(min),
                Max = max is null ? null : Emitter<TS>.Compile(max),
                Percent = percent is null ? null : Emitter<TS>.Compile(percent),
                Amount = amount is null ? null : Emitter<TS>.Compile(amount),
                Exceed = d.Exceed,
                Permission = d.Permission,
                Options = options,
                LineTarget = d.LineTarget,
            };
        }
        return new CompiledRule<TS>(spec, conditions, infos, compiled, binder.UsesLine, policy, set.AmountType);
    }

    private static (DirectiveSpec, Bound[], Bound?, Bound?, Bound?, Bound?, SituationFieldInfo?, OptionSet) BindDirective(RuleSetDefinition<TS> set,
        RuleSpec spec, DirectiveSpec d, string path, Binder<TS> binder, DynamicRulePolicy? policy)
    {
        var family = d.Kind switch
        {
            DirectiveSpecKind.Deny => DynamicDirectives.Deny,
            DirectiveSpecKind.Warn => DynamicDirectives.Warn,
            DirectiveSpecKind.Limit => DynamicDirectives.Limit,
            DirectiveSpecKind.Grant => DynamicDirectives.Grant,
            DirectiveSpecKind.Decrease => DynamicDirectives.Decrease,
            DirectiveSpecKind.Increase => DynamicDirectives.Increase,
            _ => DynamicDirectives.RequireApproval,
        };
        string inner = path + "." + d.Kind switch
        {
            DirectiveSpecKind.Approval => "approval",
            _ => d.Kind.ToString().ToLowerInvariant(),
        };
        if (policy is not null && (policy.Allow & family) == 0)
            binder.Error(path, DynamicRuleCodes.DirectiveNotAllowed, $"group '{spec.Group}' does not allow {family} from runtime rules (allows {policy.Allow})");

        var args = new Bound[d.Args.IsDefault ? 0 : Math.Min(d.Args.Length, 2)];
        if (!d.Args.IsDefault && d.Args.Length > 2) binder.Error(inner + ".args", DynamicRuleCodes.Arity, "at most two message arguments");
        for (int i = 0; i < args.Length; i++) args[i] = binder.Bind(d.Args[i], $"{inner}.args[{i}]");

        SituationFieldInfo? field = null;
        Bound? min = null, max = null, percent = null, amount = null;
        OptionSet options = default;
        switch (d.Kind)
        {
            case DirectiveSpecKind.Limit:
            case DirectiveSpecKind.Grant:
                if (string.IsNullOrEmpty(d.Field) || !TS.Schema.TryGetField(d.Field, out field) || field.Role == FieldRole.Lines)
                {
                    binder.Error(inner + ".field", DynamicRuleCodes.UnknownField, $"'{d.Field}' is not a situation field");
                    break;
                }
                var fieldType = new DynType(field.Type, field.Kind == ValueKind.EnumCode ? field : null);
                if (d.Kind == DirectiveSpecKind.Limit)
                {
                    if (d.Min is null && d.Max is null) binder.Error(path, DynamicRuleCodes.Missing, "limit needs min and/or max");
                    min = d.Min is null ? null : Typed(binder, d.Min, fieldType, inner + ".min");
                    max = d.Max is null ? null : Typed(binder, d.Max, fieldType, inner + ".max");
                    if (!fieldType.IsComparable) binder.Error(inner + ".field", DynamicRuleCodes.Type, $"{field.Name} ({fieldType}) has no order to limit");
                    if (d.Exceed == Exceed.RequireApproval && string.IsNullOrEmpty(d.Permission))
                        binder.Error(inner + ".permission", DynamicRuleCodes.Missing, "exceed = approval needs a permission");
                }
                else
                {
                    if (d.Options.IsDefaultOrEmpty) binder.Error(inner + ".options", DynamicRuleCodes.Missing, "grant needs options");
                    var values = new RawValue[d.Options.IsDefault ? 0 : d.Options.Length];
                    for (int i = 0; i < values.Length; i++) values[i] = binder.ConvertLiteral(d.Options[i], fieldType, $"{inner}.options[{i}]");
                    options = OptionSet.Of(values);
                }
                break;
            case DirectiveSpecKind.Decrease:
            case DirectiveSpecKind.Increase:
                if (set.AmountType is not { } amountType)
                {
                    binder.Error(inner, DynamicRuleCodes.NoAmount, $"rule set '{set.Name}' has no amount to {family}");
                    break;
                }
                if ((d.Percent is null) == (d.Amount is null)) binder.Error(path, DynamicRuleCodes.Missing, "give exactly one of percent or amount");
                if (d.LineTarget && TS.Schema.LineSchema is null) binder.Error(inner + ".target", DynamicRuleCodes.NoLines, "target \"line\" needs a situation with lines");
                var amountDyn = new DynType(amountType);
                if (d.Percent is not null)
                {
                    percent = binder.Bind(d.Percent, inner + ".percent");
                    if (!percent.Type.IsNumeric && !percent.Type.IsNull) binder.Error(inner + ".percent", DynamicRuleCodes.Type, $"percent must be a number, got {percent.Type}");
                    if (percent.IsLiteral && percent.Value.TryGet(out decimal p) && policy?.MaxPercent is { } capP && capP.ToRaw().TryGet(out decimal capPv) && p > capPv)
                        binder.Error(inner + ".percent", DynamicRuleCodes.OverCap, $"{p}% is above the group cap {capPv}%");
                    if (d.Max is not null) max = Typed(binder, d.Max, amountDyn, inner + ".max");
                }
                if (d.Amount is not null)
                {
                    amount = Typed(binder, d.Amount, amountDyn, inner + ".amount");
                    if (amount.IsLiteral && amount.Value.TryGet(out decimal a) && policy?.MaxAmount is { } capA && a > capA)
                        binder.Error(inner + ".amount", DynamicRuleCodes.OverCap, $"{a} is above the group cap {capA}");
                }
                break;
            case DirectiveSpecKind.Approval:
                if (string.IsNullOrEmpty(d.Permission)) binder.Error(inner + ".permission", DynamicRuleCodes.Missing, "approval needs a permission");
                break;
        }
        return (d, args, min, max, percent, amount, field, options);
    }

    /// <summary>Binds <paramref name="e"/> and requires <paramref name="type"/> (literals convert; plain numbers fit numeric targets).</summary>
    private static Bound Typed(Binder<TS> binder, Expr e, in DynType type, string path)
    {
        var b = binder.Bind(e, path);
        if (b.IsLiteral) return new Bound { Kind = ExprKind.Literal, Value = binder.ConvertLiteral(b.Value, type, path), Type = type, Source = e };
        if (!b.Type.IsNull && !b.Type.SameAs(type) && !(type.IsNumeric && b.Type.IsNumber))
            binder.Error(path, DynamicRuleCodes.Type, $"expected {type}, got {b.Type}");
        return b;
    }

    /// <summary>Trace text of a condition: a known template when it's "field op literal", else the expression text.</summary>
    private static ConditionInfo Info(byte index, Bound b)
    {
        string text = ExprText.Render(b.Source);
        if (b.Args.Length == 2 && Template(b.Kind) is { } key)
        {
            var (l, r) = (b.Args[0], b.Args[1]);
            if (l.Kind == ExprKind.Var && l.Special == SpecialVar.None && r.IsLiteral) return new(index, text, key, l.Source.Name, r.Value);
            if (r.Kind == ExprKind.Var && r.Special == SpecialVar.None && l.IsLiteral) return new(index, text, Mirror(key), r.Source.Name, l.Value);
        }
        if (b.Kind == ExprKind.Var && b.Special == SpecialVar.None && b.Type.IsBool) return new(index, text, "rules.cond.true", b.Source.Name, default);
        if (b.Kind == ExprKind.Not && b.Args[0] is { Kind: ExprKind.Var, Special: SpecialVar.None } v && v.Type.IsBool)
            return new(index, text, "rules.cond.false", v.Source.Name, default);
        return new(index, text, "rules.cond.expr", null, RawValue.From(text));
    }

    private static string? Template(ExprKind k) => k switch
    {
        ExprKind.Equal => "rules.cond.eq", ExprKind.NotEqual => "rules.cond.neq", ExprKind.Less => "rules.cond.lt",
        ExprKind.LessOrEqual => "rules.cond.lte", ExprKind.Greater => "rules.cond.gt", ExprKind.GreaterOrEqual => "rules.cond.gte",
        _ => null,
    };

    private static string Mirror(string key) => key switch
    {
        "rules.cond.lt" => "rules.cond.gt", "rules.cond.lte" => "rules.cond.gte",
        "rules.cond.gt" => "rules.cond.lt", "rules.cond.gte" => "rules.cond.lte", _ => key,
    };
}
