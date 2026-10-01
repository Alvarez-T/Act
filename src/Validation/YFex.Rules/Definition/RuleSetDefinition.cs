using System.Collections.Frozen;

namespace YFex.Rules;

/// <summary>
/// Frozen code part of a rule set (04 §4, §14): stages, groups, code rules, table bindings, derived fields, the optional
/// amount (type + Base/LineBase), rounding, allocation. Built once by the generated <c>Set</c> property through
/// <see cref="RuleSetDefinitionBuilder{TSituation}"/>.
/// Iteration orders are stable and total (determinism): groups by (stage order, declaration), rules by
/// (priority desc, code ordinal), tables after rules of equal priority by code ordinal.
/// </summary>
public sealed class RuleSetDefinition<TSituation> where TSituation : ISituation<TSituation>
{
    private readonly Stage[] _stages;
    private readonly RuleGroup[] _groups;
    private readonly RuleDescriptor<TSituation>[] _rules;
    private readonly TableBinding<TSituation>[] _tables;
    private readonly DerivedField<TSituation>[] _derived;
    private readonly RuleParameter[] _parameters;
    private readonly int[][] _rulesByGroup;
    private readonly int[][] _tablesByGroup;
    private readonly FrozenDictionary<string, int> _ruleByCode;
    private readonly FrozenDictionary<string, int> _tableByCode;
    private readonly FrozenDictionary<string, RuleParameter> _parameterByKey;
    private CompiledRuleSet<TSituation>? _codeOnly;

