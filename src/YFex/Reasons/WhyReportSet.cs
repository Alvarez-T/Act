using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace YFex.Reasons;

/// <summary>
/// Several "why" answers sent together, one per field (validation/14 ⚑RX30): the reasons a server attaches to a rejection, so a
/// thin client (or a rich client the server overruled) explains the rejected fields the way a local decision would. Each
/// report names its field in <see cref="WhyReport.Field"/>; a report without a field answers for the whole decision.
/// <see langword="default"/> is empty and allocates nothing.
/// </summary>
/// <example><code>
/// if (problem.Reasons.TryGet(FieldPath.Of("ManualDiscount"), out var why)) dialog.ShowReasons(why);
/// </code></example>
public readonly struct WhyReportSet : IEquatable<WhyReportSet>
{
    private readonly ImmutableArray<WhyReport> _reports;

    private WhyReportSet(ImmutableArray<WhyReport> reports) => _reports = reports;

    /// <summary>No reports.</summary>
    public static WhyReportSet Empty => default;

    /// <summary>The reports, in the producer's order; empty for <see langword="default"/>.</summary>
    public ImmutableArray<WhyReport> Reports => _reports.IsDefault ? [] : _reports;

    /// <summary>True when there is no report.</summary>
    public bool IsEmpty => _reports.IsDefaultOrEmpty;

    /// <summary>The number of reports.</summary>
    public int Count => _reports.IsDefault ? 0 : _reports.Length;

    /// <summary>A set of <paramref name="reports"/>; an empty array gives <see cref="Empty"/>.</summary>
    public static WhyReportSet From(ImmutableArray<WhyReport> reports)
    {
        if (reports.IsDefaultOrEmpty) return default;
        foreach (var r in reports) ArgumentNullException.ThrowIfNull(r, nameof(reports));
        return new(reports);
    }

    /// <summary>A set of <paramref name="reports"/>.</summary>
    public static WhyReportSet From(params ReadOnlySpan<WhyReport> reports) => From(ImmutableArray.Create(reports));

    /// <summary>
    /// The report for <paramref name="path"/> (compared with its formatted form, <c>"Lines[1].Quantity"</c>);
    /// <see cref="FieldPath.Empty"/> finds the whole-decision report. False when the set has none.
    /// </summary>
    public bool TryGet(in FieldPath path, [MaybeNullWhen(false)] out WhyReport report)
    {
        if (!_reports.IsDefault)
        {
            foreach (var r in _reports)
            {
                bool match = path.IsEmpty ? string.IsNullOrEmpty(r.Field) : r.Field is { Length: > 0 } f && path.FormattedEquals(f);
                if (!match) continue;
                report = r;
                return true;
            }
        }
        report = null;
        return false;
    }

    /// <summary>
    /// This set with every field path (each report's <see cref="WhyReport.Field"/> and its items' <see cref="WhyItem.Field"/>)
    /// passed through <paramref name="map"/>: situation → message paths on the server, contract → view-model paths on the client.
    /// A path that does not parse is kept as it is.
    /// </summary>
    public WhyReportSet MapFields<TState>(TState state, Func<TState, FieldPath, FieldPath> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (IsEmpty) return this;
        var mapped = new WhyReport[_reports.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            var r = _reports[i];
            var items = r.Items.IsDefaultOrEmpty ? r.Items : MapItems(r.Items, state, map);
            mapped[i] = r with { Field = MapOne(r.Field, state, map), Items = items };
        }
        return new(ImmutableArray.Create(mapped));
    }

    private static ImmutableArray<WhyItem> MapItems<TState>(ImmutableArray<WhyItem> items, TState state, Func<TState, FieldPath, FieldPath> map)
    {
        var mapped = new WhyItem[items.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            var item = items[i];
            mapped[i] = item.Field is null ? item : item with { Field = MapOne(item.Field, state, map) };
        }
        return ImmutableArray.Create(mapped);
    }

    private static string? MapOne<TState>(string? field, TState state, Func<TState, FieldPath, FieldPath> map)
    {
        if (string.IsNullOrEmpty(field) || !FieldPath.TryParse(field, out var path)) return field;
        var to = map(state, path);
        return to.IsEmpty ? field : to.ToString();
    }

    /// <summary>Same reports (by value), in the same order.</summary>
    public bool Equals(WhyReportSet other)
    {
        var a = Reports;
        var b = other.Reports;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is WhyReportSet other && Equals(other);

    public override int GetHashCode() => Count;

    public static bool operator ==(WhyReportSet left, WhyReportSet right) => left.Equals(right);
    public static bool operator !=(WhyReportSet left, WhyReportSet right) => !left.Equals(right);
}
