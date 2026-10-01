using YFex.Validation;

namespace YFex.Rules;

/// <summary>How much "why" an evaluation records (06 §3). Applied adjustments are recorded at every level.</summary>
public enum TraceLevel : byte
{
    /// <summary>Default on the server hot path: adjustments only.</summary>
    Off = 0,
    /// <summary>+ one entry per applied rule with its directives.</summary>
    Applied = 1,
    /// <summary>+ not applicable (failed condition), superseded, capped, overridden, denied, not effective — "why not".</summary>
    Full = 2,
}

/// <summary>Options of one evaluation.</summary>
public readonly record struct EvaluateOptions
{
    public TraceLevel Trace { get; init; }
}

/// <summary>Trace case: the rule applied.</summary>
public readonly record struct Applied;
/// <summary>Trace case: a condition was false (see 06 §4).</summary>
public readonly record struct NotApplicable(ConditionId Failed);
/// <summary>Trace case: lost an Exclusive/First resolution.</summary>
public readonly record struct Superseded(RuleRef By);
/// <summary>Trace case: reduced by a Stack cap / a directive's Max (amounts in the rule set's amount type).</summary>
public readonly record struct Capped(RawValue From, RawValue To);
/// <summary>
/// Trace case: disabled/replaced by data published at rule tier <see cref="ByTier"/>, or clamped/ignored by a sealed
/// guard (<see cref="ByTier"/> 0 = code).
/// </summary>
public readonly record struct Overridden(int ByTier);
/// <summary>Trace case: the rule issued a Deny (named <c>Blocked</c> — <see cref="Denied"/> is the Verdict case).</summary>
public readonly record struct Blocked(Violation Violation);
/// <summary>Trace case: outside its <see cref="EffectiveAttribute"/> window on the situation's effective date.</summary>
public readonly record struct NotEffective(DateOnly EffectiveDate);

/// <summary>Kind of a <see cref="TraceOutcome"/>.</summary>
public enum TraceOutcomeKind : byte
{
    None = 0, Applied = 1, NotApplicable = 2, Superseded = 3, Capped = 4, Overridden = 5, Blocked = 6, NotEffective = 7,
}

