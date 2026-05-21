using System.Collections;

namespace YFex.Primitives.Collections;

public class PagedList<T> : IEnumerable<T>, IAsyncEnumerable<T>
{
    public List<T> Items { get; }
    public int CurrentPage { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool HasPreviousPage => CurrentPage > 1;

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