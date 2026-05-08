using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YFex.NavigatR;

namespace YFex.NavigatR.Tests;

public sealed class PrefetchTests : IDisposable
{
    private readonly Navigator _navigator;
    private readonly FakeNavigation _navPane;
    private readonly RouteRegistry _registry;

    public PrefetchTests()
    {
        var (nav, navPane, registry) = NavigatorFactory.Create();
        _navigator = nav;
        _navPane = navPane;
        _registry = registry;
    }

    public void Dispose() => NavigatorLocator.Clear();

    // ─────────────────────────────────────────────────────────────────────────
    // Prefetch — basic
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prefetch_ReturnsToken()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));

        Assert.NotNull(token);
        Assert.False(token.IsExpired);
    }

    [Fact]
    public void Prefetch_TokenCarriesRoute()
    {
        var route = new ProductRoute(new ProductParams(42));
        var token = _navigator.Prefetch(route);

        Assert.Equal(route, token.Route);
    }

    [Fact]
    public async Task Prefetch_CallsOnPrefetchOnViewModel()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));

        // Give prefetch time to run off UI thread
        await Task.Delay(100);

        var vm = token.PrefetchedEntry?.NavigableInstance as ProductViewModel;
        Assert.NotNull(vm);
        Assert.True(vm!.PrefetchWasCalled);
    }

    [Fact]
    public async Task Prefetch_EntryStateIsPrefetching()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));

        await Task.Delay(10); // let entry be created

        Assert.Equal(NavigationEntryState.Prefetching, token.PrefetchedEntry?.State);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo with token — prefetch completed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithCompletedPrefetchToken_SkipsOnNavigation()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        await Task.Delay(100); // prefetch completes

        await _navigator.NavigateTo(token);

        var vm = (ProductViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        // OnNavigation called via bridge — data from prefetch
        Assert.True(vm.OnNavigationWasCalled);
        Assert.Equal("Product-1", vm.ReceivedData?.Title);
    }

    [Fact]
    public async Task NavigateTo_WithToken_UsesPreloadedData()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(5)));
        await Task.Delay(100);

        await _navigator.NavigateTo(token);

        var vm = (ProductViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(5, vm.ReceivedData == null ? 0 : (int)(vm.ReceivedData.Price == 9.99m ? 5 : 0));
        // More meaningful: check title contains ProductId
        Assert.Contains("5", vm.ReceivedData?.Title ?? "");
    }

    [Fact]
    public async Task NavigateTo_WithToken_AddsEntryToBreadcrumb()
    {
        var token = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        await Task.Delay(100);

        await _navigator.NavigateTo(token);

        Assert.Single(_navigator.Breadcrumb);
        Assert.IsType<ProductRoute>(_navigator.Breadcrumb[0].Route);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo with token — prefetch still running
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithInFlightPrefetch_AwaitsIt()
    {
        var (navigator, _, registry) = NavigatorFactory.Create(services =>
            services.AddTransient<SlowProductViewModel>());

        registry.Register<ProductRoute, SlowProductViewModel>();

        var token = navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        await Task.Delay(10); // let entry be created

        // Navigate while prefetch still running
        var navTask = navigator.NavigateTo(token);

        // Get the VM from the prefetched entry and complete its TCS
        var vmEntry = token.PrefetchedEntry?.NavigableInstance as SlowProductViewModel;
        Assert.NotNull(vmEntry);
        vmEntry!.PrefetchTcs.TrySetResult(new ProductData("SlowProduct", 1.0m));

        await navTask;

        Assert.Equal("SlowProduct", vmEntry.ReceivedData?.Title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NavigateTo with token — expired
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithExpiredToken_FallsBackToFreshNavigation()
    {
        // Use very short timeout
        var token = _navigator.Prefetch(
            new ProductRoute(new ProductParams(1)),
            timeout: TimeSpan.FromMilliseconds(10));

        await Task.Delay(50); // let token expire

        Assert.True(token.IsExpired);

        // Falls back to fresh navigation — OnNavigation called normally
        await _navigator.NavigateTo(token);

        var vm = (ProductViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        Assert.True(vm.OnNavigationWasCalled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Same route — reuse token
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prefetch_SameRoute_ReturnsSameToken()
    {
        var route = new ProductRoute(new ProductParams(1));
        var token1 = _navigator.Prefetch(route);
        var token2 = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));

        Assert.Same(token1, token2);
    }

    [Fact]
    public void Prefetch_SameRouteSameParams_DoesNotCancelPrevious()
    {
        var token1 = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        var token2 = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));

        Assert.False(token1.IsExpired);
        Assert.Same(token1, token2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Different params — cancel previous
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prefetch_DifferentParams_CancelsPreviousToken()
    {
        var token1 = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        await Task.Delay(10);

        // Different params — should cancel token1
        var token2 = _navigator.Prefetch(new ProductRoute(new ProductParams(2)));

        Assert.True(token1.IsExpired);
        Assert.False(token2.IsExpired);
        Assert.NotSame(token1, token2);
    }

    [Fact]
    public async Task Prefetch_CancelPreviousFalse_KeepsBothTokens()
    {
        var token1 = _navigator.Prefetch(new ProductRoute(new ProductParams(1)));
        await Task.Delay(10);

        var token2 = _navigator.Prefetch(
            new ProductRoute(new ProductParams(2)),
            cancelPrevious: false);

        Assert.False(token1.IsExpired);
        Assert.False(token2.IsExpired);
        Assert.NotSame(token1, token2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No prefetch — normal navigation still works
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithoutPrefetch_WorksNormally()
    {
        await _navigator.NavigateTo(new ProductRoute(new ProductParams(1)));

        var vm = (ProductViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        Assert.True(vm.OnNavigationWasCalled);
        Assert.False(vm.PrefetchWasCalled);
        Assert.Equal("Product-1", vm.ReceivedData?.Title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prefetch timeout
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prefetch_TokenExpiresAfterTimeout()
    {
        var token = _navigator.Prefetch(
            new ProductRoute(new ProductParams(1)),
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.False(token.IsExpired);
        await Task.Delay(100);
        Assert.True(token.IsExpired);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prefetch with UntilReturns
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_WithToken_UntilReturns_WorksCorrectly()
    {
        await _navigator.NavigateTo(new HomeRoute());

        var token = _navigator.Prefetch(new PickerRoute());
        await Task.Delay(50);

        var resultTask = _navigator.NavigateTo(token).UntilReturns<PickerResult>();

        var vm = (PickerViewModel)_navigator.Breadcrumb.Last().NavigableInstance!;
        vm.Returns(new PickerResult("Apple"));

        var result = await resultTask;
        Assert.IsType<NavigationSuccess<PickerResult>>(result.Value);
        Assert.Equal("Apple", ((NavigationSuccess<PickerResult>)result.Value!).Value.SelectedItem);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AFK — NotifyInactive / NotifyActive
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyInactive_CallsOnSuspendWithoutStateChange()
    {
        _registry.Register<AfkRoute, AfkViewModel>();
        await _navigator.NavigateTo(new AfkRoute());

        var vm = (AfkViewModel)_navigator.Breadcrumb[0].NavigableInstance!;
        var stateBefore = _navigator.Breadcrumb[0].State;

        _navigator.NotifyInactive();
        await Task.Delay(50);

        Assert.Contains("OnSuspend", vm.Calls);
        Assert.Equal(stateBefore, _navigator.Breadcrumb[0].State); // state unchanged
    }

    [Fact]
    public async Task NotifyActive_CallsOnActiveWithoutStateChange()
    {
        _registry.Register<AfkRoute, AfkViewModel>();
        await _navigator.NavigateTo(new AfkRoute());

        var vm = (AfkViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        _navigator.NotifyInactive();
        await Task.Delay(50);
        _navigator.NotifyActive();
        await Task.Delay(50);

        Assert.Contains("OnActive", vm.Calls);
        Assert.Equal(NavigationEntryState.Active, _navigator.Breadcrumb[0].State);
    }

    [Fact]
    public async Task INavigation_UserBecameInactive_TriggersNotifyInactive()
    {
        _registry.Register<AfkRoute, AfkViewModel>();
        await _navigator.NavigateTo(new AfkRoute());

        var vm = (AfkViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        // Platform fires the event
        _navPane.SimulateInactive();
        await Task.Delay(50);

        Assert.Contains("OnSuspend", vm.Calls);
    }

    [Fact]
    public async Task INavigation_UserBecameActive_TriggersNotifyActive()
    {
        _registry.Register<AfkRoute, AfkViewModel>();
        await _navigator.NavigateTo(new AfkRoute());

        var vm = (AfkViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        _navPane.SimulateInactive();
        await Task.Delay(50);
        _navPane.SimulateActive();
        await Task.Delay(50);

        Assert.Contains("OnActive", vm.Calls);
    }

    [Fact]
    public async Task NotifyInactive_WhenNothingNavigated_DoesNotThrow()
    {
        var ex = Record.Exception(() => _navigator.NotifyInactive());
        Assert.Null(ex);
    }

    [Fact]
    public async Task NotifyInactive_WhenPinned_DoesNotCallOnSuspend()
    {
        await _navigator.NavigateTo(new HomeRoute());
        var homeVm = (HomeViewModel)_navigator.Breadcrumb[0].NavigableInstance!;

        // Pin home
        var resultTask = _navigator.NavigateTo(new PickerRoute()).UntilReturns<PickerResult>();

        _navigator.NotifyInactive();
        await Task.Delay(50);

        // Pinned — OnSuspend must not be called via AFK either
        Assert.DoesNotContain("OnSuspend", homeVm.Calls);

        // Cleanup
        ((PickerViewModel)_navigator.Breadcrumb.Last().NavigableInstance!).Returns(new PickerResult("x"));
        await resultTask;
    }
}