    internal RuleSetDefinition(string name, ulong codeVersion, Stage[] stages, RuleGroup[] groups, RuleDescriptor<TSituation>[] rules,
        TableBinding<TSituation>[] tables, DerivedField<TSituation>[] derived, RoundingPolicy rounding, Allocation allocation,
        RawType? amountType, LineBaseFn<TSituation>? lineBase, SituationBaseFn<TSituation>? @base)
    {
        Name = name;
        CodeVersion = codeVersion;
        _stages = stages;
        _groups = groups;
        _rules = rules;
        _tables = tables;
        _derived = derived;
        AmountType = amountType;
        Rounding = amountType is null ? rounding : Amounts.EffectiveRounding(amountType, rounding);
        Allocation = allocation;
        LineBase = lineBase;
        Base = @base;

        _rulesByGroup = new int[groups.Length][];
        _tablesByGroup = new int[groups.Length][];
        for (int g = 0; g < groups.Length; g++)
        {
            var rs = new List<int>();
            for (int i = 0; i < rules.Length; i++) if (rules[i].Group.Index == g) rs.Add(i);
            rs.Sort((a, b) => rules[a].Priority != rules[b].Priority
                ? rules[b].Priority.CompareTo(rules[a].Priority)
                : string.CompareOrdinal(rules[a].Code, rules[b].Code));
            _rulesByGroup[g] = [.. rs];
            var ts = new List<int>();
            for (int i = 0; i < tables.Length; i++) if (tables[i].Group.Index == g) ts.Add(i);
            ts.Sort((a, b) => tables[a].Priority != tables[b].Priority
                ? tables[b].Priority.CompareTo(tables[a].Priority)
                : string.CompareOrdinal(tables[a].Code, tables[b].Code));
            _tablesByGroup[g] = [.. ts];
        }

        var ruleByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var parameters = new List<RuleParameter>();
        for (int i = 0; i < rules.Length; i++)
        {
            if (!ruleByCode.TryAdd(rules[i].Code, i)) throw new InvalidOperationException($"Rule set '{name}': duplicate rule code '{rules[i].Code}'.");
            foreach (var p in rules[i].Parameters) parameters.Add(p);
        }
        var tableByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < tables.Length; i++)
            if (!tableByCode.TryAdd(tables[i].Code, i) || ruleByCode.ContainsKey(tables[i].Code))
                throw new InvalidOperationException($"Rule set '{name}': duplicate code '{tables[i].Code}'.");
        _ruleByCode = ruleByCode.ToFrozenDictionary(StringComparer.Ordinal);
        _tableByCode = tableByCode.ToFrozenDictionary(StringComparer.Ordinal);
        _parameters = [.. parameters];
        var byKey = new Dictionary<string, RuleParameter>(StringComparer.Ordinal);
        foreach (var p in _parameters) byKey[p.Key] = p;
        _parameterByKey = byKey.ToFrozenDictionary(StringComparer.Ordinal);
        Catalog = RuleCatalog.Build(this);
        Index = ApplicabilityIndex<TSituation>.Build(rules);
    }

    /// <summary>04 §12.1 pre-filter of situation-level rules (null = nothing indexable).</summary>
    internal ApplicabilityIndex<TSituation>? Index { get; }

    public string Name { get; }
    /// <summary>Hash of every code rule/table code + version (generator constant).</summary>
    public ulong CodeVersion { get; }
    public ReadOnlySpan<Stage> Stages => _stages;
    public ReadOnlySpan<RuleGroup> Groups => _groups;
    public ReadOnlySpan<RuleDescriptor<TSituation>> Rules => _rules;
    public ReadOnlySpan<TableBinding<TSituation>> Tables => _tables;
    public ReadOnlySpan<DerivedField<TSituation>> DerivedFields => _derived;
    public ReadOnlySpan<RuleParameter> Parameters => _parameters;
    /// <summary>
    /// Type of the amount the rule set accumulates and adjusts (Money, Mass, decimal…); null when it has none (then it only
    /// limits, grants, denies, warns and requires approvals).
    /// </summary>
    public RawType? AmountType { get; }
    public RoundingPolicy Rounding { get; }
    public Allocation Allocation { get; }
    public RuleCatalog Catalog { get; }
    internal LineBaseFn<TSituation>? LineBase { get; }
    internal SituationBaseFn<TSituation>? Base { get; }

    internal ReadOnlySpan<int> RulesOf(int group) => _rulesByGroup[group];
    internal int[][] RulesByGroup => _rulesByGroup;

    /// <summary>The group named <paramref name="name"/> (declaring field name), or null.</summary>
    public RuleGroup? FindGroup(string name)
    {
        foreach (var g in _groups) if (string.Equals(g.Name, name, StringComparison.Ordinal)) return g;
        return null;
    }
    internal ReadOnlySpan<int> TablesOf(int group) => _tablesByGroup[group];

    public bool TryGetRule(string code, out RuleDescriptor<TSituation> rule)
    {
        if (_ruleByCode.TryGetValue(code, out int i)) { rule = _rules[i]; return true; }
        rule = null!;
        return false;
    }

    public bool TryGetTable(string code, out TableBinding<TSituation> table)
    {
        if (_tableByCode.TryGetValue(code, out int i)) { table = _tables[i]; return true; }
        table = null!;
        return false;
    }

    /// <summary>Parameter by key (<c>"loyalty.gold.rate"</c>).</summary>
    public bool TryGetParameter(string key, out RuleParameter parameter) => _parameterByKey.TryGetValue(key, out parameter!);

    public bool TryGetDerived(ReadOnlySpan<char> name, out DerivedField<TSituation> field)
    {
        foreach (var d in _derived)
            if (name.SequenceEqual(d.Name)) { field = d; return true; }
        field = null!;
        return false;
    }

    /// <summary>The code-only compiled set (default rows, default parameters, no overrides).</summary>
    public CompiledRuleSet<TSituation> CodeOnly
        => _codeOnly ??= CompiledRuleSet<TSituation>.Compile(this, RuleTierPath.Code, RuleData.Empty, DateOnly.MaxValue);

    /// <summary>Evaluates with the code-only set.</summary>
    public Decision<TSituation> Evaluate(in TSituation situation, in EvaluateOptions options = default)
        => CodeOnly.Evaluate(in situation, in options);
}

