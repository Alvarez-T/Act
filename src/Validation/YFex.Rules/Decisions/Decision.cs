using System.Globalization;
using YFex.Localization;
using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// Resolved limit/options of one field after all stages (Principle B). <see cref="RawValue.None"/> = unbounded side;
/// <see cref="OptionSet.Unrestricted"/> = no grant.
/// </summary>
public readonly record struct FieldLimit(SituationFieldRef Field, RawValue Min, RawValue Max, OptionSet Options)
{
    public bool HasOptions => !Options.IsEmpty;
}

/// <summary>
/// Immutable result of one evaluation (04 §6). One allocation per evaluation (+ exact-size arrays). Equality =
/// <see cref="Fingerprint"/> equality → safe as an <c>[Observable]</c> VM property without UI churn.
/// </summary>
public sealed class Decision<TSituation> : IEquatable<Decision<TSituation>> where TSituation : ISituation<TSituation>
{
    private readonly FieldLimit[] _limits;
    private readonly RawValue[] _derived;
    private readonly RuleSetDefinition<TSituation> _definition;
    private readonly RuleDescriptor<TSituation>[] _rules;

    internal Decision(RuleSetDefinition<TSituation> definition, RuleDescriptor<TSituation>[] rules, in TSituation situation, Verdict verdict, ViolationList violations,
        ApprovalRequestList approvals, DecisionTotals totals, AdjustmentList adjustments, RulesetVersion version, ulong fingerprint,
        DecisionTrace? trace, FieldLimit[] limits, RawValue[] derived)
    {
        _definition = definition;
        _rules = rules;
        Situation = situation;
        Verdict = verdict;
        Violations = violations;
        Approvals = approvals;
        Totals = totals;
        Adjustments = adjustments;
        RulesetVersion = version;
        Fingerprint = fingerprint;
        Trace = trace;
        _limits = limits;
        _derived = derived;
    }

    public TSituation Situation { get; }
    public Verdict Verdict { get; }
    /// <summary>Deny/Warn + <see cref="RequestedAttribute"/> checks; paths = situation field names.</summary>
    public ViolationList Violations { get; }
    /// <summary>Which permissions are needed and why.</summary>
    public ApprovalRequestList Approvals { get; }
    /// <summary>Per line + gross + final, after all stages.</summary>
    public DecisionTotals Totals { get; }
    /// <summary>Applied adjustments — ALWAYS present (06 §2).</summary>
    public AdjustmentList Adjustments { get; }
    public RulesetVersion RulesetVersion { get; }
    /// <summary>Hash of the OUTCOME (04 §8) — not of the trace, not of the version.</summary>
    public ulong Fingerprint { get; }
    /// <summary>Null unless <see cref="EvaluateOptions.Trace"/> &gt; Off.</summary>
    public DecisionTrace? Trace { get; }
    public string RuleSet => _definition.Name;
    public RuleSetDefinition<TSituation> Definition => _definition;
    /// <summary>The rules that were in force: code rules, then runtime rules (05 §8) — condition ids index into this.</summary>
    internal ReadOnlySpan<RuleDescriptor<TSituation>> Rules => _rules;

    /// <summary>Resolved limits, sorted by field id.</summary>
    public ReadOnlySpan<FieldLimit> Limits => _limits;

    public bool TryGetLimit(SituationFieldRef field, out RawValue min, out RawValue max)
    {
        foreach (ref readonly var l in _limits.AsSpan())
            if (l.Field.Id == field.Id) { min = l.Min; max = l.Max; return !(min.IsNone && max.IsNone); }
        min = max = default;
        return false;
    }

    public bool TryGetMax<T>(SituationField<TSituation, T> field, out T value)
    {
        value = default!;
        return TryGetLimit(field, out _, out var max) && !max.IsNone && RawValueConvert.TryGet(in max, out value);
    }

    public bool TryGetMin<T>(SituationField<TSituation, T> field, out T value)
    {
        value = default!;
        return TryGetLimit(field, out var min, out _) && !min.IsNone && RawValueConvert.TryGet(in min, out value);
    }

    /// <summary>Upper limit for bindings (<c>Policy.Max(Fields.Installments)</c>); <c>default</c> when unbounded — prefer <see cref="TryGetMax{T}"/>.</summary>
    public T? Max<T>(SituationField<TSituation, T> field) => TryGetMax(field, out T v) ? v : default;
    public T? Min<T>(SituationField<TSituation, T> field) => TryGetMin(field, out T v) ? v : default;

    /// <summary>Granted options of a field (<see cref="OptionSet.Unrestricted"/> when no grant).</summary>
    public OptionSet Options(SituationFieldRef field)
    {
        foreach (ref readonly var l in _limits.AsSpan())
            if (l.Field.Id == field.Id) return l.Options;
        return OptionSet.Unrestricted;
    }

    /// <summary>FieldConstraints for a situation field — what the UI merges into <c>IValidationSource</c> (07 §7.3).</summary>
    public FieldConstraints ConstraintsFor(SituationFieldRef field)
    {
        foreach (ref readonly var l in _limits.AsSpan())
            if (l.Field.Id == field.Id) return FieldConstraints.Create(min: l.Min, max: l.Max, options: l.Options);
        return FieldConstraints.None;
    }

    /// <summary>
    /// Constraints of the situation field or rule-set derived field NAMED <paramref name="fieldName"/> (VM properties bind by
    /// the same name, 07 §9.3 — used by generated <c>[ConstraintsFrom]</c> code). False when the decision has none for it.
    /// </summary>
    public bool TryGetConstraints(ReadOnlySpan<char> fieldName, out FieldConstraints constraints)
    {
        constraints = default;
        SituationFieldRef field;
        if (TSituation.TryGetFieldId(fieldName, out ushort id)) field = TSituation.Schema[id].Ref;
        else if (_definition.TryGetDerived(fieldName, out var derived)) field = derived.Ref;
        else return false;
        constraints = ConstraintsFor(field);
        return constraints.Flags != ConstraintFlags.None;
    }

    /// <summary>Value of a rule-set derived field computed during this evaluation.</summary>
    public RawValue DerivedValue(DerivedField field)
    {
        int i = field.Id - SituationFieldRef.DerivedBase;
        return (uint)i < (uint)_derived.Length ? _derived[i] : RawValue.None;
    }

    /// <summary>Human explanation built from <see cref="Adjustments"/> (+<see cref="Trace"/> when present) and a catalog (06 §5).</summary>
    public Explanation Explain(IMessageCatalog catalog, CultureInfo culture) => Explanation.Build(this, catalog, culture);

    public bool Equals(Decision<TSituation>? other) => other is not null && other.Fingerprint == Fingerprint;
    public override bool Equals(object? obj) => obj is Decision<TSituation> d && Equals(d);
    public override int GetHashCode() => Fingerprint.GetHashCode();
    public override string ToString() => $"{Verdict} final={Totals.Final} adjustments={Adjustments.Count} fp={Fingerprint:X16}";
}
