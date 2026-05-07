using YFex.NavigatR;
using Xunit;

namespace YFex.NavigatR.Tests;

public sealed class NavigatorNavigationTests : IDisposable
{
    private readonly Navigator _navigator;
    private readonly FakeNavigation _navPane;

    public NavigatorNavigationTests()
    {
        var (nav, navPane, _) = NavigatorFactory.Create();
        _navigator = nav;
        _navPane = navPane;
    }

    public void Dispose() => NavigatorLocator.Clear();

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo — no parameter, no result
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_Simple_ReturnsSuccess()
    {
        var result = await _navigator.NavigateTo(new HomeRoute());

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.Single(_navigator.Breadcrumb);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
    }

    [Fact]
    public async Task NavigateTo_Simple_PerformsNavigation()
    {
        await _navigator.NavigateTo(new HomeRoute());

        Assert.Single(_navPane.NavigatedViews);
        Assert.IsType<HomeViewModel>(_navPane.LastView);
    }

    [Fact]
    public async Task NavigateTo_Simple_DirectionIsInitialOnFirstNav()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var vm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        Assert.Contains("OnNavigation:Initial", vm.Calls);
    }

    [Fact]
    public async Task NavigateTo_Simple_DirectionIsForwardOnSubsequentNav()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());

        var vm = (HomeViewModel)_navigator.Breadcrumb[1].NavigableInstance!;
        Assert.Contains("OnNavigation:Forward", vm.Calls);
    }

    [Fact]
    public async Task NavigateTo_Simple_SuspendsCallerAfterOnNavigationConfirms()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        await _navigator.NavigateTo(new SearchRoute());

        Assert.Contains("OnSuspend", homeVm.Calls);
    }

    [Fact]
    public async Task NavigateTo_WhenDenied_CallerNeverReceivesOnSuspend()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        await _navigator.NavigateTo(new AdminRoute());

        Assert.DoesNotContain("OnSuspend", homeVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo — route carries parameter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithParameter_DeliverParameterToViewModel()
    {
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(OrderId: 42)));

        var vm = (OrderViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(42, vm.ReceivedParams?.OrderId);
    }

    [Fact]
    public async Task NavigateTo_WithParameter_RouteOnEntryCarriesParameter()
    {
        var route = new OrderRoute(new OrderParams(OrderId: 99));
        await _navigator.NavigateTo(route);

        var entry = (NavigationEntry<IRoute>)_navigator.Breadcrumb[0];
        Assert.Equal(99, ((OrderRoute)entry.Route).Params.OrderId);
    }

    [Fact]
    public async Task NavigateTo_WithParameter_ReturnsSuccess()
    {
        var result = await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        Assert.IsType<NavigationSuccess>(result.Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UntilReturns<TResult>
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UntilReturns_ReturnsSuccessWhenViewModelCallsReturns()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("Apple"));

        var result = await resultTask;

        Assert.IsType<NavigationSuccess<PickerResult>>(result.Value);
        Assert.Equal("Apple", ((NavigationSuccess<PickerResult>)result.Value!).Value.SelectedItem);
    }

    [Fact]
    public async Task UntilReturns_ReturnsCancelledWhenViewModelCallsCancel()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Cancel();

        var result = await resultTask;
        Assert.IsType<NavigationCancelled>(result.Value);
    }

    [Fact]
    public async Task UntilReturns_ReturnsDeniedWhenViewModelCallsDeny()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Deny("Not allowed");

        var result = await resultTask;
        var denied = Assert.IsType<NavigationDenied>(result.Value);
        Assert.Equal("Not allowed", denied.Reason);
    }

    [Fact]
    public async Task UntilReturns_NavigatesBackAfterCompletion()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("Apple"));
        await resultTask;

        Assert.Single(_navigator.Breadcrumb);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
    }

    [Fact]
    public async Task UntilReturns_CancelledViaCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var resultTask = _navigator.NavigateTo(new PickerRoute(), cts.Token).UntilReturns<PickerResult>();

        cts.Cancel();

        var result = await resultTask;
        Assert.IsType<NavigationCancelled>(result.Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UntilReturns() — no typed result
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UntilReturnsNoResult_CompletesWhenUserNavigatesBack()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var closedTask = _navigator.NavigateTo(new SettingsRoute()).UntilReturns();

        await _navigator.NavigateBackward();

        var result = await closedTask;
        Assert.IsType<NavigationSuccess>(result.Value);
    }

    [Fact]
    public async Task UntilReturnsNoResult_CallerIsPinnedWhileWaiting()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        var closedTask = _navigator.NavigateTo(new SettingsRoute()).UntilReturns();

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        await _navigator.NavigateBackward();
        await closedTask;

        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parameter + result
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UntilReturns_WithParameterOnRoute_DeliversParameterAndReturnsResult()
    {
        var route = new CheckoutRoute(new CheckoutParams(CartId: 5));
        var resultTask = _navigator.NavigateTo(route).UntilReturns<CheckoutResult>();

        var vm = GetCurrentVm<CheckoutViewModel>();
        Assert.Equal(5, vm.ReceivedParams?.CartId);

        vm.Returns(new CheckoutResult("TXN-001"));
        var result = await resultTask;

        var success = Assert.IsType<NavigationSuccess<CheckoutResult>>(result.Value);
        Assert.Equal("TXN-001", success.Value.TransactionId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Denied from OnNavigation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WhenDenied_ReturnsNavigationDenied()
    {
        var result = await _navigator.NavigateTo(new AdminRoute());

        var denied = Assert.IsType<NavigationDenied>(result.Value);
        Assert.Equal("Access denied", denied.Reason);
    }

    [Fact]
    public async Task NavigateTo_WhenDenied_DoesNotAddToHistory()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new AdminRoute());

        Assert.Single(_navigator.Breadcrumb);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
    }

    [Fact]
    public async Task NavigateTo_WhenDenied_NotifiesNavPane()
    {
        await _navigator.NavigateTo(new AdminRoute());

        Assert.Equal(1, _navPane.DeniedCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pinned state
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UntilReturns_CallerIsPinnedAfterOnNavigationConfirms()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    [Fact]
    public async Task UntilReturns_PinnedCallerDoesNotReceiveOnSuspend()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        Assert.DoesNotContain("OnSuspend", homeVm.Calls);
    }

    [Fact]
    public async Task UntilReturns_PinnedCallerDoesNotReceiveOnResume()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        Assert.DoesNotContain("OnResume", homeVm.Calls);
    }

    [Fact]
    public async Task UntilReturns_WhenDenied_CallerNeverPinned()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        var result = await _navigator.NavigateTo(new AdminRoute()).UntilReturns();

        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
        Assert.IsType<NavigationDenied>(result.Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Backward / Forward
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateBackward_MovesToPreviousEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());

        await _navigator.NavigateBackward();

        Assert.IsType<HomeRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateForward_MovesToNextEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateBackward();

        await _navigator.NavigateForward();

        Assert.IsType<SearchRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateBackwardTo_MovesToMostRecentMatchingRoute()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        await _navigator.NavigateBackwardTo<HomeRoute>();

        Assert.IsType<HomeRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateForwardTo_MovesToNextMatchingRoute()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        await _navigator.NavigateBackwardTo<HomeRoute>();

        await _navigator.NavigateForwardTo<OrderRoute>();

        Assert.IsType<OrderRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateToIndex_JumpsToSpecificEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        await _navigator.NavigateToIndex(0);

        Assert.IsType<HomeRoute>(_navigator.Breadcrumb.Last().Route);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Breadcrumb
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Breadcrumb_ReflectsNavigationHistory()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        Assert.Equal(3, _navigator.Breadcrumb.Count);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
        Assert.IsType<SearchRoute>(_navigator.Breadcrumb[1].Route);
        Assert.IsType<OrderRoute>(_navigator.Breadcrumb[2].Route);
    }

    [Fact]
    public async Task Breadcrumb_RouteCarriesParameter()
    {
        await _navigator.NavigateTo(new OrderRoute(new OrderParams(42)));

        var route = (OrderRoute)_navigator.Breadcrumb[0].Route;
        Assert.Equal(42, route.Params.OrderId);
    }

    [Fact]
    public async Task Breadcrumb_DisplayNameAccessible()
    {
        await _navigator.NavigateTo(new HomeRoute());

        Assert.Equal("Home", _navigator.Breadcrumb[0].Route.DisplayName);
    }

    [Fact]
    public async Task Breadcrumb_NavigatedAtIsSet()
    {
        var before = DateTimeOffset.UtcNow;
        await _navigator.NavigateTo(new HomeRoute());
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(_navigator.Breadcrumb[0].NavigatedAt, before, after);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // History policy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneForwardOnBranch_RemovesForwardHistoryOnNewNavigation()
    {
        _navigator.HistoryPolicy = NavigationHistoryPolicy.PruneForwardOnBranch;

        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateBackward();

        await _navigator.NavigateTo(new OrderRoute(new OrderParams(1)));

        Assert.Equal(2, _navigator.Breadcrumb.Count);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
        Assert.IsType<OrderRoute>(_navigator.Breadcrumb[1].Route);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ClearHistory
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearHistory_RemovesAllEntries()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());

        _navigator.ClearHistory();

        Assert.Empty(_navigator.Breadcrumb);
    }

    [Fact]
    public async Task ClearHistory_DisposesNonPinnedViewModels()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        _navigator.ClearHistory();

        Assert.Contains("Dispose", homeVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OnResume / OnSuspend semantics
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateBack_CallsOnResumeOnPreviousEntry_WhenStillAlive()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        _navigator.HistoryPolicy = NavigationHistoryPolicy.PreserveForwardOnBranch;
        await _navigator.NavigateTo(new SearchRoute());

        await _navigator.NavigateBackward();

        Assert.Contains("OnResume", homeVm.Calls);
    }

    [Fact]
    public async Task NavigateBack_CallsOnNavigationAgain_WhenInstanceWasEvicted()
    {
        var (navigator, _, _) = NavigatorFactory.Create(poolCapacity: 1);

        await navigator.NavigateTo(new SearchRoute()); // fills pool slot 1
        await navigator.NavigateTo(new OrderRoute(new OrderParams(1))); // evicts Search

        await navigator.NavigateBackward(); // Search reconstructed — new instance

        // Capture AFTER navigate back — this is the new reconstructed instance
        var searchVm = (HomeViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Contains("OnNavigation:Backward", searchVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chained awaits
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChainedAwaits_CallerPinnedAfterOnNavigationConfirms()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("Apple"));
        var result = await resultTask;

        Assert.IsType<NavigationSuccess<PickerResult>>(result.Value);
        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigatorLocator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NavigatorLocator_ReturnsSetNavigator()
        => Assert.Same(_navigator, NavigatorLocator.GetNavigator());

    [Fact]
    public void NavigatorLocator_ThrowsWhenNotSet()
    {
        NavigatorLocator.Clear();
        Assert.Throws<InvalidOperationException>(() => NavigatorLocator.GetNavigator());
    }

    [Fact]
    public async Task NavigatorLocator_AsyncLocalIsolation()
    {
        var (navA, _, _) = NavigatorFactory.Create();
        var (navB, _, _) = NavigatorFactory.Create();

        Navigator? capturedA = null;
        Navigator? capturedB = null;

        await Task.WhenAll(
            Task.Run(() => { NavigatorLocator.Set(navA); capturedA = NavigatorLocator.GetNavigator(); }),
            Task.Run(() => { NavigatorLocator.Set(navB); capturedB = NavigatorLocator.GetNavigator(); }));

        Assert.Same(navA, capturedA);
        Assert.Same(navB, capturedB);
        Assert.NotSame(capturedA, capturedB);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_ClearsHistory()
    {
        await _navigator.NavigateTo(new HomeRoute());
        _navigator.Dispose();

        Assert.Empty(_navigator.Breadcrumb);
    }

    private T GetCurrentVm<T>() where T : class
        => (T)_navigator.Breadcrumb.Last().NavigableInstance!;
}