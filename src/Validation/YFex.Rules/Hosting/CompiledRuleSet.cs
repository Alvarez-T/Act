using System.Collections.Concurrent;

namespace YFex.Rules;

/// <summary>Why published data was changed or ignored while compiling (guardrails, 04 §9) — surfaced in Full traces.</summary>
public enum CompileNoteKind : byte
{
    /// <summary>A parameter override was outside its sealed guard → clamped.</summary>
    ParameterClamped = 1,
    /// <summary>Data published at a rule tier the table/parameter's <c>EditableAt</c> doesn't name → ignored.</summary>
    NotEditableAtTier = 2,
    /// <summary>An override targeting a <see cref="SealedAttribute"/> rule → ignored.</summary>
    SealedOverrideIgnored = 3,
    /// <summary>A table row failed its guardrail/shape checks → excluded.</summary>
    RowRejected = 4,
    /// <summary>A runtime rule (05 §8) was not placed: code collision, group without a policy for that tier, group limit.</summary>
    RuntimeRuleIgnored = 5,
}

/// <summary>One compile note (<paramref name="Tier"/> = depth the data was published at).</summary>
public readonly record struct CompileNote(CompileNoteKind Kind, string Code, int Tier, string? Detail = null);

/// <summary>
/// A rule set definition + the published data resolved for one (tier path, effective date): table versions, parameter
/// values and overrides of every tier on the path (04 §7 step 1). Immutable; cached by <see cref="RuleSetHost{TSituation}"/>.
/// </summary>
public sealed class CompiledRuleSet<TSituation> where TSituation : ISituation<TSituation>
{
    private readonly TableInstance<TSituation>[] _tables;
    private readonly ParameterValue?[] _parameters;
    private readonly RuleDescriptor<TSituation>[] _rules;
    private readonly int[][] _rulesByGroup;
    private readonly int[] _ruleTier;
    private readonly int?[] _ruleDisabledBy;
    private readonly int?[] _tableDisabledBy;
    private readonly long[] _ruleDataVersion;
    private readonly CompileNote[] _notes;

    private CompiledRuleSet(RuleSetDefinition<TSituation> definition, RuleTierPath tierPath, TableInstance<TSituation>[] tables,
        ParameterValue?[] parameters, RuleDescriptor<TSituation>[] rules, int[][] rulesByGroup, int[] ruleTier, int?[] ruleDisabledBy,
        int?[] tableDisabledBy, long[] ruleDataVersion, CompileNote[] notes, long dataVersion)
    {
        Definition = definition;
        TierPath = tierPath;
        _tables = tables;
        _parameters = parameters;
        _rules = rules;
        _rulesByGroup = rulesByGroup;
        _ruleTier = ruleTier;
        _ruleDisabledBy = ruleDisabledBy;
        _tableDisabledBy = tableDisabledBy;
        _ruleDataVersion = ruleDataVersion;
        _notes = notes;
        Version = new(definition.CodeVersion, dataVersion);
    }

    public RuleSetDefinition<TSituation> Definition { get; }
    public RuleTierPath TierPath { get; }
    public RulesetVersion Version { get; }
    public ReadOnlySpan<TableInstance<TSituation>> Tables => _tables;
    public ReadOnlySpan<CompileNote> Notes => _notes;

    /// <summary>
    /// Rules in force: the definition's code rules (same indices), then the runtime rules (05 §8) of this tier path / date.
    /// Owner ids of tables follow: <c>Rules.Length + table index</c>.
    /// </summary>
    public ReadOnlySpan<RuleDescriptor<TSituation>> Rules => _rules;

    /// <summary>Runtime rules in force (after the code rules in <see cref="Rules"/>).</summary>
    public ReadOnlySpan<RuleDescriptor<TSituation>> RuntimeRules => _rules.AsSpan(Definition.Rules.Length);