/// <summary>Fluent builder used by the generated <c>Set</c> property.</summary>
public sealed class RuleSetDefinitionBuilder<TSituation>(string name, ulong codeVersion) where TSituation : ISituation<TSituation>
{
    private readonly List<(string Name, RuleGroup Group)> _groups = [];
    private readonly List<RuleDescriptor<TSituation>> _rules = [];
    private readonly List<TableBinding<TSituation>> _tables = [];
    private readonly List<(DerivedField<TSituation> Field, bool Requested)> _derived = [];
    private RoundingPolicy _rounding = RoundingPolicy.Currency();
    private Allocation _allocation = global::YFex.Rules.Allocation.Proportional();
    private RawType? _amountType;
    private Type? _amountClr;
    private LineBaseFn<TSituation>? _lineBase;
    private SituationBaseFn<TSituation>? _base;

    public RuleSetDefinitionBuilder<TSituation> Group(string groupName, RuleGroup group) { _groups.Add((groupName, group)); return this; }
    public RuleSetDefinitionBuilder<TSituation> Rule(RuleDescriptor<TSituation> rule) { _rules.Add(rule); return this; }
    public RuleSetDefinitionBuilder<TSituation> Table(TableBinding<TSituation> table) { _tables.Add(table); return this; }
    public RuleSetDefinitionBuilder<TSituation> Derived(DerivedField<TSituation> field, bool requested = false) { _derived.Add((field, requested)); return this; }
    public RuleSetDefinitionBuilder<TSituation> Rounding(RoundingPolicy rounding) { _rounding = rounding; return this; }
    public RuleSetDefinitionBuilder<TSituation> Allocation(Allocation allocation) { _allocation = allocation; return this; }
    /// <summary>Declares the amount type (<c>.Amount&lt;Money&gt;()</c>); required by <see cref="LineBase"/>/<see cref="Base"/>.</summary>
    public RuleSetDefinitionBuilder<TSituation> Amount<TAmount>() { _amountType = Amounts.TypeOf<TAmount>(); _amountClr = typeof(TAmount); return this; }
    public RuleSetDefinitionBuilder<TSituation> LineBase(LineBaseFn<TSituation> lineBase) { _lineBase = lineBase; return this; }
    public RuleSetDefinitionBuilder<TSituation> Base(SituationBaseFn<TSituation> @base) { _base = @base; return this; }

