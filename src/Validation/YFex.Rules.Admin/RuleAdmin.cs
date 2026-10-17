using MemoryPack;
using YFex.Cqrs;

namespace YFex.Rules.Admin;

/// <summary>
/// Runtime rule editing (05 §4, D11/D12/D33): publish table versions, parameter values and overrides; read history and
/// "how was the rule when it was applied" (06 §6). Approval tokens moved to <c>YFex.Cqrs.Approvals</c> (authorization-consolidation 4b). Tier paths travel as
/// <see cref="RuleTierPaths"/> text, payloads as the JSON documents of <see cref="RuleDataCodec"/> / <c>RowsJson</c>.
/// Handlers live in this module; apps register them with <c>AddYFexRulesAdmin()</c>.
/// </summary>
public partial class RuleAdmin
{
    public static partial class Commands
    {
        /// <summary>
        /// Publishes a new immutable table version. <see cref="BaseVersion"/> = the version the editor started from; a newer
        /// one → Conflict (someone published meanwhile). Requires the table's <c>PublishPermission</c>. Returns the version.
        /// </summary>
        [MemoryPackable]
        public sealed partial record PublishTable(string RuleSet, string TableCode, string TierPath, long BaseVersion, DateOnly EffectiveFrom,
            byte[] RowsJson, string? Note) : ICommand<long>;

        /// <summary>Publishes values of a code rule's editable parameters (<c>{"rate": 7.5}</c>).</summary>
        [MemoryPackable]
        public sealed partial record PublishParameters(string RuleSet, string RuleCode, string TierPath, long BaseVersion, DateOnly EffectiveFrom,
            byte[] ValuesJson, string? Note) : ICommand<long>;

        /// <summary>Disable / enable / replace a non-sealed rule for a scope (05 §6). <see cref="Action"/>: disable | enable | replace.</summary>
        [MemoryPackable]
        public sealed partial record SetRuleOverride(string RuleSet, string RuleCode, string TierPath, long BaseVersion, DateOnly EffectiveFrom,
            string Action, string? ReplacementCode, string? Note) : ICommand<long>;

        /// <summary>
        /// Publishes a runtime-authored rule (05 §8, YFex.Rules.Dynamic JSON spec) — validated by the host's runtime rule codec
        /// against the group's policy, the situation schema and the function catalog. Requires
        /// <c>RuleAdminOptions.RulePublishPermission</c>. Disable/re-enable it with <see cref="SetRuleOverride"/>.
        /// </summary>
        [MemoryPackable]
        public sealed partial record PublishRule(string RuleSet, string RuleCode, string TierPath, long BaseVersion, DateOnly EffectiveFrom,
            byte[] Spec, string? Note) : ICommand<long>;
    }

    public static partial class Queries
    {
        /// <summary>
        /// Published versions of a rule set newer than <see cref="AfterVersion"/>, ascending (05 §4.4): 0 = everything (warm-up),
        /// the device's last known version = incremental sync.
        /// </summary>
        [MemoryPackable]
        public sealed partial record GetRuleData(string RuleSet, long AfterVersion = 0) : IQuery<RuleDataPayload[]>, ICacheable;

        /// <summary>
        /// <see cref="GetRuleData"/> with each version's signed form (offline 05 §2.1, fixes F6): devices with an
        /// <see cref="IRuleDataVerifier"/> sync through it. Needs an <see cref="IRuleDataSigner"/> at the server.
        /// </summary>
        [MemoryPackable]
        public sealed partial record GetSignedRuleData(string RuleSet, long AfterVersion = 0) : IQuery<SignedRuleDataPayload[]>, ICacheable;

        /// <summary>
        /// What changed in <see cref="Version"/> of a stream compared with the version before it (or the code rows for a
        /// table's first version) — audit "diff vs N" and the editor's "compare with published" (05 §4).
        /// </summary>
        [MemoryPackable]
        public sealed partial record GetDataChanges(string RuleSet, RuleDataKind Kind, string Code, string TierPath, long Version)
            : IQuery<RuleDataChanges>, ICacheable;

        /// <summary>
        /// What-if (05 §4.0): replays past situations (<c>ISituationSamples</c>, at most <see cref="MaxSamples"/> between
        /// <see cref="SampleFrom"/> and <see cref="SampleTo"/>) with the DRAFT <see cref="Payload"/> instead of the published
        /// version and reports how many decisions change. Nothing is published; requires the same permission as publishing.
        /// </summary>
        [MemoryPackable]
        public sealed partial record Simulate(string RuleSet, RuleDataKind Kind, string Code, string TierPath, byte[] Payload,
            DateOnly SampleFrom, DateOnly SampleTo, int MaxSamples = 1000, int MaxExamples = 10) : IQuery<SimulationResult>;

        /// <summary>Version history (newest first) of one table / parameter set / override for one scope.</summary>
        [MemoryPackable]
        public sealed partial record GetDataVersions(string RuleSet, RuleDataKind Kind, string Code, string TierPath) : IQuery<RuleDataHeader[]>, ICacheable;

        /// <summary>One version with its payload (clients fetch it when notified; editors open it). Empty = not found.</summary>
        [MemoryPackable]
        public sealed partial record GetDataVersion(string RuleSet, RuleDataKind Kind, string Code, string TierPath, long Version) : IQuery<RuleDataPayload[]>, ICacheable;

        /// <summary>"How was this rule when it was applied?" (06 §6) — the definition behind a persisted <see cref="RuleRef"/>.</summary>
        [MemoryPackable]
        public sealed partial record GetRuleDefinition(string RuleSet, string Code, int Version, int Tier, long DataVersion, Guid RowId)
            : IQuery<RuleDefinitionView>, ICacheable;
    }

