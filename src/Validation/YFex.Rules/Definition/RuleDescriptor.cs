namespace YFex.Rules;

/// <summary>Adapter to a rule's <c>Applies</c> (the generator bridges both accepted shapes).</summary>
public delegate bool RuleAppliesFn<TSituation>(in TSituation s, in RuleContext c);
/// <summary>Adapter to a rule's <c>Evaluate</c>.</summary>
public delegate void RuleEvaluateFn<TSituation>(in TSituation s, in RuleContext c, ref DirectiveWriter d);
/// <summary>Adapter to a line rule's <c>Applies</c> (the generated body reads line <paramref name="line"/> itself).</summary>
public delegate bool LineAppliesFn<TSituation>(in TSituation s, int line, in RuleContext c);
/// <summary>Adapter to a line rule's <c>Evaluate</c>.</summary>
public delegate void LineEvaluateFn<TSituation>(in TSituation s, int line, in RuleContext c, ref DirectiveWriter d);
/// <summary>Generated "why not": index of the first false split condition, or <c>byte.MaxValue</c> (06 §4).</summary>
public delegate byte RuleExplainFn<TSituation>(in TSituation s, int line);
/// <summary>
/// Base amount of line <paramref name="line"/> before adjustments (a rule set's <c>LineBase</c> method), as the decimal
/// storage of the rule set's amount type (the generator converts Money/Mass/… with <see cref="Amounts.ToDecimal{T}"/>).
/// </summary>
public delegate decimal LineBaseFn<TSituation>(in TSituation s, int line);
/// <summary>Base amount of a situation without lines (a rule set's <c>Base</c> method), as decimal storage.</summary>
public delegate decimal SituationBaseFn<TSituation>(in TSituation s);

/// <summary>
/// One split applicability condition, for the trace and the catalog (06 §4). <see cref="MessageKey"/> is a generic
/// template (<c>"rules.cond.eq"</c>) formatted with the field display name and <see cref="Value"/>.
/// </summary>
public readonly record struct ConditionInfo(byte Index, string Text, string MessageKey, string? Field, RawValue Value);

/// <summary>
/// Frozen description of one code rule inside a rule set (built by generated code — never by hand in apps).
/// Calls go through cached static lambdas that call the rule's static methods directly.
/// </summary>
public sealed class RuleDescriptor<TSituation> where TSituation : ISituation<TSituation>
{
    private readonly ConditionInfo[] _conditions;
    private readonly RuleParameter[] _parameters;

    private RuleDescriptor(string code, string groupName, int priority, int version, bool isSealed, DateOnly? from, DateOnly? to,
        RuleAppliesFn<TSituation>? applies, RuleEvaluateFn<TSituation>? evaluate,
        LineAppliesFn<TSituation>? lineApplies, LineEvaluateFn<TSituation>? lineEvaluate,
        RuleExplainFn<TSituation>? explain, ConditionInfo[]? conditions, RuleParameter[]? parameters, string? typeName)
    {
        Code = code;
        GroupName = groupName;
        Priority = priority;
        Version = version;
        IsSealed = isSealed;
        EffectiveFrom = from;
        EffectiveTo = to;
        Applies = applies;
        Evaluate = evaluate;
        LineApplies = lineApplies;
        LineEvaluate = lineEvaluate;
        Explain = explain;
        _conditions = conditions ?? [];
        _parameters = parameters ?? [];
        TypeName = typeName;
    }

    public string Code { get; }
    public string GroupName { get; }
    public int Priority { get; }
    public int Version { get; }
    public bool IsSealed { get; }
    public DateOnly? EffectiveFrom { get; }
    public DateOnly? EffectiveTo { get; }
    public bool IsLineRule => LineEvaluate is not null;
    /// <summary>CLR type name (diagnostics/catalog).</summary>
    public string? TypeName { get; }
    public ReadOnlySpan<ConditionInfo> Conditions => _conditions;
    public ReadOnlySpan<RuleParameter> Parameters => _parameters;

    internal RuleAppliesFn<TSituation>? Applies { get; }
    internal RuleEvaluateFn<TSituation>? Evaluate { get; }
    internal LineAppliesFn<TSituation>? LineApplies { get; }
    internal LineEvaluateFn<TSituation>? LineEvaluate { get; }
    internal RuleExplainFn<TSituation>? Explain { get; }

    /// <summary>Set when the definition is built.</summary>
    public RuleGroup Group { get; internal set; } = null!;
    /// <summary>Index inside the definition (also the violation origin owner id).</summary>
    public int Index { get; internal set; } = -1;

    public bool IsEffective(DateOnly effectiveDate)
        => (EffectiveFrom is not { } f || effectiveDate >= f) && (EffectiveTo is not { } t || effectiveDate <= t);