    internal RuleDescriptor<TSituation>[] RuleArray => _rules;
    internal ReadOnlySpan<int> RulesOf(int group) => _rulesByGroup[group];
    internal ParameterValue?[] ParameterValues => _parameters;
    internal int? RuleDisabledBy(int rule) => _ruleDisabledBy[rule];
    internal int? TableDisabledBy(int table) => _tableDisabledBy[table];
    internal long RuleDataVersion(int rule) => _ruleDataVersion[rule];
    /// <summary>Tier depth a rule comes from (0 = code).</summary>
    internal int RuleTier(int rule) => _ruleTier[rule];

    /// <summary>Evaluates <paramref name="situation"/> against this compiled set (04 §7).</summary>
    public Decision<TSituation> Evaluate(in TSituation situation, in EvaluateOptions options = default)
        => Evaluator<TSituation>.Evaluate(this, in situation, in options);

    /// <summary>Resolves <paramref name="data"/> for <paramref name="tierPath"/> in effect on <paramref name="effectiveDate"/>.</summary>
    public static CompiledRuleSet<TSituation> Compile(RuleSetDefinition<TSituation> definition, in RuleTierPath tierPath, RuleData data,
        DateOnly effectiveDate)
    {
        // Least → most specific: code, tier 1, …, the path itself.
        var chain = new RuleTierPath[tierPath.Depth + 1];
        for (int i = 0; i < chain.Length; i++) chain[i] = tierPath.Prefix(i);
        var schema = TSituation.Schema;

        var notes = new List<CompileNote>();
        long dataVersion = 0;

        // Tables: most specific allowed version wins.
        var tables = new TableInstance<TSituation>[definition.Tables.Length];
        for (int t = 0; t < tables.Length; t++)
        {
            var binding = definition.Tables[t];
            TableSnapshot? chosen = null;
            for (int i = chain.Length - 1; i >= 0 && chosen is null; i--)
            {
                var snap = data.InForce<TableSnapshot>(binding.Code, chain[i], effectiveDate);
                if (snap is null) continue;
                if (!schema.AllowsTier(binding.Schema.EditableAt, snap.Tier)) { notes.Add(new(CompileNoteKind.NotEditableAtTier, binding.Code, snap.Tier)); continue; }
                chosen = snap;
            }
            tables[t] = binding.Freeze(chosen?.Table);
            if (chosen is not null) dataVersion = Math.Max(dataVersion, chosen.Version);
            foreach (var r in tables[t].Rejected)
                notes.Add(new(CompileNoteKind.RowRejected, binding.Code, tables[t].Tier, r.Meta.RowId.ToString("N")));
        }

        // Runtime rules (05 §8): per code, the most specific version in force whose tier the group's policy allows.
        var runtime = SelectRuntimeRules(definition, chain, data, effectiveDate, notes, ref dataVersion);
        int codeRules = definition.Rules.Length;
        var rules = new RuleDescriptor<TSituation>[codeRules + runtime.Count];
        definition.Rules.CopyTo(rules);
        var ruleTier = new int[rules.Length];
        var ruleDataVersion = new long[rules.Length];
        for (int i = 0; i < runtime.Count; i++)
        {
            var (snap, group) = runtime[i];
            rules[codeRules + i] = snap.Rule.Bind(group, codeRules + i);
            ruleTier[codeRules + i] = snap.Tier;
            ruleDataVersion[codeRules + i] = snap.Version;
        }

        // Parameters: code < tier 1 < tier 2 …, per parameter name.
        var parameters = new ParameterValue?[definition.Parameters.Length];
        for (int r = 0; r < codeRules; r++)
        {
            var rule = definition.Rules[r];
            if (rule.Parameters.IsEmpty) continue;
            foreach (var path in chain)
            {
                var snap = data.InForce<ParameterSnapshot>(rule.Code, path, effectiveDate);
                if (snap is null) continue;
                foreach (var p in rule.Parameters)
                {
                    if (!snap.Values.TryGetValue(p.Name, out var raw)) continue;
                    if (!schema.AllowsTier(p.EditableAt, snap.Tier)) { notes.Add(new(CompileNoteKind.NotEditableAtTier, p.Key, snap.Tier)); continue; }
                    parameters[p.Slot] = p.CreateValue(raw, snap.Tier, snap.Version, out bool clamped);
                    if (clamped) notes.Add(new(CompileNoteKind.ParameterClamped, p.Key, snap.Tier));
                    ruleDataVersion[r] = Math.Max(ruleDataVersion[r], snap.Version);
                    dataVersion = Math.Max(dataVersion, snap.Version);
                }
            }
        }

        // Overrides: the most specific snapshot decides (runtime rules can be disabled too).
        var ruleDisabled = new int?[rules.Length];
        for (int r = 0; r < ruleDisabled.Length; r++)
            ruleDisabled[r] = ResolveOverride(rules[r].Code, rules[r].IsSealed, chain, data, effectiveDate, notes, ref dataVersion);
        var tableDisabled = new int?[definition.Tables.Length];
        for (int t = 0; t < tableDisabled.Length; t++)
            tableDisabled[t] = ResolveOverride(definition.Tables[t].Code, !definition.Tables[t].Schema.IsEditable, chain, data, effectiveDate, notes, ref dataVersion);

        var rulesByGroup = runtime.Count == 0 ? definition.RulesByGroup : MergeGroups(definition, rules);
        return new(definition, tierPath, tables, parameters, rules, rulesByGroup, ruleTier, ruleDisabled, tableDisabled, ruleDataVersion,
            [.. notes], dataVersion);
    }

