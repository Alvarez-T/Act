namespace YFex.Primitives.Collections;

/// <summary>Order of one sort key.</summary>
public enum SortDirection : byte
{
    /// <summary>Smallest first (A→Z, 0→9, oldest first).</summary>
    Ascending,

    /// <summary>Largest first.</summary>
    Descending,
}

/// <summary>
/// One sort key of a list: the field path of the row model (<c>"Customer.Name"</c>, <c>"Total"</c>) and its direction.
/// Neutral: a data grid sends it, a query handler or report reads it; neither knows the other.
/// </summary>
/// <example><code>
/// var byTotal = new SortSpec("Total", SortDirection.Descending);
/// </code></example>
public readonly record struct SortSpec(string Path, SortDirection Direction = SortDirection.Ascending);

/// <summary>How a <see cref="FilterSpec"/> compares a field with its value.</summary>
public enum FilterOperator : byte
{
    /// <summary>The field's text contains <see cref="FilterSpec.Value"/> (case and accents ignored is the reader's choice).</summary>
    Contains,

    /// <summary>The field equals <see cref="FilterSpec.Value"/>.</summary>
    Equals,

    /// <summary>The field's text starts with <see cref="FilterSpec.Value"/>.</summary>
    StartsWith,

    /// <summary>The field has no value (null or empty text).</summary>
    IsEmpty,

    /// <summary>The field has a value.</summary>
    IsNotEmpty,

    /// <summary>The field is one of <see cref="FilterSpec.Values"/>.</summary>
    OneOf,

    /// <summary>The field is different from <see cref="FilterSpec.Value"/>.</summary>
    NotEquals,

    /// <summary>The field is greater than <see cref="FilterSpec.Value"/>.</summary>
    GreaterThan,

    /// <summary>The field is greater than or equal to <see cref="FilterSpec.Value"/>.</summary>
    GreaterOrEqual,

    /// <summary>The field is less than <see cref="FilterSpec.Value"/>.</summary>
    LessThan,

    /// <summary>The field is less than or equal to <see cref="FilterSpec.Value"/>.</summary>
    LessOrEqual,

    /// <summary>The field is within <see cref="FilterSpec.Values"/>[0] … [1] (both included).</summary>
    Between,

    /// <summary><see cref="FilterSpec.Expression"/> holds a filter-dialect expression (YFex.Expressions); <c>Path</c> may
    /// be empty.</summary>
    Expression,
}

/// <summary>
/// One filter of a list: the field path of the row model, how it is compared and the value(s), as text in the invariant
/// culture (numbers with a dot, dates ISO 8601) so it travels to a server and into a saved view unchanged. Neutral: a data
/// grid's column filter sends it, a query handler reads it (⚑DG20, ⚑DG72).
/// </summary>
/// <example><code>
/// var south = new FilterSpec("Region", FilterOperator.Equals) { Value = "South" };
/// var heavy = new FilterSpec("Weight", FilterOperator.GreaterOrEqual) { Value = "5" };
/// var tiers = new FilterSpec("Tier", FilterOperator.OneOf) { Values = ["Gold", "Silver"] };
/// var view = new ViewSpec { Filters = [south, heavy, tiers] };
/// </code></example>
public sealed record FilterSpec(string Path, FilterOperator Operator)
{
    /// <summary>The value one-value operators compare with, or null.</summary>
    public string? Value { get; init; }

    /// <summary>The values of <see cref="FilterOperator.OneOf"/> (an empty field is <c>""</c>) and the two ends of
    /// <see cref="FilterOperator.Between"/>.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>A filter-dialect expression (<see cref="FilterOperator.Expression"/>), or null.</summary>
    public string? Expression { get; init; }
}

/// <summary>
/// What a user sees of a list right now, as data: sort keys (first is the main one), filters and the search text. A data
/// grid builds it (<c>YDataGrid.CurrentView</c>); a query, a saved view or a future report builder reads it. Grouping is
/// added by a later phase of the data-grid plan (⚑DG60) as a new property, so existing readers keep working.
/// </summary>
/// <example><code>
/// var view = new ViewSpec { Sort = [new("Date", SortDirection.Descending)], Search = "sao paulo" };
/// var page = await Movement.Queries.List(view, page: 1, pageSize: 100).Send(ct);
/// </code></example>
public sealed record ViewSpec
{
    /// <summary>The view with no sort and no search.</summary>
    public static ViewSpec Default { get; } = new();

    /// <summary>Sort keys in priority order; empty = the source's own order.</summary>
    public IReadOnlyList<SortSpec> Sort { get; init; } = [];

    /// <summary>Free text the rows must match, or null. Matching (accents, case, fuzzy) is the reader's choice.</summary>
    public string? Search { get; init; }

    /// <summary>Filters every row must pass (all of them); empty = no filter.</summary>
    public IReadOnlyList<FilterSpec> Filters { get; init; } = [];
}

/// <summary>
/// A request for one page of a list: the 1-based <paramref name="Page"/>, its <paramref name="PageSize"/> and the
/// <paramref name="View"/> (sort, search) the rows must follow. Answered with a <see cref="PagedList{T}"/>.
/// </summary>
/// <example><code>
/// // a query handler
/// var rows = await db.Movements.Where(view).OrderBy(request.View.Sort).Skip(request.Skip).Take(request.PageSize)…;
/// return PagedList&lt;MovementRow&gt;.From(rows, total, request.Page, request.PageSize);
/// </code></example>
public readonly record struct PageRequest(int Page, int PageSize, ViewSpec View)
{
    /// <summary>Rows before this page: <c>(Page - 1) * PageSize</c>.</summary>
    public int Skip => Math.Max(0, Page - 1) * PageSize;
}