    /// <summary>Copy placed into a compiled rule set (runtime rules: one template, a group + index per compiled set).</summary>
    internal RuleDescriptor<TSituation> Bind(RuleGroup group, int index)
        => new(Code, GroupName, Priority, Version, IsSealed, EffectiveFrom, EffectiveTo, Applies, Evaluate, LineApplies, LineEvaluate,
            Explain, _conditions, _parameters, TypeName) { Group = group, Index = index };

    /// <summary>Situation-level rule.</summary>
    public static RuleDescriptor<TSituation> Create(string code, string group, RuleAppliesFn<TSituation> applies, RuleEvaluateFn<TSituation> evaluate,
        int priority = 0, int version = 1, bool isSealed = false, DateOnly? effectiveFrom = null, DateOnly? effectiveTo = null,
        RuleExplainFn<TSituation>? explain = null, ConditionInfo[]? conditions = null, RuleParameter[]? parameters = null, string? typeName = null)
        => new(code, group, priority, version, isSealed, effectiveFrom, effectiveTo, applies, evaluate, null, null, explain, conditions, parameters, typeName);

    /// <summary>Line rule (evaluated per line).</summary>
    public static RuleDescriptor<TSituation> CreateLine(string code, string group, LineAppliesFn<TSituation> applies, LineEvaluateFn<TSituation> evaluate,
        int priority = 0, int version = 1, bool isSealed = false, DateOnly? effectiveFrom = null, DateOnly? effectiveTo = null,
        RuleExplainFn<TSituation>? explain = null, ConditionInfo[]? conditions = null, RuleParameter[]? parameters = null, string? typeName = null)
        => new(code, group, priority, version, isSealed, effectiveFrom, effectiveTo, null, null, applies, evaluate, explain, conditions, parameters, typeName);

    public override string ToString() => Code;
}

/// <summary>Definition-level binding of a decision table into a rule set group.</summary>
public abstract class TableBinding<TSituation> where TSituation : ISituation<TSituation>
{
    protected TableBinding(string groupName, int priority, int version)
    {
        GroupName = groupName;
        Priority = priority;
        Version = version;
    }

    public abstract string Code { get; }
    public abstract TableSchema Schema { get; }
    public string GroupName { get; }
    public int Priority { get; }
    /// <summary>Definition (code) version of the table's columns/logic.</summary>
    public int Version { get; }
    public bool IsLineTable => Schema.IsLineTable;
    public RuleGroup Group { get; internal set; } = null!;
    public int Index { get; internal set; } = -1;

    /// <summary>Freezes <paramref name="version"/> (<see langword="null"/> = the code default rows).</summary>
    public abstract TableInstance<TSituation> Freeze(TableVersion? version);

    /// <summary>Publish-time analysis of a candidate version (rows + meta).</summary>
    public abstract bool Check(TableVersion version, ref YFex.Validation.ViolationWriter w);

    /// <summary>Row document of a version (store payload, file export, editor).</summary>
    public abstract byte[] Serialize(TableVersion version);

    /// <summary>Row document of the code default rows.</summary>
    public abstract byte[] SerializeDefaults();

    /// <summary>
    /// Parses a row document into a version. Rows without an id get a new one (<paramref name="newRowId"/>, default
    /// <see cref="Guid.NewGuid"/>) — ids are stable across later versions because the editor sends them back.
    /// </summary>
    public abstract TableVersion Parse(ReadOnlySpan<byte> json, in TableVersionInfo info, Func<Guid>? newRowId = null);

    public override string ToString() => Code;
}

