using System.Collections.Immutable;
using System.Globalization;
using YFex.Localization;

namespace YFex.Rules;

/// <summary>Kind of an <see cref="ExplanationLine"/>.</summary>
public enum ExplanationKind : byte { Applied = 0, NotApplied = 1, Limit = 2, Approval = 3, Warning = 4 }

/// <summary>One display line of an <see cref="Explanation"/>.</summary>
/// <param name="Title">Rule display name from the catalog (key = rule/table code; falls back to the code).</param>
/// <param name="Detail">Why/how: capped, exclusive with…, requires…, limit text.</param>
/// <param name="Amount">Size of an applied change (rule set's amount type); None for other kinds.</param>
/// <param name="Direction">Direction of an applied change.</param>
public readonly record struct ExplanationLine(string Title, string? Detail, RuleRef Rule, StageRef Stage, int? LineIndex,
    RawValue Amount, ExplanationKind Kind, AdjustDirection Direction = AdjustDirection.Decrease);

/// <summary>
/// Framework-neutral display model built from a decision's adjustments (+ trace) and a message catalog (06 §5).
/// UI controls (<c>YRuleBreakdown</c>) only render this — they never interpret rules.
/// </summary>
public sealed class Explanation
{
    private Explanation(ImmutableArray<ExplanationLine> applied, ImmutableArray<ExplanationLine> notApplied,
        ImmutableArray<ExplanationLine> limits, ImmutableArray<ExplanationLine> approvals, RawValue initial, RawValue final)
    {
        Applied = applied;
        NotApplied = notApplied;
        Limits = limits;
        Approvals = approvals;
        Initial = initial;
        Final = final;
    }

    /// <summary>Applied adjustments, in application order.</summary>
    public ImmutableArray<ExplanationLine> Applied { get; }
    /// <summary>Full trace only: not applicable / superseded / not effective / overridden.</summary>
    public ImmutableArray<ExplanationLine> NotApplied { get; }
    public ImmutableArray<ExplanationLine> Limits { get; }
    public ImmutableArray<ExplanationLine> Approvals { get; }
    /// <summary>Amount before adjustments (rule set's amount type; None without an amount).</summary>
    public RawValue Initial { get; }
    public RawValue Final { get; }

