using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YFex.Primitives.Collections;

/// <summary>
/// A list that loads its rows a page at a time, seen without its row type, so a list control (a data grid, a pager) can
/// drive it: ask for the next page or another page, change the sort or search, retry after a failure (data-grid plan
/// ⚑DG62). <see cref="ObservablePagedCollection{T}"/> is the implementation.
/// </summary>
public interface IPagedCollection : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    /// <summary>The row type, known before any row is loaded (a grid builds its columns from it).</summary>
    Type ItemType { get; }

    /// <summary>Rows per request. Changing it reloads from the page that holds the first row shown.</summary>
    int PageSize { get; set; }

    /// <summary>The last page loaded (1-based); 0 before the first load.</summary>
    int CurrentPage { get; }

    /// <summary>Rows in the whole list on the server, from the last answer.</summary>
    int TotalCount { get; }

    int TotalPages { get; }

    bool HasNextPage { get; }

    bool HasPreviousPage { get; }

    /// <summary>True once a page has arrived.</summary>
    bool HasLoaded { get; }

    /// <summary>A request is running.</summary>
    bool IsLoading { get; }

    /// <summary>The running request appends a page to the rows already shown (load on demand).</summary>
    bool IsLoadingMore { get; }

    /// <summary>Why the last request failed, or null. <see cref="RetryAsync"/> repeats it.</summary>
    Exception? Error { get; }

    /// <summary>Sort and search every request carries.</summary>
    ViewSpec View { get; }

    /// <summary>Shows page <paramref name="page"/> alone (paging). A request still running is cancelled.</summary>
    Task GoToPageAsync(int page);

    /// <summary>Appends the next page (load on demand); the first call loads page 1. Calls while one runs share it.</summary>
    Task LoadMoreAsync();

    /// <summary>New sort or search: the rows start again at page 1, the running request is cancelled. Before the first
    /// load it only stores the view.</summary>
    Task SetViewAsync(ViewSpec view);

    /// <summary>The data changed elsewhere: loads again (page 1 when appending, the same page when paging).</summary>
    Task ReloadAsync();

    /// <summary>Repeats the request that failed.</summary>
    Task RetryAsync();
}

/// <summary>
/// An observable list of <typeparamref name="T"/> filled a page at a time by the view model's load method. A data grid
/// with <c>LoadMode="Incremental"</c> appends pages while the user scrolls; with <c>LoadMode="Paged"</c> it shows one page
/// and a pager. Every request carries the current <see cref="ViewSpec"/> (sort, search); a request that a newer one
/// replaced is cancelled and its answer never shows; a failure keeps the rows and sets <see cref="Error"/>.
/// </summary>
/// <remarks>
/// Use it on the UI thread: the load method's continuation changes the list. Pages arrive as one change each (a reset
/// for a new page, one add for an appended page), not one event per row.
/// </remarks>
/// <example><code>
/// public ObservablePagedCollection&lt;CustomerRow&gt; Customers => field ??= new((request, ct) =>
///     queries.Send(new Customer.List(request.View, request.Page, request.PageSize), ct), pageSize: 50);
///
/// &lt;YDataGrid ItemsSource="{Binding Customers}" LoadMode="Paged" /&gt;
///
/// // after a change made elsewhere
/// await Customers.ReloadAsync();
/// </code></example>
public class ObservablePagedCollection<T> : ObservableCollection<T>, IPagedCollection
{
    private readonly Func<PageRequest, CancellationToken, Task<PagedList<T>>> _load;
    private CancellationTokenSource? _pending;
    private Task? _loadingMore;
    private Func<Task>? _retry;
    private bool _appends;

    private int _pageSize;
    private int _currentPage;
    private int _totalPages;
    private int _totalCount;
    private bool _hasLoaded;
    private bool _isLoading;
    private bool _isLoadingMore;
    private Exception? _error;
    private ViewSpec _view;

    /// <param name="load">Loads one page: the request's page, size, sort and search; cancel when the token fires.</param>
    /// <param name="pageSize">Rows per request.</param>
    /// <param name="view">Initial sort and search; <see cref="ViewSpec.Default"/> when null.</param>
    public ObservablePagedCollection(Func<PageRequest, CancellationToken, Task<PagedList<T>>> load, int pageSize = 50,
        ViewSpec? view = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _load = load;
        _pageSize = pageSize;
        _view = view ?? ViewSpec.Default;
    }