/// <summary>Typed binding: every call is a static call on <typeparamref name="TTable"/>.</summary>
public sealed class TableBinding<TSituation, TTable, TRow>(string groupName, int priority = 0, int version = 1)
    : TableBinding<TSituation>(groupName, priority, version)
    where TSituation : ISituation<TSituation>
    where TTable : IDecisionTable<TSituation, TRow>
{
    public override string Code => TTable.Code;
    public override TableSchema Schema => TTable.Schema;

    public override TableInstance<TSituation> Freeze(TableVersion? version)
    {
        if (version is null)
            return new TableInstance<TSituation, TTable, TRow>(this,
                DecisionTable.Freeze<TSituation, TTable, TRow>(TTable.DefaultRows, default,
                    new TableVersionInfo(TTable.Code, 0, RuleTierPath.Code, DateOnly.MinValue)));
        if (version is not TableVersion<TRow> typed)
            throw new ArgumentException($"Table '{TTable.Code}' expects rows of {typeof(TRow).Name}.", nameof(version));
        return new TableInstance<TSituation, TTable, TRow>(this,
            DecisionTable.Freeze<TSituation, TTable, TRow>(typed.Rows.AsSpan(), typed.Meta.AsSpan(), typed.Info));
    }

    public override bool Check(TableVersion version, ref YFex.Validation.ViolationWriter w)
    {
        if (version is not TableVersion<TRow> typed)
            throw new ArgumentException($"Table '{TTable.Code}' expects rows of {typeof(TRow).Name}.", nameof(version));
        return DecisionTable.Check<TSituation, TTable, TRow>(typed.Rows.AsSpan(), typed.Meta.AsSpan(), ref w);
    }

    public override byte[] Serialize(TableVersion version)
    {
        if (version is not TableVersion<TRow> typed)
            throw new ArgumentException($"Table '{TTable.Code}' expects rows of {typeof(TRow).Name}.", nameof(version));
        return TableJson.Write(typed.Rows.AsSpan(), typed.Meta.AsSpan(), static (w, in row) => TTable.WriteRow(w, in row));
    }

    public override byte[] SerializeDefaults()
    {
        var rows = TTable.DefaultRows;
        var meta = new RowMeta[rows.Length];
        for (int i = 0; i < meta.Length; i++) meta[i] = new RowMeta(RowMeta.CodeRowId(TTable.Code, i));
        return TableJson.Write(rows, meta, static (w, in row) => TTable.WriteRow(w, in row));
    }

    public override TableVersion Parse(ReadOnlySpan<byte> json, in TableVersionInfo info, Func<Guid>? newRowId = null)
    {
        var (rows, meta) = TableJson.Read(json, static (ref System.Text.Json.Utf8JsonReader r) => TTable.ReadRow(ref r));
        for (int i = 0; i < meta.Length; i++)
            if (meta[i].RowId == Guid.Empty) meta[i] = meta[i] with { RowId = newRowId?.Invoke() ?? Guid.NewGuid() };
        return new TableVersion<TRow>(in info, [.. rows], [.. meta]);
    }
}

/// <summary>A frozen table version attached to a compiled rule set; the engine drives it.</summary>
public abstract class TableInstance<TSituation> where TSituation : ISituation<TSituation>
{
    protected TableInstance(TableBinding<TSituation> binding) => Binding = binding;

    public TableBinding<TSituation> Binding { get; }
    public abstract TableVersionInfo Info { get; }
    /// <summary>Tier depth the version was published at (0 = code rows).</summary>
    public int Tier => Info.Tier;
    public abstract int RowCount { get; }
    public abstract System.Collections.Immutable.ImmutableArray<RejectedRow> Rejected { get; }
    public abstract RowMeta Meta(int row);

    /// <summary>
    /// Writes the indices of the rows selected by the hit policy into <paramref name="into"/>; returns how many.
    /// When none matches, <paramref name="closestRow"/>/<paramref name="closestColumn"/> tell which row came closest and
    /// which column failed ("why not").
    /// </summary>
    public abstract int Select(in TSituation s, int line, DateOnly effectiveDate, Span<int> into, out int closestRow, out int closestColumn);

    public abstract void Emit(int row, in TSituation s, int line, ref DirectiveWriter d);
}

internal sealed class TableInstance<TSituation, TTable, TRow>(TableBinding<TSituation> binding, FrozenTable<TRow> table)
    : TableInstance<TSituation>(binding)
    where TSituation : ISituation<TSituation>
    where TTable : IDecisionTable<TSituation, TRow>
{
    public FrozenTable<TRow> Table { get; } = table;
    public override TableVersionInfo Info => Table.Info;
    public override int RowCount => Table.Count;
    public override System.Collections.Immutable.ImmutableArray<RejectedRow> Rejected => Table.Rejected;
    public override RowMeta Meta(int row) => Table.RowMetaAt(row);

    public override int Select(in TSituation s, int line, DateOnly effectiveDate, Span<int> into, out int closestRow, out int closestColumn)
    {
        closestRow = -1;
        closestColumn = -1;
        var policy = TTable.Schema.HitPolicy;
        int count = 0, best = -1, bestPriority = int.MinValue;
        for (int i = 0; i < Table.Count; i++)
        {
            var meta = Table.RowMetaAt(i);
            if (!meta.IsEffective(effectiveDate)) continue;
            int failed = TTable.Match(in Table.Row(i), in s, line);
            if (failed >= 0)
            {
                if (failed > closestColumn) { closestColumn = failed; closestRow = i; }
                continue;
            }
            switch (policy)
            {
                case HitPolicy.First:
                case HitPolicy.Unique:
                    into[0] = i;
                    return 1;
                case HitPolicy.Priority:
                    if (meta.Priority > bestPriority) { bestPriority = meta.Priority; best = i; }
                    break;
                default:
                    into[count++] = i;
                    break;
            }
        }
        if (policy == HitPolicy.Priority && best >= 0) { into[0] = best; return 1; }
        return count;
    }

    public override void Emit(int row, in TSituation s, int line, ref DirectiveWriter d) => TTable.Emit(in Table.Row(row), in s, line, ref d);
}
