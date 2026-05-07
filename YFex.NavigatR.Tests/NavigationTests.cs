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
    public async Task NavigateTo_Simple_CallsOnNavigation()
    {
        var result = await _navigator.NavigateTo(new HomeRoute());

        Assert.IsType<NavigationSuccess>(result);
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
        HomeViewModel? vm = null;
        await _navigator.NavigateTo(new HomeRoute());
        vm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

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
    public async Task NavigateTo_Simple_SuspendsCurrentBeforeNavigating()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        await _navigator.NavigateTo(new SearchRoute());

        Assert.Contains("OnSuspend", homeVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo — parameter only
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithParameter_DeliverParameterToViewModel()
    {
        var param = new OrderParams(OrderId: 42);
        await _navigator.NavigateTo(new OrderRoute(), param);

        var vm = (OrderViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(42, vm.ReceivedParams?.OrderId);
    }

    [Fact]
    public async Task NavigateTo_WithParameter_EntryStoresParameter()
    {
        var param = new OrderParams(OrderId: 99);
        await _navigator.NavigateTo(new OrderRoute(), param);

        var entry = (NavigationEntry<OrderRoute>)_navigator.Breadcrumb[0];
        Assert.Equal(param, entry.Parameter);
    }

    [Fact]
    public async Task NavigateTo_WithParameter_ReturnsSuccess()
    {
        var result = await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

        Assert.IsType<NavigationSuccess>(result);
    }

    [Fact]
    public async Task NavigateTo_WithResult_ReturnsSuccessWhenViewModelCallsReturns()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute());

        var vm = GetCurrentVm<PickerViewModel>();
        vm.Returns(new PickerResult("Apple"));

        var result = await resultTask.WithResult<PickerResult>();

        Assert.IsType<NavigationSuccess<PickerResult>>(result); 
        var success = (NavigationSuccess<PickerResult>)result.Value!;
        Assert.Equal("Apple", success.Value.SelectedItem);
    }

    [Fact]
    public async Task NavigateTo_WithResult_ReturnsCancelledWhenViewModelCallsCancel()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute());

        var vm = GetCurrentVm<PickerViewModel>();
        vm.Cancel();

        var result = await resultTask;
        Assert.IsType<NavigationCancelled>(result);
    }

    [Fact]
    public async Task NavigateTo_WithResult_ReturnsDeniedWhenViewModelCallsDeny()
    {
        var resultTask = _navigator.NavigateTo(new PickerRoute());

        var vm = GetCurrentVm<PickerViewModel>();
        vm.Deny("Not allowed");

        var result = await resultTask;

        Assert.IsType<NavigationDenied>(result);
        var denied = (NavigationDenied)result.Value!;
        Assert.Equal("Not allowed", denied.Reason);
    }

    [Fact]
    public async Task NavigateTo_WithResult_NavigatesBackAfterCompletion()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var resultTask = _navigator.NavigateTo(new PickerRoute());

        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("Apple"));
        await resultTask;

        // Should be back at Home
        Assert.Single(_navigator.Breadcrumb);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
    }

    [Fact]
    public async Task NavigateTo_WithResult_CancelledViaCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var resultTask = _navigator.NavigateTo(new PickerRoute(), cts.Token);

        cts.Cancel();

        var result = await resultTask;
        Assert.IsType<NavigationCancelled>(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo — parameter + result
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithParameterAndResult_DeliversParameterAndReturnsResult()
    {
        var param = new CheckoutParams(CartId: 5);
        NavigationTask<CheckoutRoute, CheckoutParams> resultTask = _navigator.NavigateTo(new CheckoutRoute(), param);

        var vm = GetCurrentVm<CheckoutViewModel>();
        Assert.Equal(5, vm.ReceivedParams?.CartId);

        vm.Returns(new CheckoutResult("TXN-001"));
        var result = await resultTask.WithResult<CheckoutResult>();

        Assert.IsType<NavigationSuccess<CheckoutResult>>(result);
        var success = (NavigationSuccess<CheckoutResult>)result.Value!;
        Assert.Equal("TXN-001", success.Value.TransactionId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo — Denied from OnNavigation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WhenDeniedFromOnNavigation_ReturnsNavigationDenied()
    {
        var result = await _navigator.NavigateTo(new AdminRoute());

        Assert.IsType<NavigationDenied>(result);
        var denied = (NavigationDenied)result.Value!;
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
    // Pinned state — mid-await behavior
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithResult_CallerIsPinnedWhileAwaiting()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        // Start awaiting but don't complete yet
        var resultTask = _navigator.NavigateTo(new PickerRoute());

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        // Complete
        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    [Fact]
    public async Task NavigateTo_WithResult_PinnedCallerDoesNotReceiveOnSuspend()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        var resultTask = _navigator.NavigateTo(new PickerRoute());
        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        // OnSuspend must NOT have been called on HomeViewModel while it was Pinned
        Assert.DoesNotContain("OnSuspend", homeVm.Calls);
    }

    [Fact]
    public async Task NavigateTo_WithResult_PinnedCallerDoesNotReceiveOnResume()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        var resultTask = _navigator.NavigateTo(new PickerRoute());
        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("x"));
        await resultTask;

        // OnResume must NOT have been called — caller was Pinned, never truly left
        Assert.DoesNotContain("OnResume", homeVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateBackward / NavigateForward
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateBackward_MovesToPreviousEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());

        _navigator.NavigateBackward();
        await Task.Delay(50); // allow async to settle

        Assert.IsType<HomeRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateForward_MovesToNextEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        _navigator.NavigateBackward();
        await Task.Delay(50);

        _navigator.NavigateForward();
        await Task.Delay(50);

        Assert.IsType<SearchRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateBackwardTo_MovesToMostRecentMatchingRoute()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

        _navigator.NavigateBackwardTo<HomeRoute>();
        await Task.Delay(50);

        Assert.IsType<HomeRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateForwardTo_MovesToNextMatchingRoute()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

        _navigator.NavigateBackwardTo<HomeRoute>();
        await Task.Delay(50);

        _navigator.NavigateForwardTo<OrderRoute>();
        await Task.Delay(50);

        Assert.IsType<OrderRoute>(_navigator.Breadcrumb.Last().Route);
    }

    [Fact]
    public async Task NavigateToIndex_JumpsToSpecificEntry()
    {
        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

        _navigator.NavigateToIndex(0);
        await Task.Delay(50);

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
        await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

        Assert.Equal(3, _navigator.Breadcrumb.Count);
        Assert.IsType<HomeRoute>(_navigator.Breadcrumb[0].Route);
        Assert.IsType<SearchRoute>(_navigator.Breadcrumb[1].Route);
        Assert.IsType<OrderRoute>(_navigator.Breadcrumb[2].Route);
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
    // History policy — PruneForwardOnBranch
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneForwardOnBranch_RemovesForwardHistoryOnNewNavigation()
    {
        _navigator.HistoryPolicy = NavigationHistoryPolicy.PruneForwardOnBranch;

        await _navigator.NavigateTo(new HomeRoute());
        await _navigator.NavigateTo(new SearchRoute());
        _navigator.NavigateBackward();
        await Task.Delay(50);

        // Navigate forward to a different route — should prune Search
        await _navigator.NavigateTo(new OrderRoute(), new OrderParams(1));

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

        // Navigate forward keeping home alive
        _navigator.HistoryPolicy = NavigationHistoryPolicy.PreserveForwardOnBranch;
        await _navigator.NavigateTo(new SearchRoute());

        // Navigate back
        _navigator.NavigateBackward();
        await Task.Delay(50);

        Assert.Contains("OnResume", homeVm.Calls);
    }

    [Fact]
    public async Task NavigateBack_CallsOnNavigationAgain_WhenInstanceWasEvicted()
    {
        // Pool capacity 1 — Home will be evicted when Search is navigated to
        var (navigator, _, _) = NavigatorFactory.Create(poolCapacity: 1);

        await navigator.NavigateTo(new HomeRoute());
        await navigator.NavigateTo(new SearchRoute()); // Home evicted from pool

        navigator.NavigateBackward();
        await Task.Delay(50);

        // Home was reconstructed — OnNavigation called again on new instance
        var homeEntry = navigator.Breadcrumb[0];
        var homeVm = (HomeViewModel)homeEntry.NavigableInstance!;
        Assert.Contains("OnNavigation:Backward", homeVm.Calls);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Chained awaits — Home → Order → Checkout
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChainedAwaits_AllCallersPinnedUntilEachResolves()
    {
        // Navigate to Home normally — caller suspended
        await _navigator.NavigateTo(new HomeRoute());
        var homeEntry = _navigator.Breadcrumb[0];

        // Home awaits Picker — pins Home
        var pickerResultTask = _navigator.NavigateTo(new PickerRoute())
            .UntilReturns<PickerResult>();

        // Home should be Pinned now — navigation confirmed, awaiting result
        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        // Picker resolves
        GetCurrentVm<PickerViewModel>().Returns(new PickerResult("Apple"));
        var result = await pickerResultTask;

        // Home unpinned after result delivered
        Assert.IsType<NavigationSuccess<PickerResult>>(result);
        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigatorLocator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NavigatorLocator_ReturnsSetNavigator()
    {
        Assert.Same(_navigator, NavigatorLocator.GetNavigator());
    }

    [Fact]
    public void NavigatorLocator_ThrowsWhenNotSet()
    {
        NavigatorLocator.Clear();
        Assert.Throws<InvalidOperationException>(() => NavigatorLocator.GetNavigator());
    }

    [Fact]
    public async Task NavigatorLocator_AsyncLocalIsolation_EachContextHasOwnNavigator()
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
    public async Task Dispose_ClearsHistoryAndDisposesScope()
    {
        await _navigator.NavigateTo(new HomeRoute());
        _navigator.Dispose();

        Assert.Empty(_navigator.Breadcrumb);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private T GetCurrentVm<T>() where T : class
        => (T)_navigator.Breadcrumb.Last().NavigableInstance!;
}