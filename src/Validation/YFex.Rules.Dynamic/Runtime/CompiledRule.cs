using YFex.Validation;

namespace YFex.Rules.Dynamic;

internal sealed class CompiledDirective<TS> where TS : ISituation<TS>
{
    public required DirectiveSpecKind Kind { get; init; }
    public SituationFieldRef Field { get; init; }
    public FieldPath Path { get; init; }
    public RuleCode Code { get; init; }
    public DynProgram<TS>[] Args { get; init; } = [];
    public DynProgram<TS>? Min { get; init; }
    public DynProgram<TS>? Max { get; init; }
    public DynProgram<TS>? Percent { get; init; }
    public DynProgram<TS>? Amount { get; init; }
    public Exceed Exceed { get; init; }
    public string? Permission { get; init; }
    public OptionSet Options { get; init; }
    public bool LineTarget { get; init; }
}

/// <summary>
/// A dynamic rule ready to run: one program per top-level <c>and</c> operand of <c>when</c> (so "why not" names the first
/// false operand, 06 §4), the compiled directives, and the <see cref="RuleDescriptor{TSituation}"/> the engine evaluates.
/// Immutable; shared by every compiled rule set (and cached by version).
/// </summary>
internal sealed class CompiledRule<TS> where TS : ISituation<TS>
{
    private readonly DynProgram<TS>[] _conditions;
    private readonly CompiledDirective<TS>[] _directives;
    private readonly DynamicRulePolicy _policy;
    private readonly RawType? _amountType;

    public CompiledRule(RuleSpec spec, DynProgram<TS>[] conditions, ConditionInfo[] conditionInfos, CompiledDirective<TS>[] directives,
        bool isLineRule, DynamicRulePolicy policy, RawType? amountType)
    {
        Spec = spec;
        _conditions = conditions;
        _directives = directives;
        _policy = policy;
        _amountType = amountType;
        IsLineRule = isLineRule;
        Descriptor = isLineRule
            ? RuleDescriptor<TS>.CreateLine(spec.Code, spec.Group,
                (in TS s, int line, in RuleContext c) => Applies(in s, line, in c),
                (in TS s, int line, in RuleContext c, ref DirectiveWriter d) => Emit(in s, line, in c, ref d),
                spec.Priority, explain: Explain, conditions: conditionInfos, typeName: "dynamic")
            : RuleDescriptor<TS>.Create(spec.Code, spec.Group,
                (in TS s, in RuleContext c) => Applies(in s, -1, in c),
                (in TS s, in RuleContext c, ref DirectiveWriter d) => Emit(in s, -1, in c, ref d),
                spec.Priority, explain: Explain, conditions: conditionInfos, typeName: "dynamic");
    }

    public RuleSpec Spec { get; }
    public bool IsLineRule { get; }
    public RuleDescriptor<TS> Descriptor { get; }

    private bool Applies(in TS s, int line, in RuleContext c)
    {
        var @base = c.Base;
        foreach (var cond in _conditions)
            if (!cond.RunBool(in s, line, in @base, c.EffectiveDate)) return false;
        return true;
    }

    /// <summary>Index of the first false condition (trace "why not"); the running base isn't known here → <c>$base</c> reads null.</summary>
    private byte Explain(in TS s, int line)
    {
        var date = TS.EffectiveDate(in s);
        for (int i = 0; i < _conditions.Length && i < byte.MaxValue; i++)
            if (!_conditions[i].RunBool(in s, line, RawValue.None, date)) return (byte)i;
        return byte.MaxValue;
    }

    private void Emit(in TS s, int line, in RuleContext c, ref DirectiveWriter d)
    {
        var @base = c.Base;
        var date = c.EffectiveDate;
        foreach (var x in _directives)
        {
            switch (x.Kind)
            {
                case DirectiveSpecKind.Deny or DirectiveSpecKind.Warn:
                {
                    RawValue a0 = x.Args.Length > 0 ? x.Args[0].Run(in s, line, in @base, date) : default;
                    RawValue a1 = x.Args.Length > 1 ? x.Args[1].Run(in s, line, in @base, date) : default;
                    bool deny = x.Kind == DirectiveSpecKind.Deny;
                    switch (x.Args.Length)
                    {
                        case 0: if (deny) d.Deny(x.Path, x.Code); else d.Warn(x.Path, x.Code); break;
                        case 1: if (deny) d.Deny(x.Path, x.Code, a0); else d.Warn(x.Path, x.Code, a0); break;
                        default: if (deny) d.Deny(x.Path, x.Code, a0, a1); else d.Warn(x.Path, x.Code, a0, a1); break;
                    }
                    break;
                }
                case DirectiveSpecKind.Limit:
                    d.LimitRaw(x.Field, x.Min?.Run(in s, line, in @base, date) ?? default, x.Max?.Run(in s, line, in @base, date) ?? default,
                        x.Exceed, x.Permission);
                    break;
                case DirectiveSpecKind.Grant:
                    d.Grant(x.Field, x.Options);
                    break;
                case DirectiveSpecKind.Approval:
                    d.RequireApproval(x.Permission!, x.Path, x.Code);
                    break;
                default:
                    EmitAdjust(in s, line, in @base, date, x, ref d);
                    break;
            }
        }
    }

    /// <summary>Increase/Decrease, clamped to the group's <see cref="DynamicRulePolicy"/> caps; a negative or null size emits nothing.</summary>
    private void EmitAdjust(in TS s, int line, in RawValue @base, DateOnly date, CompiledDirective<TS> x, ref DirectiveWriter d)
    {
        var target = x.LineTarget ? AdjustTarget.Line : AdjustTarget.Total;
        var direction = x.Kind == DirectiveSpecKind.Decrease ? AdjustDirection.Decrease : AdjustDirection.Increase;
        RawValue max = default;
        if (x.Max?.Run(in s, line, in @base, date) is { Kind: not ValueKind.None } m && m.TryGet(out decimal mv) && mv >= 0) max = Cap(mv);

        if (x.Percent is { } percentProgram)
        {
            if (!percentProgram.Run(in s, line, in @base, date).TryGet(out decimal p) || p < 0) return;
            if (_policy.MaxPercent is { } cap && cap.ToRaw().TryGet(out decimal capValue) && p > capValue) p = capValue;
            d.Adjust(new AdjustDirective(target, direction, Percentual.FromPercentage(p), Max: max));
        }
        else if (x.Amount is { } amountProgram)
        {
            if (!amountProgram.Run(in s, line, in @base, date).TryGet(out decimal a) || a < 0) return;
            d.Adjust(new AdjustDirective(target, direction, null, Cap(a)));
        }
    }

    private RawValue Cap(decimal amount)
    {
        if (_policy.MaxAmount is { } cap && amount > cap) amount = cap;
        return Amounts.ToRaw(amount, _amountType ?? RawType.Decimal);
    }
}
