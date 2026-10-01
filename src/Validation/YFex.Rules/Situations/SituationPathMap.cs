using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// Renames situation paths to message paths (04 §14) so rule violations land on the field the caller sent: the rule
/// says <c>"Total"</c> / <c>"Lines[2].Qty"</c>, the command calls them <c>"Amount"</c> / <c>"Items[2].Quantity"</c>.
/// Immutable; build once in the configuration:
/// <code>.MapSituationPaths(m => m.Path("Total", "Amount").Path("Lines[]", "Items[]").Path("Lines[].Qty", "Items[].Quantity"))</code>
/// A rename applies to the path itself and to everything below it (<c>"Lines[]"</c> also renames <c>"Lines[].Price"</c>);
/// the most specific (longest) match wins. Indices are kept, so both sides must have the same number of <c>[]</c>.
/// Paths with no match are returned unchanged (name situation fields like the message fields and nothing is needed).
/// </summary>
public sealed class SituationPathMap
{
    private readonly (string From, string To)[] _renames;

    private SituationPathMap((string From, string To)[] renames) => _renames = renames;

    public static SituationPathMap Empty { get; } = new([]);

    public bool IsEmpty => _renames.Length == 0;

    /// <summary>Adds a rename (returns a new map).</summary>
    public SituationPathMap Path(string situationPath, string messagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(situationPath);
        ArgumentException.ThrowIfNullOrEmpty(messagePath);
        if (Holes(situationPath) != Holes(messagePath))
            throw new ArgumentException($"'{situationPath}' → '{messagePath}': both sides need the same number of '[]' (indices are kept).", nameof(messagePath));
        foreach (var (from, _) in _renames)
            if (from == situationPath) throw new ArgumentException($"'{situationPath}' is mapped twice.", nameof(situationPath));

        var next = new (string, string)[_renames.Length + 1];
        _renames.CopyTo(next, 0);
        next[^1] = (situationPath, messagePath);
        // Longest first → the most specific rename wins.
        Array.Sort(next, static (a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return new(next);
    }

    /// <summary>The message path of <paramref name="path"/>.</summary>
    public FieldPath Map(in FieldPath path)
    {
        if (path.Template is not { } template) return path;
        foreach (var (from, to) in _renames)
        {
            if (!template.StartsWith(from, StringComparison.Ordinal)) continue;
            if (template.Length == from.Length) return FieldPath.Of(to, path.Indices);
            if (template[from.Length] is '.' or '[') return FieldPath.Of(string.Concat(to, template.AsSpan(from.Length)), path.Indices);
        }
        return path;
    }

    /// <summary>Maps every violation's path (the same list when nothing changes).</summary>
    public ViolationList Map(ViolationList violations)
    {
        if (IsEmpty || violations.Count == 0) return violations;
        Violation[]? mapped = null;
        var span = violations.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            var path = Map(span[i].Path);
            if (path == span[i].Path) continue;
            mapped ??= span.ToArray();
            mapped[i] = span[i].WithPath(path);
        }
        return mapped is null ? violations : ViolationList.FromOwnedArray(mapped);
    }

    private static int Holes(string template)
    {
        int n = 0;
        for (int i = template.IndexOf("[]", StringComparison.Ordinal); i >= 0; i = template.IndexOf("[]", i + 2, StringComparison.Ordinal)) n++;
        return n;
    }
}
