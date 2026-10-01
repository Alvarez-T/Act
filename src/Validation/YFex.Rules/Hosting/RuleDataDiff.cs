using System.Collections.Immutable;
using System.Text.Json;

namespace YFex.Rules;

/// <summary>What happened to one table row between two versions (flags: a row can be changed AND moved).</summary>
[Flags]
public enum RowChangeKind : byte
{
    None = 0,
    Added = 1,
    Removed = 2,
    /// <summary>Cells or row metadata (priority, sealed, from, to, note) differ.</summary>
    Changed = 4,
    /// <summary>Relative order changed (matters for <see cref="HitPolicy.First"/>) — minimal set: rows outside the longest kept order.</summary>
    Moved = 8,
}

/// <summary>
/// One changed value: a table cell (<paramref name="Name"/> = row member), a row metadata attribute
/// (<paramref name="IsRowMeta"/>: <c>priority</c>, <c>sealed</c>, <c>from</c>, <c>to</c>, <c>note</c>), a parameter, or an
/// override attribute (<c>action</c>, <c>code</c>). <paramref name="Old"/>/<paramref name="New"/> are the canonical JSON texts
/// (<see langword="null"/> = absent) — display them as-is or parse with the column's codec.
/// </summary>
public readonly record struct ValueChange(string Name, string? Old, string? New, bool IsRowMeta = false);

/// <summary>One row of a table diff. Indices are positions in the old/new documents (−1 = absent on that side).</summary>
public sealed record RowChange(Guid RowId, RowChangeKind Kind, int OldIndex, int NewIndex, ImmutableArray<ValueChange> Values);

/// <summary>
/// Difference between two published payloads of the same stream (05 §4 audit "diff vs N", editor "compare with published").
/// Works on the canonical JSON documents of <see cref="RuleDataCodec"/> / <c>RowsJson</c>, so it needs no typed row
/// knowledge: rows are matched by id, cells compared by their JSON text.
/// </summary>
public sealed class RuleDataDiff
{
    private RuleDataDiff(RuleDataKind kind, long fromVersion, long toVersion, ImmutableArray<RowChange> rows, ImmutableArray<ValueChange> values)
    {
        Kind = kind;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Rows = rows;
        Values = values;
    }

    public RuleDataKind Kind { get; }
    /// <summary>Version compared against (0 = nothing published before: the code rows for tables, nothing otherwise).</summary>
    public long FromVersion { get; }
    public long ToVersion { get; }
    /// <summary>Tables: changed rows in new-document order, removed rows last.</summary>
    public ImmutableArray<RowChange> Rows { get; }
    /// <summary>Parameters / overrides: changed values by name.</summary>
    public ImmutableArray<ValueChange> Values { get; }
    public bool IsEmpty => Rows.IsEmpty && Values.IsEmpty;

    /// <summary>
    /// Compares <paramref name="newPayload"/> with <paramref name="oldPayload"/> (empty = nothing before). Payloads should be
    /// canonical (<see cref="IRuleSetHost.Canonicalize"/>) so that formatting never shows up as a change.
    /// </summary>
    public static RuleDataDiff Compute(RuleDataKind kind, ReadOnlySpan<byte> oldPayload, ReadOnlySpan<byte> newPayload, long fromVersion, long toVersion)
        => kind == RuleDataKind.Table
            ? new(kind, fromVersion, toVersion, DiffRows(oldPayload, newPayload), [])
            : new(kind, fromVersion, toVersion, [], DiffObjects(Props(oldPayload), Props(newPayload), isRowMeta: false));

    // ── Tables ──────────────────────────────────────────────────────────────

    private readonly record struct RowDoc(Guid Id, Dictionary<string, string> Cells, Dictionary<string, string> Meta);