    public RuleSetDefinition<TSituation> Build()
    {
        // Stages: every stage reachable from a group or a derived field, ordered; duplicate orders = two chains.
        var stages = new List<Stage>();
        void AddStage(Stage s) { if (!stages.Contains(s)) stages.Add(s); }
        foreach (var (_, g) in _groups) AddStage(g.Stage);
        foreach (var (d, _) in _derived) AddStage(d.Stage);
        stages.Sort((a, b) => a.Order.CompareTo(b.Order));
        for (int i = 1; i < stages.Count; i++)
            if (stages[i].Order == stages[i - 1].Order)
                throw new InvalidOperationException($"Rule set '{name}': stages '{stages[i - 1].Code}' and '{stages[i].Code}' have the same order — chain them with Stage.After.");

        // Groups: stage order, then declaration order.
        var ordered = new List<(string Name, RuleGroup Group, int Decl)>();
        for (int i = 0; i < _groups.Count; i++) ordered.Add((_groups[i].Name, _groups[i].Group, i));
        ordered.Sort((a, b) => a.Group.Stage.Order != b.Group.Stage.Order ? a.Group.Stage.Order.CompareTo(b.Group.Stage.Order) : a.Decl.CompareTo(b.Decl));
        var groups = new RuleGroup[ordered.Count];
        var byName = new Dictionary<string, RuleGroup>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
        {
            var g = ordered[i].Group;
            if (g.Index >= 0 && g.Name != ordered[i].Name) throw new InvalidOperationException($"Group '{ordered[i].Name}' is already registered as '{g.Name}'.");
            g.Name = ordered[i].Name;
            g.Index = i;
            groups[i] = g;
            if (!byName.TryAdd(g.Name, g)) throw new InvalidOperationException($"Rule set '{name}': duplicate group '{g.Name}'.");
        }

        var rules = _rules.ToArray();
        int slot = 0;
        for (int i = 0; i < rules.Length; i++)
        {
            if (!byName.TryGetValue(rules[i].GroupName, out var g))
                throw new InvalidOperationException($"Rule '{rules[i].Code}': unknown group '{rules[i].GroupName}' in rule set '{name}'.");
            rules[i].Group = g;
            rules[i].Index = i;
            foreach (var p in rules[i].Parameters) p.Register(rules[i].Code, slot++);
        }
        var tables = _tables.ToArray();
        for (int i = 0; i < tables.Length; i++)
        {
            if (!byName.TryGetValue(tables[i].GroupName, out var g))
                throw new InvalidOperationException($"Table '{tables[i].Code}': unknown group '{tables[i].GroupName}' in rule set '{name}'.");
            tables[i].Group = g;
            tables[i].Index = i;
        }
        if ((_lineBase is not null || _base is not null) && _amountType is null)
            throw new InvalidOperationException($"Rule set '{name}': Base/LineBase need the amount type (.Amount<T>()).");
        var derived = new DerivedField<TSituation>[_derived.Count];
        for (int i = 0; i < derived.Length; i++)
        {
            var field = _derived[i].Field;
            if (field.AmountType != _amountClr)
                throw new InvalidOperationException(_amountClr is null
                    ? $"Rule set '{name}': derived field '{field.Name}' reads RunningTotals<{field.AmountType.Name}> but the rule set has no amount (declare Base or LineBase)."
                    : $"Rule set '{name}': derived field '{field.Name}' reads RunningTotals<{field.AmountType.Name}>; the rule set's amount is {_amountClr.Name}.");
            field.Register((ushort)(SituationFieldRef.DerivedBase + i), _derived[i].Requested);
            derived[i] = field;
        }
        return new RuleSetDefinition<TSituation>(name, codeVersion, [.. stages], groups, rules, tables, derived, _rounding, _allocation,
            _amountType, _lineBase, _base);
    }
}

/// <summary>
/// One rule/table of a rule set as the catalog shows it (id ↔ code, group, stage, editability, version).
/// <paramref name="EditableAt"/> = rule tiers that may publish data for it (table rows / parameter values).
/// </summary>
public readonly record struct RuleCatalogEntry(int Id, string Code, string Group, StageRef Stage, bool IsTable, bool IsSealed,
    int Version, string[] EditableAt, int ConditionCount, string? PublishPermission, string? TypeName);

/// <summary>Rule catalog (04 §4): what the editor, the explanation and <c>GetRuleDefinition</c> list.</summary>
public sealed class RuleCatalog
{
    private readonly RuleCatalogEntry[] _entries;

    private RuleCatalog(string ruleSet, RuleCatalogEntry[] entries)
    {
        RuleSet = ruleSet;
        _entries = entries;
    }

    public string RuleSet { get; }
    public ReadOnlySpan<RuleCatalogEntry> Entries => _entries;

    public bool TryGet(string code, out RuleCatalogEntry entry)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Code, code, StringComparison.Ordinal)) { entry = e; return true; }
        entry = default;
        return false;
    }

    internal static RuleCatalog Build<TS>(RuleSetDefinition<TS> set) where TS : ISituation<TS>
    {
        var list = new List<RuleCatalogEntry>();
        foreach (var r in set.Rules)
        {
            var editable = new List<string>();
            foreach (var p in r.Parameters)
                foreach (var tier in p.EditableAt)
                    if (!editable.Contains(tier)) editable.Add(tier);
            list.Add(new(r.Index, r.Code, r.Group.Name, r.Group.Stage.Ref, false, r.IsSealed, r.Version, [.. editable], r.Conditions.Length, null, r.TypeName));
        }
        foreach (var t in set.Tables)
            list.Add(new(set.Rules.Length + t.Index, t.Code, t.Group.Name, t.Group.Stage.Ref, true, !t.Schema.IsEditable,
                t.Version, t.Schema.EditableAt.ToArray(), 0, t.Schema.PublishPermission, null));
        return new(set.Name, [.. list]);
    }
}
