using System.Collections;

namespace YFex.Primitives.Collections;

/// <summary>
/// A page of a list seen without its row type, so a control (a data grid, a pager) can read the counts and the rows of any
/// <see cref="PagedList{T}"/>.
/// </summary>
public interface IPagedList : IEnumerable
{
    /// <summary>The rows of this page.</summary>
    IList Rows { get; }

    /// <summary>1-based number of this page.</summary>
    int CurrentPage { get; }

    int PageSize { get; }

    /// <summary>Rows in the whole list (every page).</summary>
    int TotalCount { get; }

    int TotalPages { get; }

    bool HasNextPage { get; }
}

/// <summary>One page of a list, with the counts of the whole list.</summary>
public class PagedList<T> : IEnumerable<T>, IAsyncEnumerable<T>, IPagedList
{
    public List<T> Items { get; }
    public int CurrentPage { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool HasPreviousPage => CurrentPage > 1;

    IList IPagedList.Rows => Items;

    private PagedList(List<T> items, int totalCount, int currentPage, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        CurrentPage = currentPage;
        PageSize = pageSize;
    }

    //// -------------------------
    //// EF Core
    //// -------------------------
    //public static async Task<PagedList<T>> CreateAsync(
    //    IQueryable<T> query,
    //    int page,
    //    int pageSize,
    //    CancellationToken ct = default)
    //{
    //    var total = await query.CountAsync(ct);
    //    var items = await query
    //        .Skip((page - 1) * pageSize)
    //        .Take(pageSize)
    //        .ToListAsync(ct);

    //    return new PagedList<T>(items, total, page, pageSize);
    //}

    // -------------------------
    // Dapper (or any async source)
    // -------------------------
    public static async Task<PagedList<T>> CreateAsync(
        Func<Task<int>> countQuery,
        Func<Task<IEnumerable<T>>> itemsQuery,
        int page,
        int pageSize)
    {
        var countTask = countQuery();
        var itemsTask = itemsQuery();

        await Task.WhenAll(countTask, itemsTask);

        return new PagedList<T>(
            (await itemsTask).ToList(),
            await countTask,
            page,
            pageSize);
    }

    /// <summary>
    /// A page whose rows were already fetched (a query handler, a server reply): <paramref name="items"/> are the rows of
    /// page <paramref name="page"/> (1-based) and <paramref name="totalCount"/> counts every page.
    /// </summary>
    public static PagedList<T> From(IEnumerable<T> items, int totalCount, int page, int pageSize)
        => new(items as List<T> ?? [.. items], totalCount, page, pageSize);

    // -------------------------
    // In-memory / plain list
    // -------------------------
    public static PagedList<T> Create(
        IEnumerable<T> source,
        int page,
        int pageSize)
    {
        var list = source.ToList();
        var total = list.Count;
        var items = list
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedList<T>(items, total, page, pageSize);
    }

    // -------------------------
    // IEnumerable<T>
    // -------------------------
    public IEnumerator<T> GetEnumerator()
        => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    // -------------------------
    // IAsyncEnumerable<T>
    // -------------------------
    public async IAsyncEnumerator<T> GetAsyncEnumerator(
        CancellationToken ct = default)
    {
        foreach (var item in Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask; // satisfies async requirement
    }
}