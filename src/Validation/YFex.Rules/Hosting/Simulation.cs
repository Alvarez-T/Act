using System.Collections.Immutable;

namespace YFex.Rules;

/// <summary>
/// Source of past situations for a what-if (05 §4.0): the canonical situation bytes the app stored with its documents
/// (06 §2 — <see cref="Situation.WriteTo{TS}"/>). The app chooses which ones (e.g. the last 30 days of sales at or below
/// <paramref name="tierPath"/>); the engine only replays them.
/// </summary>
public interface ISituationSamples
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> GetAsync(string ruleSet, RuleTierPath tierPath, DateOnly from, DateOnly to, int max, CancellationToken ct);
}

/// <summary>The part of a decision a what-if compares (amounts as the rule set's amount type; None without amount).</summary>
public readonly record struct DecisionSummary(VerdictKind Verdict, RawValue Final, RawValue Change, ulong Fingerprint, int Violations, int Approvals)
{
    public static DecisionSummary Of<TS>(Decision<TS> d) where TS : ISituation<TS>
        => new(d.Verdict.Kind, d.Totals.Final, d.Totals.Change, d.Fingerprint, d.Violations.Count, d.Approvals.Count);
}

/// <summary>One situation whose decision changes with the draft.</summary>
public sealed record SimulationExample(int Index, ReadOnlyMemory<byte> Situation, DecisionSummary Before, DecisionSummary After);

/// <summary>
/// Outcome of replaying past situations with a draft instead of the published version (05 §4.0). Pure — nothing is
/// published. <see cref="FinalDelta"/> = Σ (after.Final − before.Final) in the rule set's amount (None without amount).
/// </summary>
public sealed record SimulationReport(
    int Evaluated,
    int Changed,
    int VerdictChanged,
    int NewlyDenied,
    int NewlyNeedingApproval,
    RawValue FinalDelta,
    int Unreadable,
    ImmutableArray<SimulationExample> Examples);

public partial interface IRuleSetHost
{
    /// <summary>
    /// What-if: evaluates every situation in <paramref name="situations"/> twice — with the published data, and with
    /// <paramref name="draft"/> replacing its stream (same kind/code/tier path, treated as in force on every date) —
    /// and reports the differences. The draft must have passed the matching <c>Validate…</c>.
    /// </summary>
    SimulationReport Simulate(RuleDataKind kind, string code, in RuleTierPath tierPath, ReadOnlySpan<byte> draftPayload,
        IEnumerable<ReadOnlyMemory<byte>> situations, int maxExamples = 10);
}

public sealed partial class RuleSetHost<TSituation>
{
    public SimulationReport Simulate(RuleDataKind kind, string code, in RuleTierPath tierPath, ReadOnlySpan<byte> draftPayload,
        IEnumerable<ReadOnlyMemory<byte>> situations, int maxExamples = 10)
    {
        ArgumentNullException.ThrowIfNull(situations);
        // Draft = a version newer than anything published, in force since forever, replacing its own stream only:
        // more specific tier paths keep their own versions (a what-if at tenant level doesn't touch stores that override).
        var info = new RuleDataInfo(new RuleDataKey(Definition.Name, kind, code, tierPath), long.MaxValue, DateOnly.MinValue, null, default, "what-if");
        var draft = RuleDataCodec.Decode(Definition, new RuleDataRecord(info, draftPayload.ToArray()), RuntimeRules);
        var draftData = Data.Without(draft).With(draft);

        var compiled = new Dictionary<(RuleTierPath, DateOnly), CompiledRuleSet<TSituation>>();
        var examples = ImmutableArray.CreateBuilder<SimulationExample>();
        int evaluated = 0, changed = 0, verdictChanged = 0, newlyDenied = 0, newlyApproval = 0, unreadable = 0, index = -1;
        decimal delta = 0m;
        foreach (var bytes in situations)
        {
            index++;
            TSituation s;
            try { s = Situation.ReadFrom<TSituation>(bytes.Span); }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            { unreadable++; continue; }

            var path = TSituation.TierPath(in s);
            var date = TSituation.EffectiveDate(in s);
            var before = Resolve(in path, date).Evaluate(in s);
            if (!compiled.TryGetValue((path, date), out var set))
                compiled[(path, date)] = set = CompiledRuleSet<TSituation>.Compile(Definition, in path, draftData, date);
            var after = set.Evaluate(in s);
            evaluated++;

            if (before.Totals.Final.TryGet(out decimal b) && after.Totals.Final.TryGet(out decimal a)) delta += a - b;
            if (before.Fingerprint == after.Fingerprint) continue;
            changed++;
            if (before.Verdict.Kind != after.Verdict.Kind)
            {
                verdictChanged++;
                if (after.Verdict.Kind == VerdictKind.Denied) newlyDenied++;
                else if (after.Verdict.Kind == VerdictKind.NeedsApproval) newlyApproval++;
            }
            if (examples.Count < maxExamples) examples.Add(new(index, bytes, DecisionSummary.Of(before), DecisionSummary.Of(after)));
        }
        var finalDelta = Definition.AmountType is { } t ? Amounts.ToRaw(delta, t) : RawValue.None;
        return new(evaluated, changed, verdictChanged, newlyDenied, newlyApproval, finalDelta, unreadable, examples.ToImmutable());
    }
}
