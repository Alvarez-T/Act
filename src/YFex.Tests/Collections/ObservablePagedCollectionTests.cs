using System.Collections.Specialized;
using FluentAssertions;
using YFex.Primitives.Collections;
using Xunit;

namespace YFex.Tests.Collections;

/// <summary>Data-grid plan ⚑DG62: a list that loads pages for a grid (paging, load on demand, view changes, cancel, retry).</summary>
public class ObservablePagedCollectionTests
{
    /// <summary>A fake server of 0..count-1 whose answers the test releases one by one.</summary>
    private sealed class Server(int count)
    {
        public List<(PageRequest Request, TaskCompletionSource<PagedList<int>> Answer, CancellationToken Token)> Calls { get; } = [];

        public Task<PagedList<int>> Load(PageRequest request, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<PagedList<int>>();
            Calls.Add((request, answer, ct));
            return answer.Task;
        }

        public void Answer(int call)
        {
            var (request, answer, _) = Calls[call];
            var rows = Enumerable.Range(0, count).ToList();
            if (request.View.Sort is [{ Direction: SortDirection.Descending }, ..])
                rows.Reverse();
            answer.SetResult(PagedList<int>.From(rows.Skip(request.Skip).Take(request.PageSize), count, request.Page, request.PageSize));
        }

        public void Fail(int call) => Calls[call].Answer.SetException(new InvalidOperationException("server down"));
    }

    [Fact]
    public async Task Paging_replaces_the_rows_with_one_reset()
    {
        var server = new Server(120);
        var rows = new ObservablePagedCollection<int>(server.Load, pageSize: 50);
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        var first = rows.LoadAsync();
        rows.IsLoading.Should().BeTrue();
        server.Answer(0);
        await first;

        rows.Should().HaveCount(50).And.StartWith(0);
        rows.TotalCount.Should().Be(120);
        rows.TotalPages.Should().Be(3);
        rows.HasNextPage.Should().BeTrue();
        rows.IsLoading.Should().BeFalse();

        var last = rows.LastPageAsync();
        server.Calls[1].Request.Page.Should().Be(3);
        server.Calls[1].Request.Skip.Should().Be(100);
        server.Answer(1);
        await last;

        rows.Should().HaveCount(20).And.StartWith(100);
        rows.HasNextPage.Should().BeFalse();
        rows.HasPreviousPage.Should().BeTrue();
        events.Should().Equal(NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task Load_more_appends_pages_and_stops_at_the_end()
    {
        var server = new Server(70);
        var rows = new ObservablePagedCollection<int>(server.Load, pageSize: 50);
        var adds = new List<int>();
        rows.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) adds.Add(e.NewItems!.Count); };

        var first = rows.LoadMoreAsync();
        rows.IsLoadingMore.Should().BeFalse("the first page replaces");
        server.Answer(0);
        await first;

        var second = rows.LoadMoreAsync();
        rows.IsLoadingMore.Should().BeTrue();
        rows.LoadMoreAsync().Should().BeSameAs(second, "calls while one runs share it");
        server.Answer(1);
        await second;

        rows.Should().HaveCount(70).And.EndWith(69);
        adds.Should().Equal(20);
        rows.HasNextPage.Should().BeFalse();

        await rows.LoadMoreAsync();
        server.Calls.Should().HaveCount(2, "nothing is left to load");
    }

    [Fact]
    public async Task A_new_view_cancels_the_running_request_and_its_answer_never_shows()
    {
        var server = new Server(100);
        var rows = new ObservablePagedCollection<int>(server.Load, pageSize: 10);
        var first = rows.LoadAsync();
        server.Answer(0);
        await first;

        var stale = rows.NextPageAsync();
        var fresh = rows.SetViewAsync(new ViewSpec { Sort = [new SortSpec("Value", SortDirection.Descending)] });
        server.Calls[1].Token.IsCancellationRequested.Should().BeTrue();

        server.Answer(2);
        await fresh;
        server.Answer(1);
        await stale;

        rows.Should().StartWith(99);
        rows.CurrentPage.Should().Be(1);
        rows.IsLoading.Should().BeFalse();
        server.Calls[2].Request.View.Sort.Should().ContainSingle().Which.Path.Should().Be("Value");
    }

    [Fact]
    public async Task A_failure_keeps_the_rows_and_retry_repeats_the_request()
    {
        var server = new Server(100);
        var rows = new ObservablePagedCollection<int>(server.Load, pageSize: 10);
        var first = rows.LoadMoreAsync();
        server.Answer(0);
        await first;

        var more = rows.LoadMoreAsync();
        server.Fail(1);
        await more;

        rows.Error.Should().BeOfType<InvalidOperationException>();
        rows.Should().HaveCount(10);
        rows.IsLoading.Should().BeFalse();

        var retry = rows.RetryAsync();
        rows.Error.Should().BeNull();
        server.Calls[2].Request.Page.Should().Be(2);
        server.Answer(2);
        await retry;
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Changing_the_page_size_keeps_the_first_row_in_view()
    {
        var server = new Server(100);
        var rows = new ObservablePagedCollection<int>(server.Load, pageSize: 10);
        var page = rows.GoToPageAsync(4);
        server.Answer(0);
        await page;
        rows.Should().StartWith(30);

        rows.PageSize = 25;
        server.Calls[1].Request.Should().Be(new PageRequest(2, 25, ViewSpec.Default));
        server.Answer(1);
        await Task.Yield();
        rows.Should().StartWith(25).And.HaveCount(25);
    }

    [Fact]
    public async Task A_view_set_before_the_first_load_goes_with_it()
    {
        var server = new Server(5);
        var rows = new ObservablePagedCollection<int>(server.Load);
        await rows.SetViewAsync(new ViewSpec { Search = "x" });
        server.Calls.Should().BeEmpty();

        _ = rows.LoadAsync();
        server.Calls[0].Request.View.Search.Should().Be("x");
        rows.ItemType.Should().Be(typeof(int));
    }
}