    private static ImmutableArray<RowChange> DiffRows(ReadOnlySpan<byte> oldJson, ReadOnlySpan<byte> newJson)
    {
        var oldRows = ReadRows(oldJson);
        var newRows = ReadRows(newJson);
        var oldIndex = new Dictionary<Guid, int>(oldRows.Count);
        for (int i = 0; i < oldRows.Count; i++) oldIndex.TryAdd(oldRows[i].Id, i);

        // Old indices of the kept rows, in new order → rows outside the longest increasing run moved.
        var keptOld = new List<int>();
        var keptNew = new List<int>();
        for (int j = 0; j < newRows.Count; j++)
            if (oldIndex.TryGetValue(newRows[j].Id, out int i)) { keptOld.Add(i); keptNew.Add(j); }
        var stay = LongestIncreasing(keptOld);
        var moved = new HashSet<int>();
        for (int k = 0; k < keptNew.Count; k++) if (!stay.Contains(k)) moved.Add(keptNew[k]);

        var b = ImmutableArray.CreateBuilder<RowChange>();
        var seen = new HashSet<Guid>();
        for (int j = 0; j < newRows.Count; j++)
        {
            var row = newRows[j];
            seen.Add(row.Id);
            if (!oldIndex.TryGetValue(row.Id, out int i))
            {
                b.Add(new RowChange(row.Id, RowChangeKind.Added, -1, j, AllValues(row, added: true)));
                continue;
            }
            var old = oldRows[i];
            var values = DiffObjects(old.Cells, row.Cells, isRowMeta: false).AddRange(DiffObjects(old.Meta, row.Meta, isRowMeta: true));
            var kind = (values.IsEmpty ? RowChangeKind.None : RowChangeKind.Changed) | (moved.Contains(j) ? RowChangeKind.Moved : RowChangeKind.None);
            if (kind != RowChangeKind.None) b.Add(new RowChange(row.Id, kind, i, j, values));
        }
        for (int i = 0; i < oldRows.Count; i++)
            if (!seen.Contains(oldRows[i].Id)) b.Add(new RowChange(oldRows[i].Id, RowChangeKind.Removed, i, -1, AllValues(oldRows[i], added: false)));
        return b.ToImmutable();
    }

    private static ImmutableArray<ValueChange> AllValues(RowDoc row, bool added)
    {
        var b = ImmutableArray.CreateBuilder<ValueChange>(row.Cells.Count + row.Meta.Count);
        foreach (var (name, json) in Sorted(row.Cells)) b.Add(added ? new(name, null, json) : new(name, json, null));
        foreach (var (name, json) in Sorted(row.Meta)) b.Add(added ? new(name, null, json, true) : new(name, json, null, true));
        return b.MoveToImmutable();
    }

    private static List<RowDoc> ReadRows(ReadOnlySpan<byte> json)
    {
        var rows = new List<RowDoc>();
        if (json.IsEmpty) return rows;
        using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array) return rows;
        int n = 0;
        foreach (var row in array.EnumerateArray())
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            Guid id = Guid.Empty;
            foreach (var p in row.EnumerateObject())
            {
                if (p.NameEquals("id") && p.Value.TryGetGuid(out var g)) id = g;
                else if (p.NameEquals("cells") && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var c in p.Value.EnumerateObject()) cells[c.Name] = c.Value.GetRawText();
                else meta[p.Name] = p.Value.GetRawText();
            }
            // Rows without an id (non-canonical payloads) get a positional id so they still compare.
            rows.Add(new RowDoc(id == Guid.Empty ? RowMeta.CodeRowId("#positional", n) : id, cells, meta));
            n++;
        }
        return rows;
    }

    // ── Flat objects (parameters, overrides, row cells/meta) ───────────────

    private static Dictionary<string, string> Props(ReadOnlySpan<byte> json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json.IsEmpty) return d;
        using var doc = JsonDocument.Parse(json.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.GetRawText();
        return d;
    }

    private static ImmutableArray<ValueChange> DiffObjects(Dictionary<string, string> old, Dictionary<string, string> @new, bool isRowMeta)
    {
        var b = ImmutableArray.CreateBuilder<ValueChange>();
        foreach (var (name, json) in Sorted(@new))
        {
            if (!old.TryGetValue(name, out var before)) b.Add(new(name, null, json, isRowMeta));
            else if (!string.Equals(before, json, StringComparison.Ordinal)) b.Add(new(name, before, json, isRowMeta));
        }
        foreach (var (name, json) in Sorted(old))
            if (!@new.ContainsKey(name)) b.Add(new(name, json, null, isRowMeta));
        return b.ToImmutable();
    }

    private static List<KeyValuePair<string, string>> Sorted(Dictionary<string, string> d)
    {
        var list = new List<KeyValuePair<string, string>>(d);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    /// <summary>Positions (in <paramref name="seq"/>) of one longest strictly increasing subsequence — O(n log n).</summary>
    private static HashSet<int> LongestIncreasing(List<int> seq)
    {
        var tailIdx = new List<int>();            // position of the smallest tail of each run length
        var prev = new int[seq.Count];
        for (int i = 0; i < seq.Count; i++)
        {
            int lo = 0, hi = tailIdx.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (seq[tailIdx[mid]] < seq[i]) lo = mid + 1; else hi = mid;
            }
            prev[i] = lo > 0 ? tailIdx[lo - 1] : -1;
            if (lo == tailIdx.Count) tailIdx.Add(i); else tailIdx[lo] = i;
        }
        var result = new HashSet<int>();
        for (int k = tailIdx.Count > 0 ? tailIdx[^1] : -1; k >= 0; k = prev[k]) result.Add(k);
        return result;
    }
}