    public static partial class Events
    {
        /// <summary>Raised after a version is stored and applied on the server; clients fetch + apply it (05 §4.4).</summary>
        [MemoryPackable]
        public sealed partial record RulesetPublished(string RuleSet, RuleDataKind Kind, string Code, string TierPath, long Version, DateOnly EffectiveFrom) : IEvent;
    }
}

/// <summary>Wire form of <see cref="RuleDataInfo"/>.</summary>
[MemoryPackable]
public sealed partial record RuleDataHeader(string RuleSet, RuleDataKind Kind, string Code, string TierPath, long Version, DateOnly EffectiveFrom,
    string? PublishedBy, DateTimeOffset PublishedAt, string? Note)
{
    public static RuleDataHeader From(RuleDataInfo i)
        => new(i.Key.RuleSet, i.Key.Kind, i.Key.Code, RuleTierPaths.Format(i.Key.TierPath), i.Version, i.EffectiveFrom, i.PublishedBy, i.PublishedAt, i.Note);

    public RuleDataInfo ToInfo()
        => new(new RuleDataKey(RuleSet, Kind, Code, RuleTierPaths.Parse(TierPath)), Version, EffectiveFrom, PublishedBy, PublishedAt, Note);
}

/// <summary>Wire form of <see cref="RuleDataRecord"/>.</summary>
[MemoryPackable]
public sealed partial record RuleDataPayload(RuleDataHeader Header, byte[] Payload)
{
    public static RuleDataPayload From(RuleDataRecord r) => new(RuleDataHeader.From(r.Info), r.Payload.ToArray());
    public RuleDataRecord ToRecord() => new(Header.ToInfo(), Payload);
}

/// <summary>Wire form of <see cref="ValueChange"/> (JSON texts of both sides; null = absent).</summary>
[MemoryPackable]
public sealed partial record ValueChangeView(string Name, string? Old, string? New, bool IsRowMeta)
{
    public static ValueChangeView From(in ValueChange c) => new(c.Name, c.Old, c.New, c.IsRowMeta);
}

/// <summary>Wire form of <see cref="RowChange"/>.</summary>
[MemoryPackable]
public sealed partial record RowChangeView(Guid RowId, RowChangeKind Kind, int OldIndex, int NewIndex, ValueChangeView[] Values);

/// <summary>Wire form of <see cref="RuleDataDiff"/> + the header of the version it describes (who/when/note).</summary>
[MemoryPackable]
public sealed partial record RuleDataChanges(RuleDataHeader Header, long FromVersion, RowChangeView[] Rows, ValueChangeView[] Values)
{
    public static RuleDataChanges From(RuleDataInfo info, RuleDataDiff diff)
    {
        var rows = new RowChangeView[diff.Rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = diff.Rows[i];
            var values = new ValueChangeView[r.Values.Length];
            for (int j = 0; j < values.Length; j++) values[j] = ValueChangeView.From(r.Values[j]);
            rows[i] = new(r.RowId, r.Kind, r.OldIndex, r.NewIndex, values);
        }
        var changes = new ValueChangeView[diff.Values.Length];
        for (int i = 0; i < changes.Length; i++) changes[i] = ValueChangeView.From(diff.Values[i]);
        return new(RuleDataHeader.From(info), diff.FromVersion, rows, changes);
    }
}

/// <summary>Wire form of <see cref="DecisionSummary"/> (amounts as decimal storage of the rule set's amount type).</summary>
[MemoryPackable]
public sealed partial record DecisionSummaryView(VerdictKind Verdict, decimal? Final, decimal? Change, ulong Fingerprint, int Violations, int Approvals)
{
    public static DecisionSummaryView From(in DecisionSummary s)
        => new(s.Verdict, s.Final.TryGet(out decimal f) ? f : null, s.Change.TryGet(out decimal c) ? c : null, s.Fingerprint, s.Violations, s.Approvals);
}

/// <summary>Wire form of <see cref="SimulationExample"/>: the canonical situation bytes + both outcomes.</summary>
[MemoryPackable]
public sealed partial record SimulationExampleView(int Index, byte[] Situation, DecisionSummaryView Before, DecisionSummaryView After);

/// <summary>Wire form of <see cref="SimulationReport"/>. <see cref="FinalDelta"/> is in <see cref="AmountType"/> (null = no amount).</summary>
[MemoryPackable]
public sealed partial record SimulationResult(int Evaluated, int Changed, int VerdictChanged, int NewlyDenied, int NewlyNeedingApproval,
    decimal? FinalDelta, string? AmountType, int Unreadable, SimulationExampleView[] Examples)
{
    public static SimulationResult From(SimulationReport r)
    {
        var examples = new SimulationExampleView[r.Examples.Length];
        for (int i = 0; i < examples.Length; i++)
        {
            var e = r.Examples[i];
            examples[i] = new(e.Index, e.Situation.ToArray(), DecisionSummaryView.From(e.Before), DecisionSummaryView.From(e.After));
        }
        return new(r.Evaluated, r.Changed, r.VerdictChanged, r.NewlyDenied, r.NewlyNeedingApproval,
            r.FinalDelta.TryGet(out decimal d) ? d : null, r.FinalDelta.Type?.Key, r.Unreadable, examples);
    }
}

/// <summary>
/// The definition behind a <see cref="RuleRef"/> (06 §6): the catalog entry of the rule/table plus, for published data, the
/// exact JSON in force (a table ROW for table refs, the parameter values for code rules).
/// </summary>
[MemoryPackable]
public sealed partial record RuleDefinitionView(string RuleSet, string Code, string Group, string Stage, bool IsTable, bool IsSealed,
    int CodeVersion, int Tier, long DataVersion, Guid RowId, string? DataJson, string? PublishedBy, DateTimeOffset? PublishedAt, string? Note);