    private static List<(RuntimeRuleSnapshot<TSituation> Snapshot, RuleGroup Group)> SelectRuntimeRules(RuleSetDefinition<TSituation> definition,
        RuleTierPath[] chain, RuleData data, DateOnly effectiveDate, List<CompileNote> notes, ref long dataVersion)
    {
        var selected = new List<(RuntimeRuleSnapshot<TSituation>, RuleGroup)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in data.Snapshots)
        {
            if (s is not RuntimeRuleSnapshot<TSituation> || !seen.Add(s.Code)) continue;
            if (definition.TryGetRule(s.Code, out _) || definition.TryGetTable(s.Code, out _))
            {
                notes.Add(new(CompileNoteKind.RuntimeRuleIgnored, s.Code, s.Tier, "code collides with a code rule/table"));
                continue;
            }
            for (int i = chain.Length - 1; i >= 1; i--)          // tier 0 = code: runtime rules are never "code"
            {
                var snap = data.InForce<RuntimeRuleSnapshot<TSituation>>(s.Code, chain[i], effectiveDate);
                if (snap is null) continue;
                var group = definition.FindGroup(snap.Rule.GroupName);
                if (group?.Dynamic is not { } policy || !TSituation.Schema.AllowsTier(policy.EditableAt, snap.Tier))
                {
                    notes.Add(new(CompileNoteKind.RuntimeRuleIgnored, snap.Code, snap.Tier, "group does not accept runtime rules at this tier"));
                    continue;
                }
                selected.Add((snap, group));
                dataVersion = Math.Max(dataVersion, snap.Version);
                break;
            }
        }
        // Per group cap (policy.MaxRules): highest priority first, then code — deterministic.
        selected.Sort(static (a, b) => a.Item2.Index != b.Item2.Index ? a.Item2.Index.CompareTo(b.Item2.Index)
            : a.Item1.Rule.Priority != b.Item1.Rule.Priority ? b.Item1.Rule.Priority.CompareTo(a.Item1.Rule.Priority)
            : string.CompareOrdinal(a.Item1.Code, b.Item1.Code));
        var kept = new List<(RuntimeRuleSnapshot<TSituation>, RuleGroup)>(selected.Count);
        int run = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            run = i > 0 && selected[i].Item2 == selected[i - 1].Item2 ? run + 1 : 1;
            if (run <= selected[i].Item2.Dynamic!.MaxRules) kept.Add(selected[i]);
            else notes.Add(new(CompileNoteKind.RuntimeRuleIgnored, selected[i].Item1.Code, selected[i].Item1.Tier, "group runtime rule limit reached"));
        }
        return kept;
    }

    /// <summary>Group membership incl. runtime rules — same order as code rules: priority desc, then code.</summary>
    private static int[][] MergeGroups(RuleSetDefinition<TSituation> definition, RuleDescriptor<TSituation>[] rules)
    {
        var groups = definition.Groups;
        var result = new int[groups.Length][];
        for (int g = 0; g < groups.Length; g++)
        {
            var rs = new List<int>();
            for (int i = 0; i < rules.Length; i++) if (rules[i].Group.Index == g) rs.Add(i);
            rs.Sort((a, b) => rules[a].Priority != rules[b].Priority
                ? rules[b].Priority.CompareTo(rules[a].Priority)
                : string.CompareOrdinal(rules[a].Code, rules[b].Code));
            result[g] = [.. rs];
        }
        return result;
    }

    /// <summary>The most specific override on the chain decides; returns the tier that disabled/replaced the rule.</summary>
    private static int? ResolveOverride(string code, bool isSealed, ReadOnlySpan<RuleTierPath> chain, RuleData data, DateOnly effectiveDate,
        List<CompileNote> notes, ref long dataVersion)
    {
        for (int i = chain.Length - 1; i >= 0; i--)
        {
            var snap = data.InForce<OverrideSnapshot>(code, chain[i], effectiveDate);
            if (snap is null) continue;
            if (isSealed && snap.Action.Kind != RuleOverrideKind.Enable)
            {
                notes.Add(new(CompileNoteKind.SealedOverrideIgnored, code, snap.Tier));
                return null;
            }
            dataVersion = Math.Max(dataVersion, snap.Version);
            return snap.Action.Kind is RuleOverrideKind.Disable or RuleOverrideKind.ReplaceWith ? snap.Tier : null;
        }
        return null;
    }
}

