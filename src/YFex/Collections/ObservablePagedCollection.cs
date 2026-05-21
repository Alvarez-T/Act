using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YFex.Primitives.Collections;

public class ObservablePagedCollection<T> : ObservableCollection<T>
{
    private readonly Func<int, Task<PagedList<T>>> _fetchPage;

    private int _currentPage = 1;
    private int _totalPages = 1;
    private int _totalCount;
    private bool _isLoading;
    private bool _hasError;
    private string? _errorMessage;

    public int CurrentPage
    {
        get => _currentPage;
        private set => SetField(ref _currentPage, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        private set => SetField(ref _totalPages, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetField(ref _totalCount, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetField(ref _hasError, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    // Computed
    public bool HasNextPage => CurrentPage < TotalPages && !IsLoading;
    public bool HasPreviousPage => CurrentPage > 1 && !IsLoading;
    public bool IsEmpty => !IsLoading && Count == 0;
    public string PageSummary => $"{CurrentPage} / {TotalPages} ({TotalCount} items)";

    // -----------------------------------------------
    // Constructor
    // -----------------------------------------------
    public ObservablePagedCollection(Func<int, Task<PagedList<T>>> fetchPage)
    {
        _fetchPage = fetchPage;
    }

    // -----------------------------------------------
    // Navigation
    // -----------------------------------------------
    public Task LoadAsync() => GoToPageAsync(1);
    public Task NextPageAsync() => GoToPageAsync(CurrentPage + 1);
    public Task PrevPageAsync() => GoToPageAsync(CurrentPage - 1);
    public Task FirstPageAsync() => GoToPageAsync(1);
    public Task LastPageAsync() => GoToPageAsync(TotalPages);
    public Task RefreshAsync() => GoToPageAsync(CurrentPage);

    public async Task GoToPageAsync(int page)
    {
        if (IsLoading) return;
        if (page < 1 || (TotalPages > 0 && page > TotalPages)) return;

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        NotifyComputedProperties();

        try
        {
            var result = await _fetchPage(page);

            // Replace items in the ObservableCollection
            ClearItems();
            foreach (var item in result.Items)
                Add(item);

            CurrentPage = result.CurrentPage;
            TotalPages = result.TotalPages;
            TotalCount = result.TotalCount;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyComputedProperties();
        }
    }

    // -----------------------------------------------
    // Helpers
    // -----------------------------------------------
    private void NotifyComputedProperties()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasNextPage)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasPreviousPage)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsEmpty)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(PageSummary)));
    }

    private void SetField<TField>(ref TField field, TField value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TField>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(new PropertyChangedEventArgs(name));
    }
}
