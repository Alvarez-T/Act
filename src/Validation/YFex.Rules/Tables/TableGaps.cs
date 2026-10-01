using System.Text;
using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// A condition cell as untyped values (generated <c>GetCondition</c>): <see cref="MatchKind"/> + value(s) for
/// <see cref="CellShape.Match"/>/<see cref="CellShape.Value"/>, half-open [From, To) for <see cref="CellShape.Band"/>.
/// Used by publish analysis (<c>Complete</c> gap check) and non-generic editors; never on the evaluation path.
/// </summary>
public readonly struct ConditionCell
{
    private readonly RawValue[]? _values;

    private ConditionCell(bool known, CellShape shape, MatchKind match, RawValue value, RawValue[]? values, bool hasFrom, RawValue from, bool hasTo, RawValue to)
    {
        IsKnown = known;
        Shape = shape;
        MatchKind = match;
        Value = value;
        _values = values;
        HasFrom = hasFrom;
        From = from;
        HasTo = hasTo;
        To = to;
    }

    /// <summary>False for tables whose generator predates <c>GetCondition</c> (analysis not possible).</summary>
    public bool IsKnown { get; }
    public CellShape Shape { get; }
    public MatchKind MatchKind { get; }
    public RawValue Value { get; }
    public ReadOnlySpan<RawValue> Values => _values;
    public bool HasFrom { get; }
    public RawValue From { get; }
    public bool HasTo { get; }
    /// <summary>Exclusive upper bound.</summary>
    public RawValue To { get; }
    public bool IsAny => Shape == CellShape.Band ? !HasFrom && !HasTo : Shape == CellShape.Match && MatchKind == MatchKind.Any;

    public static ConditionCell Unknown => default;

    public static ConditionCell OfValue(in RawValue value) => new(true, CellShape.Value, MatchKind.Is, value, null, false, default, false, default);

    /// <summary>A <see cref="Match{T}"/> cell of a scalar / <see cref="IRawValue{TSelf}"/> type.</summary>
    public static ConditionCell Of<T>(in Match<T> cell)
    {
        RawValue[]? set = null;
        if (cell.Kind == MatchKind.In)
        {
            set = new RawValue[cell.Values.Length];
            for (int i = 0; i < set.Length; i++) set[i] = RawValueConvert.From(cell.Values[i]);
        }
        return new(true, CellShape.Match, cell.Kind, cell.Kind is MatchKind.Is or MatchKind.Not ? RawValueConvert.From(cell.Value) : default,
            set, false, default, false, default);
    }

    /// <summary>A <see cref="Match{T}"/> cell of an enum.</summary>
    public static ConditionCell OfEnum<TEnum>(in Match<TEnum> cell) where TEnum : unmanaged, Enum
    {
        RawValue[]? set = null;
        if (cell.Kind == MatchKind.In)
        {
            set = new RawValue[cell.Values.Length];
            for (int i = 0; i < set.Length; i++) set[i] = RawValue.FromEnum(cell.Values[i]);
        }
        return new(true, CellShape.Match, cell.Kind, cell.Kind is MatchKind.Is or MatchKind.Not ? RawValue.FromEnum(cell.Value) : default,
            set, false, default, false, default);
    }

    public static ConditionCell Of<T>(in Band<T> cell) where T : IComparable<T>
        => new(true, CellShape.Band, MatchKind.Any, default, null,
            cell.HasFrom, cell.HasFrom ? RawValueConvert.From(cell.FromValue) : default,
            cell.HasTo, cell.HasTo ? RawValueConvert.From(cell.ToValue) : default);

    /// <summary>Equality-style test of a Match/Value cell (Band cells: containment).</summary>
    public bool Matches(in RawValue v)
    {
        switch (Shape)
        {
            case CellShape.Band:
                return (!HasFrom || v.CompareTo(From) >= 0) && (!HasTo || v.CompareTo(To) < 0);
            case CellShape.Value:
                return Value.Equals(v);
        }
        switch (MatchKind)
        {
            case MatchKind.Any: return true;
            case MatchKind.Is: return Value.Equals(v);
            case MatchKind.Not: return !Value.Equals(v);
            default:
                foreach (ref readonly var x in Values) if (x.Equals(v)) return true;
                return false;
        }
    }
}

/// <summary>Publish-time <c>Complete = true</c> analysis (05 §1.3): every possible input must match some row.</summary>
public static class TableGaps
{
    /// <summary>More combinations than this → the check is skipped with a warning.</summary>
    public const int MaxCombinations = 1_000_000;

    /// <summary>Reported gaps (the first ones in column order); the arg of each is the total count.</summary>
    public const int MaxReported = 5;