    public Type ItemType => typeof(T);

    public int PageSize
    {
        get => _pageSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            if (value == _pageSize)
                return;

            var firstRow = Math.Max(0, (_currentPage - 1) * _pageSize);
            SetField(ref _pageSize, value);
            if (_hasLoaded)
                _ = _appends ? Run(1, append: false) : Run(firstRow / value + 1, append: false);
        }
    }

    public int CurrentPage { get => _currentPage; private set => SetField(ref _currentPage, value); }

    public int TotalPages { get => _totalPages; private set => SetField(ref _totalPages, value); }

    public int TotalCount { get => _totalCount; private set => SetField(ref _totalCount, value); }

    public bool HasLoaded { get => _hasLoaded; private set => SetField(ref _hasLoaded, value); }

    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

    public bool IsLoadingMore { get => _isLoadingMore; private set => SetField(ref _isLoadingMore, value); }

    public Exception? Error { get => _error; private set => SetField(ref _error, value); }

    public ViewSpec View { get => _view; private set => SetField(ref _view, value); }

    public bool HasNextPage => _hasLoaded && _currentPage < _totalPages;

    public bool HasPreviousPage => _currentPage > 1;

    /// <summary>Loads page 1 (paging).</summary>
    public Task LoadAsync() => GoToPageAsync(1);

    public Task NextPageAsync() => GoToPageAsync(_currentPage + 1);

    public Task PreviousPageAsync() => GoToPageAsync(_currentPage - 1);

    public Task FirstPageAsync() => GoToPageAsync(1);

    public Task LastPageAsync() => GoToPageAsync(_totalPages);

    public Task GoToPageAsync(int page)
    {
        _appends = false;
        var last = _hasLoaded ? Math.Max(1, _totalPages) : int.MaxValue;
        return Run(Math.Clamp(page, 1, last), append: false);
    }

    public Task LoadMoreAsync()
    {
        if (_loadingMore is { IsCompleted: false })
            return _loadingMore;

        if (_hasLoaded && _appends && !HasNextPage)
            return Task.CompletedTask;

        var next = _hasLoaded && _appends ? _currentPage + 1 : 1;
        _appends = true;
        return _loadingMore = Run(next, append: next > 1);
    }

    public Task SetViewAsync(ViewSpec view)
    {
        ArgumentNullException.ThrowIfNull(view);
        View = view;
        return _hasLoaded || _isLoading ? Run(1, append: false) : Task.CompletedTask;
    }

    public Task ReloadAsync() => _appends ? Run(1, append: false) : Run(Math.Max(1, _currentPage), append: false);

    public Task RetryAsync() => _retry?.Invoke() ?? Task.CompletedTask;

    private async Task Run(int page, bool append)
    {
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        _retry = () => Run(page, append);
        Error = null;
        IsLoading = true;
        IsLoadingMore = append;

        try
        {
            var result = await _load(new PageRequest(page, _pageSize, _view), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (append)
                Append(result.Items);
            else
                Replace(result.Items);

            TotalCount = result.TotalCount;
            TotalPages = _pageSize > 0 ? (int)Math.Ceiling(result.TotalCount / (double)_pageSize) : 0;
            CurrentPage = result.CurrentPage;
            HasLoaded = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer request replaced this one.
        }
        catch (Exception error) when (!cts.IsCancellationRequested)
        {
            Error = error;
        }
        finally
        {
            if (ReferenceEquals(_pending, cts))
            {
                _pending = null;
                IsLoading = false;
                IsLoadingMore = false;
            }
            cts.Dispose();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasNextPage)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasPreviousPage)));
        }
    }

    /// <summary>One reset for a whole page.</summary>
    private void Replace(List<T> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>One add for a whole page.</summary>
    private void Append(List<T> rows)
    {
        if (rows.Count == 0)
            return;

        CheckReentrancy();
        var start = Items.Count;
        foreach (var row in rows)
            Items.Add(row);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, rows, start));
    }

    private void SetField<TField>(ref TField field, TField value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TField>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(new PropertyChangedEventArgs(name));
    }
}
