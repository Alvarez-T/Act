using Xunit;

namespace YFex.NavigatR.Tests;

public sealed class NavigablePoolTests : IDisposable
{
    public void Dispose() => NavigatorLocator.Clear();

    [Fact]
    public async Task Pool_WhenFull_EvictsLeastRecentlyUsed()
    {
        var (navigator, _, _) = NavigatorFactory.Create(poolCapacity: 2);

        // Home is IKeepAlive — exempt from pool capacity
        await navigator.NavigateTo(new HomeRoute());
        await navigator.NavigateTo(new SearchRoute()); // slot 1
        await navigator.NavigateTo(new OrderRoute(new OrderParams(1))); // slot 2 — pool full

        // Navigate again — Search (LRU) should be evicted
        await navigator.NavigateTo(new SettingsRoute());

        // Navigate back to Search — was evicted, OnNavigation called again
        await navigator.NavigateBackwardTo<SearchRoute>();

        var searchEntry = navigator.Breadcrumb.First(e => e.Route is SearchRoute);
        var vm = (HomeViewModel)searchEntry.NavigableInstance!;
        Assert.Contains("OnNavigation:Backward", vm.Calls);
    }

    [Fact]
    public async Task Pool_KeepAlive_NeverEvicted()
    {
        var (navigator, _, _) = NavigatorFactory.Create(poolCapacity: 1);

        await navigator.NavigateTo(new HomeRoute()); // IKeepAlive — exempt
        await navigator.NavigateTo(new SearchRoute()); // fills pool
        await navigator.NavigateTo(new OrderRoute(new OrderParams(1))); // evicts Search, not Home

        await navigator.NavigateBackwardTo<HomeRoute>();

        var homeVm = (HomeViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        // Home was never evicted — OnResume called, not OnNavigation
        Assert.Contains("OnResume", homeVm.Calls);
        Assert.DoesNotContain("OnNavigation:Backward", homeVm.Calls);
    }

    [Fact]
    public async Task Pool_PinnedEntries_NeverEvicted()
    {
        var (navigator, _, _) = NavigatorFactory.Create(poolCapacity: 1);

        await navigator.NavigateTo(new SearchRoute()); // fills pool
        var homeEntry = navigator.Breadcrumb[0];

        // Start UntilReturns — Search becomes Pinned after OnNavigation confirms
        var resultTask = navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();
        await Task.Yield();

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        // Complete
        var pickerVm = (PickerViewModel)navigator.Breadcrumb.Last().NavigableInstance!;
        pickerVm.Returns(new PickerResult("x"));
        await resultTask;
    }

    [Fact]
    public void Pool_InvalidCapacity_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NavigatorFactory.Create(poolCapacity: 0));
    }
}