/// <summary>What happened to a rule in an evaluation (06 §3). Non-boxing union.</summary>
[Union]
public readonly struct TraceOutcome : IUnion
{
    private readonly ConditionId _condition;
    private readonly RuleRef _by;
    private readonly RawValue _from;
    private readonly RawValue _to;
    private readonly Violation _violation;
    private readonly DateOnly _date;
    private readonly int _tier;
    private readonly TraceOutcomeKind _tag;

    public TraceOutcome(Applied _) : this() => _tag = TraceOutcomeKind.Applied;
    public TraceOutcome(NotApplicable v) : this() { _condition = v.Failed; _tag = TraceOutcomeKind.NotApplicable; }
    public TraceOutcome(Superseded v) : this() { _by = v.By; _tag = TraceOutcomeKind.Superseded; }
    public TraceOutcome(Capped v) : this() { _from = v.From; _to = v.To; _tag = TraceOutcomeKind.Capped; }
    public TraceOutcome(Overridden v) : this() { _tier = v.ByTier; _tag = TraceOutcomeKind.Overridden; }
    public TraceOutcome(Blocked v) : this() { _violation = v.Violation; _tag = TraceOutcomeKind.Blocked; }
    public TraceOutcome(NotEffective v) : this() { _date = v.EffectiveDate; _tag = TraceOutcomeKind.NotEffective; }

    public TraceOutcomeKind Kind => _tag;
    public bool HasValue => _tag != TraceOutcomeKind.None;

    /// <summary>Boxes (IUnion) — not for library code.</summary>
    public object? Value => _tag switch
    {
        TraceOutcomeKind.Applied => new Applied(),
        TraceOutcomeKind.NotApplicable => new NotApplicable(_condition),
        TraceOutcomeKind.Superseded => new Superseded(_by),
        TraceOutcomeKind.Capped => new Capped(_from, _to),
        TraceOutcomeKind.Overridden => new Overridden(_tier),
        TraceOutcomeKind.Blocked => new Blocked(_violation),
        TraceOutcomeKind.NotEffective => new NotEffective(_date),
        _ => null,
    };

    public bool TryGetValue(out Applied v) { v = default; return _tag == TraceOutcomeKind.Applied; }
    public bool TryGetValue(out NotApplicable v) { v = new(_condition); return _tag == TraceOutcomeKind.NotApplicable; }
    public bool TryGetValue(out Superseded v) { v = new(_by); return _tag == TraceOutcomeKind.Superseded; }
    public bool TryGetValue(out Capped v) { v = new(_from, _to); return _tag == TraceOutcomeKind.Capped; }
    public bool TryGetValue(out Overridden v) { v = new(_tier); return _tag == TraceOutcomeKind.Overridden; }
    public bool TryGetValue(out Blocked v) { v = new(_violation); return _tag == TraceOutcomeKind.Blocked; }
    public bool TryGetValue(out NotEffective v) { v = new(_date); return _tag == TraceOutcomeKind.NotEffective; }

    public static implicit operator TraceOutcome(Applied v) => new(v);
    public static implicit operator TraceOutcome(NotApplicable v) => new(v);
    public static implicit operator TraceOutcome(Superseded v) => new(v);
    public static implicit operator TraceOutcome(Capped v) => new(v);
    public static implicit operator TraceOutcome(Overridden v) => new(v);
    public static implicit operator TraceOutcome(Blocked v) => new(v);
    public static implicit operator TraceOutcome(NotEffective v) => new(v);

    public override string ToString() => _tag switch
    {
        TraceOutcomeKind.NotApplicable => $"NotApplicable(C{_condition.Index})",
        TraceOutcomeKind.Superseded => $"Superseded(by {_by.Code})",
        TraceOutcomeKind.Capped => $"Capped({_from} → {_to})",
        TraceOutcomeKind.Overridden => $"Overridden(tier {_tier})",
        TraceOutcomeKind.Blocked => $"Blocked({_violation.Code})",
        TraceOutcomeKind.NotEffective => $"NotEffective({_date})",
        _ => _tag.ToString(),
    };
}

/// <summary>One traced rule (or table row) of an evaluation.</summary>
public readonly struct TraceEntry
{
    private readonly Directive[]? _directives;

    internal TraceEntry(RuleRef rule, StageRef stage, string group, int lineIndex, TraceOutcome outcome, Directive[]? directives)
    {
        Rule = rule;
        Stage = stage;
        Group = group;
        LineIndex = lineIndex;
        Outcome = outcome;
        _directives = directives;
    }

    public RuleRef Rule { get; }
    public StageRef Stage { get; }
    public string Group { get; }
    /// <summary>−1 when not a line rule.</summary>
    public int LineIndex { get; }
    public TraceOutcome Outcome { get; }
    /// <summary>What it issued (Applied/Superseded/Capped).</summary>
    public ReadOnlySpan<Directive> Directives => _directives;

    public override string ToString() => $"{Rule.Code}{(LineIndex >= 0 ? "[" + LineIndex + "]" : "")}: {Outcome}";
}

/// <summary>Opt-in record of why (06 §3).</summary>
public sealed class DecisionTrace
{
    private readonly TraceEntry[] _entries;

    internal DecisionTrace(TraceEntry[] entries, TraceLevel level)
    {
        _entries = entries;
        Level = level;
    }

    public TraceLevel Level { get; }
    public ReadOnlySpan<TraceEntry> Entries => _entries;

    /// <summary>Entries of rule/table <paramref name="code"/>.</summary>
    public IEnumerable<TraceEntry> For(string code)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Rule.Code, code, StringComparison.Ordinal)) yield return e;
    }
}