    internal static Explanation Build<TS>(Decision<TS> d, IMessageCatalog catalog, CultureInfo culture) where TS : ISituation<TS>
    {
        string schema = TS.Schema.Name;
        string FieldKey(string field) => schema + "." + field;

        var applied = ImmutableArray.CreateBuilder<ExplanationLine>(d.Adjustments.Count);
        foreach (ref readonly var a in d.Adjustments.AsSpan())
        {
            string? detail = a.Note.Kind switch
            {
                ResolutionNoteKind.CappedBy when a.Note.TryGetValue(out CappedBy c)
                    => catalog.Format("rules.trace.capped", [c.Requested, c.Applied], culture),
                _ => null,
            };
            RawValue arg = a.Percent is { } p ? RawValue.From(p) : a.Amount;
            applied.Add(new(catalog.Format(a.Rule.Code, [arg], culture), detail, a.Rule, a.Stage,
                a.LineIndex >= 0 ? a.LineIndex : null, a.Amount, ExplanationKind.Applied, a.Direction));
        }

        var notApplied = ImmutableArray.CreateBuilder<ExplanationLine>();
        if (d.Trace is { } trace)
        {
            foreach (ref readonly var e in trace.Entries)
            {
                string? detail = e.Outcome.Kind switch
                {
                    TraceOutcomeKind.NotApplicable when e.Outcome.TryGetValue(out NotApplicable na) => Condition(d, na.Failed, catalog, culture, FieldKey),
                    TraceOutcomeKind.Superseded when e.Outcome.TryGetValue(out Superseded s)
                        => catalog.Format("rules.trace.superseded", [RawValue.From(catalog.Format(s.By.Code, default, culture))], culture),
                    TraceOutcomeKind.NotEffective when e.Outcome.TryGetValue(out NotEffective ne)
                        => catalog.Format("rules.trace.not-effective", [RawValue.From(ne.EffectiveDate)], culture),
                    TraceOutcomeKind.Overridden when e.Outcome.TryGetValue(out Overridden o)
                        => catalog.Format("rules.trace.overridden", [RawValue.From(TS.Schema.TierName(o.ByTier))], culture),
                    _ => null,
                };
                if (e.Outcome.Kind is TraceOutcomeKind.NotApplicable or TraceOutcomeKind.Superseded or TraceOutcomeKind.NotEffective
                    or TraceOutcomeKind.Overridden)
                    notApplied.Add(new(catalog.Format(e.Rule.Code, default, culture), detail, e.Rule, e.Stage,
                        e.LineIndex >= 0 ? e.LineIndex : null, default, ExplanationKind.NotApplied));
                else if (e.Outcome.Kind == TraceOutcomeKind.Blocked && e.Outcome.TryGetValue(out Blocked b))
                    notApplied.Add(new(catalog.Format(e.Rule.Code, default, culture), Format(catalog, b.Violation, culture, schema),
                        e.Rule, e.Stage, e.LineIndex >= 0 ? e.LineIndex : null, default, ExplanationKind.Warning));
            }
        }

        var limits = ImmutableArray.CreateBuilder<ExplanationLine>(d.Limits.Length);
        foreach (ref readonly var l in d.Limits)
        {
            if (l.Min.IsNone && l.Max.IsNone) continue;
            string key = FieldKey(l.Field.Name);
            string detail = (l.Min.IsNone, l.Max.IsNone) switch
            {
                (true, false) => catalog.Format("rules.explain.limit.max", [l.Max], culture, key),
                (false, true) => catalog.Format("rules.explain.limit.min", [l.Min], culture, key),
                _ => catalog.Format("rules.explain.limit.range", [l.Min, l.Max], culture, key),
            };
            limits.Add(new(catalog.FieldName(key, culture), detail, default, default, null, default, ExplanationKind.Limit));
        }

        var approvals = ImmutableArray.CreateBuilder<ExplanationLine>(d.Approvals.Count);
        foreach (ref readonly var a in d.Approvals.AsSpan())
            approvals.Add(new(a.Permission, Format(catalog, a.Reason, culture, schema), a.Rule, default, null, default, ExplanationKind.Approval));

        return new(applied.ToImmutable(), notApplied.ToImmutable(), limits.ToImmutable(), approvals.ToImmutable(),
            d.Totals.Initial, d.Totals.Final);
    }

    private static string Format(IMessageCatalog catalog, in YFex.Validation.Violation v, CultureInfo culture, string schema)
    {
        Span<RawValue> args = [v.Args.Count > 0 ? v.Args[0] : default, v.Args.Count > 1 ? v.Args[1] : default];
        string? field = v.Path.Template is { } t ? schema + "." + t : null;
        return v.Detail ?? catalog.Format(v.Code.Value, args[..v.Args.Count], culture, field);
    }

    private static string? Condition<TS>(Decision<TS> d, ConditionId id, IMessageCatalog catalog, CultureInfo culture,
        Func<string, string> fieldKey) where TS : ISituation<TS>
    {
        if (id.IsNone) return null;
        var def = d.Definition;
        var rules = d.Rules;
        if (id.RuleId < rules.Length)
        {
            var conditions = rules[id.RuleId].Conditions;
            if (id.Index >= conditions.Length) return null;
            var c = conditions[id.Index];
            return catalog.Format(c.MessageKey, [c.Value], culture, c.Field is null ? null : fieldKey(c.Field));
        }
        int t = id.RuleId - rules.Length;
        if ((uint)t >= (uint)def.Tables.Length) return null;
        int when = 0;
        foreach (var col in def.Tables[t].Schema.Columns)
        {
            if (col.Role != ColumnRole.When) continue;
            if (when++ == id.Index) return catalog.Format("rules.cond.column", default, culture, fieldKey(col.Field));
        }
        return null;
    }
}