/// <summary>
/// Holds the published data of a rule set and compiles it per (tier path, effective date) on demand (04 §10). Publishing
/// swaps an immutable <see cref="RuleData"/> atomically and drops the compiled cache; evaluations in flight keep the set
/// they resolved. Clients keep the last <see cref="KeepVersions"/> versions per code/tier path so evaluations on past
/// effective dates work offline.
/// </summary>
public sealed partial class RuleSetHost<TSituation> where TSituation : ISituation<TSituation>
{
    private const int MaxCachedSets = 256;
    private RuleData _data;
    private ConcurrentDictionary<(RuleTierPath TierPath, DateOnly EffectiveDate), CompiledRuleSet<TSituation>> _cache = new();

    public RuleSetHost(RuleSetDefinition<TSituation> definition, RuleData? data = null, int keepVersions = 16)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _data = data ?? RuleData.Empty;
        KeepVersions = keepVersions;
    }

    public RuleSetDefinition<TSituation> Definition { get; }
    public RuleData Data => Volatile.Read(ref _data);

    /// <summary>Codec of published runtime rules (05 §8, e.g. YFex.Rules.Dynamic); null = this host takes none.</summary>
    public IRuntimeRuleCodec<TSituation>? RuntimeRules { get; private set; }

    /// <summary>Lets this host decode, validate and run runtime-authored rules. Set once, before data is loaded.</summary>
    public RuleSetHost<TSituation> UseRuntimeRules(IRuntimeRuleCodec<TSituation> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (RuntimeRules is not null && !ReferenceEquals(RuntimeRules, codec)) throw new InvalidOperationException("A runtime rule codec is already set.");
        RuntimeRules = codec;
        return this;
    }
    public int KeepVersions { get; }

    /// <summary>Raised after a snapshot was published (distribution, query invalidation).</summary>
    public event Action<RulesetSnapshot>? Published;

    /// <summary>Compiled set for <paramref name="tierPath"/> in effect on <paramref name="effectiveDate"/>.</summary>
    public CompiledRuleSet<TSituation> Resolve(in RuleTierPath tierPath, DateOnly effectiveDate)
    {
        var cache = Volatile.Read(ref _cache);
        var key = (tierPath, effectiveDate);
        if (cache.TryGetValue(key, out var set)) return set;
        set = CompiledRuleSet<TSituation>.Compile(Definition, tierPath, Data, effectiveDate);
        if (cache.Count >= MaxCachedSets) cache.Clear();
        return cache.GetOrAdd(key, set);
    }

    /// <summary>Resolves by the situation's tier path and effective date, and evaluates.</summary>
    public Decision<TSituation> Evaluate(in TSituation situation, in EvaluateOptions options = default)
        => Resolve(TSituation.TierPath(in situation), TSituation.EffectiveDate(in situation)).Evaluate(in situation, in options);

    /// <summary>Adds a published snapshot (store/distribution layer). Unknown codes throw.</summary>
    public void Publish(RulesetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool known = snapshot switch
        {
            TableSnapshot => Definition.TryGetTable(snapshot.Code, out _),
            RuntimeRuleSnapshot<TSituation> => true,                     // placement is checked per compile (group policy, tier)
            _ => Definition.TryGetRule(snapshot.Code, out _) || Definition.TryGetTable(snapshot.Code, out _) || IsRuntimeRule(snapshot.Code),
        };
        if (!known) throw new ArgumentException($"Rule set '{Definition.Name}' has no rule/table '{snapshot.Code}'.", nameof(snapshot));

        RuleData current, next;
        do
        {
            current = Volatile.Read(ref _data);
            next = current.With(snapshot, KeepVersions);
        }
        while (Interlocked.CompareExchange(ref _data, next, current) != current);
        Volatile.Write(ref _cache, new());
        Published?.Invoke(snapshot);
    }

    /// <summary>Convenience: publish a table version.</summary>
    public void Publish(TableVersion table) => Publish(new TableSnapshot(table));

    /// <summary>Decodes and publishes a stored record (distribution: a client fetched a new version).</summary>
    public void Apply(RuleDataRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Info.Key.RuleSet != Definition.Name) throw new ArgumentException($"Record of rule set '{record.Info.Key.RuleSet}', host of '{Definition.Name}'.", nameof(record));
        Publish(RuleDataCodec.Decode(Definition, record, RuntimeRules));
    }

    /// <summary>Overrides may target published runtime rules (disable a tenant's rule at a store).</summary>
    private bool IsRuntimeRule(string code)
    {
        foreach (var s in Data.Snapshots) if (s is RuntimeRuleSnapshot<TSituation> && s.Code == code) return true;
        return false;
    }

    /// <summary>
    /// Loads every published version of this rule set from <paramref name="store"/> (server start, client sync). Records
    /// that no longer decode (a table/rule removed from code) are skipped and returned so the app can log them.
    /// </summary>
    public async ValueTask<IReadOnlyList<(RuleDataRecord Record, Exception Error)>> LoadAsync(IRuleStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var records = await store.LoadAsync(Definition.Name, 0, ct).ConfigureAwait(false);
        var failures = new List<(RuleDataRecord, Exception)>();
        var data = Data;
        foreach (var record in records)
        {
            try { data = data.With(RuleDataCodec.Decode(Definition, record, RuntimeRules), KeepVersions); }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or NotSupportedException or FormatException)
            { failures.Add((record, e)); }
        }
        Volatile.Write(ref _data, data);
        Volatile.Write(ref _cache, new());
        return failures;
    }
}