    /// <summary>
    /// Partitions every condition column into the points that matter — the closed domain (enum members, bool) or the
    /// listed values plus "any other value" for Match/Value columns, the intervals between all band edges for Band
    /// columns — and looks for a combination no row covers. Row effective windows are not considered (a windowed row
    /// counts as covering). Violations: <c>rules.table.gap</c> (error, path <c>Rows</c>, arg 0 = the combination as text,
    /// arg 1 = total gaps); <c>rules.table.complete-unverifiable</c> (warning) when the table can't be analysed.
    /// </summary>
    public static void Check<TS, TTable, TRow>(ReadOnlySpan<TRow> rows, ref ViolationWriter w)
        where TS : ISituation<TS>
        where TTable : IDecisionTable<TS, TRow>
    {
        int columns = TTable.ConditionCount;
        var names = WhenNames(TTable.Schema, columns);
        if (rows.IsEmpty)
        {
            w.Add(FieldPath.Of("Rows"), RuleEngineCodes.TableGap, RawValue.From("*"), RawValue.From(1));
            return;
        }
        if (columns == 0) return;

        var cells = new ConditionCell[columns][];
        for (int c = 0; c < columns; c++)
        {
            cells[c] = new ConditionCell[rows.Length];
            for (int r = 0; r < rows.Length; r++)
            {
                var cell = TTable.GetCondition(in rows[r], c);
                if (!cell.IsKnown) { Unverifiable(ref w, "table has no condition metadata (rebuild with the current generator)"); return; }
                cells[c][r] = cell;
            }
        }

        // Per column: the points, and for each point the rows whose cell covers it (bitset over rows).
        int words = (rows.Length + 63) >> 6;
        var axes = new Axis[columns];
        long combinations = 1;
        for (int c = 0; c < columns; c++)
        {
            if (!TryBuildAxis(cells[c], TTable.ConditionDomain(c), words, out axes[c], out string? why)) { Unverifiable(ref w, $"{names[c]}: {why}"); return; }
            combinations *= axes[c].Groups.Count;
            if (combinations > MaxCombinations) { Unverifiable(ref w, $"more than {MaxCombinations} input combinations"); return; }
        }

        // Odometer over the points; AND of the per-column row sets must be non-empty.
        var index = new int[columns];
        var scratch = new ulong[words];
        var gaps = new List<string>();
        int total = 0;
        while (true)
        {
            axes[0].Covering[index[0]].CopyTo(scratch, 0);
            for (int c = 1; c < columns; c++)
            {
                var set = axes[c].Covering[index[c]];
                for (int i = 0; i < words; i++) scratch[i] &= set[i];
            }
            bool any = false;
            foreach (ulong x in scratch) any |= x != 0;
            if (!any)
            {
                total++;
                if (gaps.Count < MaxReported) gaps.Add(Describe(axes, index, names));
            }

            int k = columns - 1;
            while (k >= 0 && ++index[k] == axes[k].Groups.Count) index[k--] = 0;
            if (k < 0) break;
        }
        foreach (var g in gaps) w.Add(FieldPath.Of("Rows"), RuleEngineCodes.TableGap, RawValue.From(g), RawValue.From(total));
    }

    private static void Unverifiable(ref ViolationWriter w, string why)
        => w.Add(new Violation(FieldPath.Of("Rows"), RuleEngineCodes.TableCompleteUnverifiable, Severity.Warning, detail: why));

    private static string[] WhenNames(TableSchema schema, int columns)
    {
        var names = new string[columns];
        int n = 0;
        foreach (var col in schema.Columns)
            if (col.Role == ColumnRole.When && n < columns) names[n++] = col.Name;
        for (; n < columns; n++) names[n] = "#" + n;
        return names;
    }

    // ── Axes ────────────────────────────────────────────────────────────────

    private enum PointKind : byte { Value, Other, Interval }

    /// <summary>One representative input of a column: a value, "any other value", or band interval [Low, High).</summary>
    private readonly record struct Point(PointKind Kind, RawValue Value, bool HasLow, RawValue Low, bool HasHigh, RawValue High);

    /// <summary>
    /// A column's points grouped by the rows that cover them (points no row tells apart are one group) — fewer
    /// combinations, and a gap names only the columns that matter.
    /// </summary>
    private sealed class Axis(List<List<Point>> groups, ulong[][] covering, int pointCount)
    {
        public List<List<Point>> Groups { get; } = groups;
        public ulong[][] Covering { get; } = covering;
        public int PointCount { get; } = pointCount;
    }

