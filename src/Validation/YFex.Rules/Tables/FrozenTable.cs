using System.Collections.Immutable;
using YFex.Validation;

namespace YFex.Rules;

/// <summary>A row excluded when freezing (failed its shape/guardrail checks) — traced, never evaluated.</summary>
public readonly record struct RejectedRow(int RowIndex, RowMeta Meta, ViolationList Violations);

/// <summary>
/// Evaluation-ready, immutable version of a table (05 §2): rows that passed their checks + metadata + version info.
/// Built once per published version (any source) by <see cref="DecisionTable.Freeze{TS,TTable,TRow}"/>.
/// </summary>
public sealed class FrozenTable<TRow>
{
    private readonly TRow[] _rows;
    private readonly RowMeta[] _meta;

    internal FrozenTable(in TableVersionInfo info, TRow[] rows, RowMeta[] meta, ImmutableArray<RejectedRow> rejected)
    {
        Info = info;
        _rows = rows;
        _meta = meta;
        Rejected = rejected;
    }

    public TableVersionInfo Info { get; }
    public int Tier => Info.Tier;
    public ReadOnlySpan<TRow> Rows => _rows;
    public ReadOnlySpan<RowMeta> Meta => _meta;
    public int Count => _rows.Length;
    public ImmutableArray<RejectedRow> Rejected { get; }

    internal ref readonly TRow Row(int index) => ref _rows[index];
    internal RowMeta RowMetaAt(int index) => _meta[index];
}

/// <summary>Runtime algorithms shared by every generated table (freeze, publish analysis).</summary>
public static class DecisionTable
{
    /// <summary>
    /// Builds the evaluation form of <paramref name="rows"/>: each row is checked (row <c>[Validatable]</c> rules +
    /// guardrail); failing rows are excluded and reported in <see cref="FrozenTable{TRow}.Rejected"/> (a bad store
    /// can never exceed the guardrails). <paramref name="meta"/> empty → deterministic code row ids.
    /// </summary>
    public static FrozenTable<TRow> Freeze<TS, TTable, TRow>(ReadOnlySpan<TRow> rows, ReadOnlySpan<RowMeta> meta, in TableVersionInfo info)
        where TS : ISituation<TS>
        where TTable : IDecisionTable<TS, TRow>
    {
        if (!meta.IsEmpty && meta.Length != rows.Length) throw new ArgumentException("Meta must be parallel to rows.", nameof(meta));
        var kept = new List<TRow>(rows.Length);
        var keptMeta = new List<RowMeta>(rows.Length);
        ImmutableArray<RejectedRow>.Builder? rejected = null;
        for (int i = 0; i < rows.Length; i++)
        {
            var m = meta.IsEmpty ? new RowMeta(RowMeta.CodeRowId(info.TableCode, i)) : meta[i];
            using var scope = ViolationWriter.Begin(out var w);
            TTable.CheckRow(in rows[i], ref w);
            if (w.HasErrors)
            {
                (rejected ??= ImmutableArray.CreateBuilder<RejectedRow>()).Add(new(i, m, w.ToResult().Violations));
                continue;
            }
            kept.Add(rows[i]);
            keptMeta.Add(m);
        }
        return new FrozenTable<TRow>(in info, [.. kept], [.. keptMeta], rejected?.ToImmutable() ?? []);
    }

    /// <summary>
    /// Publish-time analysis (05 §1.3): per-row checks, overlaps (Unique; equal-priority for Priority) = errors,
    /// unreachable rows (First) = warnings, and for <c>Complete</c> tables uncovered inputs (<see cref="TableGaps"/>) =
    /// errors. Paths are <c>Rows[i].Column</c> (gaps: <c>Rows</c>) so the editor highlights cells.
    /// Returns false when any error was written.
    /// </summary>
    public static bool Check<TS, TTable, TRow>(ReadOnlySpan<TRow> rows, ReadOnlySpan<RowMeta> meta, ref ViolationWriter w)
        where TS : ISituation<TS>
        where TTable : IDecisionTable<TS, TRow>
    {
        int errorsBefore = w.ErrorCount;
        for (int i = 0; i < rows.Length; i++)
        {
            using var scope = ViolationWriter.Begin(out var rw);
            TTable.CheckRow(in rows[i], ref rw);
            foreach (ref readonly var v in rw.AsSpan())
                w.Add(v.WithPath(v.Path.IsEmpty ? FieldPath.Of("Rows[]", i) : v.Path.Prefixed("Rows[]", i)));
        }

        var policy = TTable.Schema.HitPolicy;
        int columns = TTable.ConditionCount;
        for (int j = 1; j < rows.Length; j++)
        {
            for (int i = 0; i < j; i++)
            {
                if (policy == HitPolicy.First)
                {
                    if (AllColumns<TS, TTable, TRow>(rows, i, j, columns, covers: true))
                    {
                        w.Add(FieldPath.Of("Rows[]", j), "rules.table.unreachable", RawValue.From(i), Severity.Warning);
                        break;
                    }
                }
                else if (policy == HitPolicy.Unique
                      || (policy == HitPolicy.Priority && Priority(meta, i) == Priority(meta, j)))
                {
                    if (AllColumns<TS, TTable, TRow>(rows, i, j, columns, covers: false))
                        w.Add(FieldPath.Of("Rows[]", j), "rules.table.overlap", RawValue.From(i));
                }
            }
        }
        if (TTable.Schema.Complete) TableGaps.Check<TS, TTable, TRow>(rows, ref w);
        return w.ErrorCount == errorsBefore;

        static int Priority(ReadOnlySpan<RowMeta> meta, int i) => meta.IsEmpty ? 0 : meta[i].Priority;
    }

    private static bool AllColumns<TS, TTable, TRow>(ReadOnlySpan<TRow> rows, int i, int j, int columns, bool covers)
        where TS : ISituation<TS>
        where TTable : IDecisionTable<TS, TRow>
    {
        for (int c = 0; c < columns; c++)
            if (covers ? !TTable.Covers(in rows[i], in rows[j], c) : !TTable.Intersects(in rows[i], in rows[j], c)) return false;
        return true;
    }

}
