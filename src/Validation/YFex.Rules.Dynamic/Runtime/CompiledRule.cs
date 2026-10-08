using YFex.Validation;
using YFex.Authorization.Public;

namespace YFex.Rules.Dynamic;

internal sealed class CompiledDirective
{
    public required DirectiveSpecKind Kind { get; init; }
    public SituationFieldRef Field { get; init; }
    public FieldPath Path { get; init; }
    public RuleCode Code { get; init; }
    public ExprProgram[] Args { get; init; } = [];
    public ExprProgram? Min { get; init; }
    public ExprProgram? Max { get; init; }
    public ExprProgram? Percent { get; init; }
    public ExprProgram? Amount { get; init; }
    public Exceed Exceed { get; init; }
    /// <summary>The declared permission (resolved when the rule was compiled at publish/load).</summary>
    public Permission? Permission { get; init; }
    public OptionSet Options { get; init; }
    public bool LineTarget { get; init; }
}

/// <summary>
/// A dynamic rule ready to run: one <see cref="ExprProgram"/> per top-level <c>and</c> operand of <c>when</c> (so "why not" names the
/// first false operand, 06 §4), the compiled directives, and the <see cref="RuleDescriptor{TSituation}"/> the engine evaluates.
/// Programs read the situation through <see cref="SituationReader{TS}"/>; <c>$base</c>/<c>$effectiveDate</c> come from the
/// <see cref="ExprEnv"/>. Immutable; shared by every compiled rule set (and cached by version).
/// </summary>
internal sealed class CompiledRule<TS> where TS : ISituation<TS>
{
    private readonly ExprProgram[] _conditions;
    private readonly CompiledDirective[] _directives;
    private readonly DynamicRulePolicy _policy;
    private readonly RawType? _amountType;

    public CompiledRule(RuleSpec spec, ExprProgram[] conditions, ConditionInfo[] conditionInfos, CompiledDirective[] directives,
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
        var env = RuleExpressions.Env(c.Base, c.EffectiveDate);
        var reader = new SituationReader<TS>(in s);
        foreach (var cond in _conditions)
            if (!cond.RunBool(ref reader, in env, line)) return false;
        return true;
    }

    /// <summary>Index of the first false condition (trace "why not"); the running base isn't known here → <c>$base</c> reads null.</summary>
    private byte Explain(in TS s, int line)
    {
        var env = RuleExpressions.Env(RawValue.None, TS.EffectiveDate(in s));
        var reader = new SituationReader<TS>(in s);
        for (int i = 0; i < _conditions.Length && i < byte.MaxValue; i++)
            if (!_conditions[i].RunBool(ref reader, in env, line)) return (byte)i;
        return byte.MaxValue;
    }

    private void Emit(in TS s, int line, in RuleContext c, ref DirectiveWriter d)
    {
        var env = RuleExpressions.Env(c.Base, c.EffectiveDate);
        var reader = new SituationReader<TS>(in s);
        foreach (var x in _directives)
        {
            switch (x.Kind)
            {
                case DirectiveSpecKind.Deny or DirectiveSpecKind.Warn:
                {
                    RawValue a0 = x.Args.Length > 0 ? x.Args[0].Run(ref reader, in env, line) : default;
                    RawValue a1 = x.Args.Length > 1 ? x.Args[1].Run(ref reader, in env, line) : default;
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
                    d.LimitRaw(x.Field, x.Min?.Run(ref reader, in env, line) ?? default, x.Max?.Run(ref reader, in env, line) ?? default,
                        x.Exceed, x.Permission);
                    break;
                case DirectiveSpecKind.Grant:
                    d.Grant(x.Field, x.Options);
                    break;
                case DirectiveSpecKind.Approval:
                    d.RequireApproval(x.Permission.GetValueOrDefault(), x.Path, x.Code);
                    break;
                default:
                    EmitAdjust(in s, in env, line, x, ref d);
                    break;
            }
        }
    }

    /// <summary>Increase/Decrease, clamped to the group's <see cref="DynamicRulePolicy"/> caps; a negative or null size emits nothing.</summary>
    private void EmitAdjust(in TS s, in ExprEnv env, int line, CompiledDirective x, ref DirectiveWriter d)
    {
        var reader = new SituationReader<TS>(in s);
        var target = x.LineTarget ? AdjustTarget.Line : AdjustTarget.Total;
        var direction = x.Kind == DirectiveSpecKind.Decrease ? AdjustDirection.Decrease : AdjustDirection.Increase;
        RawValue max = default;
        if (x.Max?.Run(ref reader, in env, line) is { Kind: not ValueKind.None } m && m.TryGet(out decimal mv) && mv >= 0) max = Cap(mv);

        if (x.Percent is { } percentProgram)
        {
            if (!percentProgram.Run(ref reader, in env, line).TryGet(out decimal p) || p < 0) return;
            if (_policy.MaxPercent is { } cap && cap.ToRaw().TryGet(out decimal capValue) && p > capValue) p = capValue;
            d.Adjust(new AdjustDirective(target, direction, Percentual.FromPercentage(p), Max: max));
        }
        else if (x.Amount is { } amountProgram)
        {
            if (!amountProgram.Run(ref reader, in env, line).TryGet(out decimal a) || a < 0) return;
            d.Adjust(new AdjustDirective(target, direction, null, Cap(a)));
        }
    }

    private RawValue Cap(decimal amount)
    {
        if (_policy.MaxAmount is { } cap && amount > cap) amount = cap;
        return Amounts.ToRaw(amount, _amountType ?? RawType.Decimal);
    }
}