    private static bool TryBuildAxis(ConditionCell[] cells, RawValue[]? domain, int words, out Axis axis, out string? why)
    {
        axis = null!;
        why = null;
        bool band = false;
        foreach (var c in cells) band |= c.Shape == CellShape.Band;
        var points = band ? BandPoints(cells, out why) : MatchPoints(cells, domain);
        if (points is null) return false;

        var groups = new List<List<Point>>();
        var covering = new List<ulong[]>();
        foreach (var point in points)
        {
            var set = new ulong[words];
            for (int r = 0; r < cells.Length; r++)
                if (Covers(in cells[r], point)) set[r >> 6] |= 1UL << (r & 63);
            int g = 0;
            while (g < covering.Count && !covering[g].AsSpan().SequenceEqual(set)) g++;
            if (g == covering.Count) { covering.Add(set); groups.Add([]); }
            groups[g].Add(point);
        }
        axis = new Axis(groups, [.. covering], points.Count);
        return true;
    }

    private static List<Point>? BandPoints(ConditionCell[] cells, out string? why)
    {
        why = null;
        var edges = new List<RawValue>();
        foreach (var c in cells)
        {
            if (c.Shape != CellShape.Band) { why = "mixes band and value cells"; return null; }
            if (c.HasFrom) edges.Add(c.From);
            if (c.HasTo) edges.Add(c.To);
        }
        try { edges.Sort(static (a, b) => a.CompareTo(b)); }
        catch (InvalidOperationException e) { why = e.Message; return null; }
        var distinct = new List<RawValue>(edges.Count);
        foreach (var e in edges) if (distinct.Count == 0 || distinct[^1].CompareTo(e) != 0) distinct.Add(e);

        // (−∞, e0), [e0, e1), …, [en, +∞)
        var points = new List<Point>(distinct.Count + 1);
        for (int k = 0; k <= distinct.Count; k++)
            points.Add(new Point(PointKind.Interval, default, k > 0, k > 0 ? distinct[k - 1] : default, k < distinct.Count, k < distinct.Count ? distinct[k] : default));
        return points;
    }

    private static List<Point> MatchPoints(ConditionCell[] cells, RawValue[]? domain)
    {
        var points = new List<Point>();
        if (domain is not null)
        {
            foreach (var v in domain) points.Add(new Point(PointKind.Value, v, false, default, false, default));
            return points;
        }
        // Open domain: every listed value + one value none of the cells lists.
        var seen = new List<RawValue>();
        foreach (var c in cells)
        {
            if (c.Shape == CellShape.Value || c.MatchKind is MatchKind.Is or MatchKind.Not) AddDistinct(seen, c.Value);
            foreach (ref readonly var v in c.Values) AddDistinct(seen, v);
        }
        foreach (var v in seen) points.Add(new Point(PointKind.Value, v, false, default, false, default));
        points.Add(new Point(PointKind.Other, default, false, default, false, default));
        return points;

        static void AddDistinct(List<RawValue> list, in RawValue v)
        {
            foreach (var x in list) if (x.Equals(v)) return;
            list.Add(v);
        }
    }

    private static bool Covers(in ConditionCell cell, in Point p)
    {
        if (cell.IsAny) return true;
        switch (p.Kind)
        {
            case PointKind.Interval:
                // Band edges are interval edges, so containment = low ≥ From and high ≤ To.
                bool low = !cell.HasFrom || (p.HasLow && p.Low.CompareTo(cell.From) >= 0);
                bool high = !cell.HasTo || (p.HasHigh && p.High.CompareTo(cell.To) <= 0);
                return low && high;
            case PointKind.Other:
                return cell.Shape == CellShape.Match && cell.MatchKind == MatchKind.Not;
            default:
                return cell.Matches(p.Value);
        }
    }

    private static string Describe(Axis[] axes, int[] index, string[] names)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < axes.Length; c++)
        {
            var group = axes[c].Groups[index[c]];
            if (group.Count == axes[c].PointCount) continue;                  // the column doesn't matter for this gap
            if (sb.Length > 0) sb.Append(", ");
            if (group[0].Kind == PointKind.Interval)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (i > 0) sb.Append(" or ");
                    AppendInterval(sb, names[c], group[i]);
                }
                continue;
            }
            sb.Append(names[c]);
            if (group.Count == 1) sb.Append(" = ").Append(ValueText(group[0]));
            else
            {
                sb.Append(" in {");
                for (int i = 0; i < group.Count; i++) sb.Append(i > 0 ? ", " : "").Append(ValueText(group[i]));
                sb.Append('}');
            }
        }
        return sb.Length == 0 ? "*" : sb.ToString();

        static string ValueText(in Point p) => p.Kind == PointKind.Other ? "(other)" : p.Value.ToString();

        static void AppendInterval(StringBuilder sb, string name, in Point p)
        {
            sb.Append(name);
            if (p.HasLow && p.HasHigh) sb.Append(" in [").Append(p.Low.ToString()).Append(", ").Append(p.High.ToString()).Append(')');
            else if (p.HasLow) sb.Append(" ≥ ").Append(p.Low.ToString());
            else sb.Append(" < ").Append(p.High.ToString());
        }
    }
}
