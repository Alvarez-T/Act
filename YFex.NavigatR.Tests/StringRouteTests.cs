using YFex.NavigatR;
using Xunit;

namespace YFex.NavigatR.Tests;

public sealed class StringRouteTests : IDisposable
{
    public void Dispose() => NavigatorLocator.Clear();

    private static (Navigator nav, FakeNavigation navPane, RouteRegistry registry)
        Create(Action<RouteRegistry>? configure = null)
    {
        var (nav, navPane, registry) = NavigatorFactory.Create();
        configure?.Invoke(registry);
        return (nav, navPane, registry);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Static patterns — no parameter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StaticStringRoute_NavigatesCorrectly()
    {
        var (navigator, _, _) = Create(r => r.Register("/home", typeof(HomeViewModel)));

        var result = await navigator.NavigateTo("/home");

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.Single(navigator.Breadcrumb);
    }

    [Fact]
    public async Task NavigateTo_StaticStringRoute_ResolvesCorrectViewModel()
    {
        var (navigator, navPane, _) = Create(r => r.Register("/home", typeof(HomeViewModel)));

        await navigator.NavigateTo("/home");

        Assert.IsType<HomeViewModel>(navPane.LastView);
    }

    [Fact]
    public void NavigateTo_UnknownStringRoute_Throws()
    {
        var (navigator, _, _) = Create();

        Assert.Throws<InvalidOperationException>(
            () => navigator.NavigateTo("/unknown"));
    }

    [Fact]
    public async Task NavigateTo_StaticStringRoute_CaseInsensitive()
    {
        var (navigator, _, _) = Create(r => r.Register("/Home", typeof(HomeViewModel)));

        var result = await navigator.NavigateTo("/home");

        Assert.IsType<NavigationSuccess>(result.Value);
    }

    [Fact]
    public async Task NavigateTo_StringRoute_AddsToBreadcrumb()
    {
        var (navigator, _, _) = Create(r => r.Register("/home", typeof(HomeViewModel)));

        await navigator.NavigateTo("/home");

        Assert.Single(navigator.Breadcrumb);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parameterized patterns — int segment
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StringRouteWithIntParam_ParsesSegmentToRouteParam()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/orders/{id}", typeof(OrderViewModel)));

        await navigator.NavigateTo("/orders/42");

        var vm = (OrderViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(42, vm.ReceivedParams?.OrderId);
    }

    [Fact]
    public async Task NavigateTo_StringRouteWithIntParam_DifferentValues()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/orders/{id}", typeof(OrderViewModel)));

        await navigator.NavigateTo("/orders/99");

        var vm = (OrderViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(99, vm.ReceivedParams?.OrderId);
    }

    [Fact]
    public void NavigateTo_StringRouteWithInvalidIntParam_Throws()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/orders/{id}", typeof(OrderViewModel)));

        Assert.Throws<InvalidOperationException>(
            () => navigator.NavigateTo("/orders/abc"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fixed object parameter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StringRouteWithFixedParam_UsesFixedObject()
    {
        var fixedParams = new OrderParams(OrderId: 999);
        var (navigator, _, _) = Create(r =>
            r.Register("/orders/special", typeof(OrderViewModel), fixedParams));

        await navigator.NavigateTo("/orders/special");

        var vm = (OrderViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(999, vm.ReceivedParams?.OrderId);
    }

    [Fact]
    public async Task NavigateTo_StringRouteWithFixedParam_IgnoresUrlSegment()
    {
        var fixedParams = new OrderParams(OrderId: 777);
        var (navigator, _, _) = Create(r =>
            r.Register("/orders/vip", typeof(OrderViewModel), fixedParams));

        await navigator.NavigateTo("/orders/vip");

        var vm = (OrderViewModel)navigator.Breadcrumb[0].NavigableInstance!;
        Assert.Equal(777, vm.ReceivedParams?.OrderId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No typed route — AnonymousRoute fallback
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StringRouteWithNoTypedRoute_UsesAnonymousRoute()
    {
        var (navigator, navPane, _) = Create(r =>
            r.Register("/dashboard", typeof(HomeViewModel)));

        var result = await navigator.NavigateTo("/dashboard");

        Assert.IsType<NavigationSuccess>(result.Value);
        Assert.IsType<HomeViewModel>(navPane.LastView);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UntilReturns on string route
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StringRoute_UntilReturns_CompletesOnBackNavigation()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/settings", typeof(SettingsViewModel)));

        await navigator.NavigateTo(new HomeRoute());
        var closedTask = navigator.NavigateTo("/settings").UntilReturns();

        await navigator.NavigateBackward();

        var result = await closedTask;
        Assert.IsType<NavigationSuccess>(result.Value);
    }

    [Fact]
    public async Task NavigateTo_StringRoute_UntilReturns_PinsCallerWhileWaiting()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/settings", typeof(SettingsViewModel)));

        await navigator.NavigateTo(new HomeRoute());
        var homeEntry = navigator.Breadcrumb[0];

        var closedTask = navigator.NavigateTo("/settings").UntilReturns();

        Assert.Equal(NavigationEntryState.Pinned, homeEntry.State);

        await navigator.NavigateBackward();
        await closedTask;

        Assert.Equal(NavigationEntryState.Active, homeEntry.State);
    }

    [Fact]
    public async Task NavigateTo_StringRoute_UntilReturnsTyped_ReturnsResult()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/picker", typeof(PickerViewModel)));

        await navigator.NavigateTo(new HomeRoute());

        var resultTask = navigator.NavigateTo("/picker").UntilReturns<PickerResult>();

        var vm = (PickerViewModel)navigator.Breadcrumb.Last().NavigableInstance!;
        vm.Returns(new PickerResult("FromString"));

        var result = await resultTask;
        var success = Assert.IsType<NavigationSuccess<PickerResult>>(result.Value);
        Assert.Equal("FromString", success.Value.SelectedItem);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multiple patterns — order matters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_MultiplePatterns_MatchesMoreSpecificFirst()
    {
        var (navigator, navPane, _) = Create(r =>
        {
            r.Register("/orders/special", typeof(HomeViewModel));
            r.Register("/orders/{id}", typeof(OrderViewModel));
        });

        await navigator.NavigateTo("/orders/special");

        Assert.IsType<HomeViewModel>(navPane.LastView);
    }

    [Fact]
    public async Task NavigateTo_MultiplePatterns_FallsBackToParameterized()
    {
        var (navigator, navPane, _) = Create(r =>
        {
            r.Register("/orders/special", typeof(HomeViewModel));
            r.Register("/orders/{id}", typeof(OrderViewModel));
        });

        await navigator.NavigateTo("/orders/42");

        Assert.IsType<OrderViewModel>(navPane.LastView);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // String route + denial
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NavigateTo_StringRouteToDenyingViewModel_ReturnsDenied()
    {
        var (navigator, _, _) = Create(r =>
            r.Register("/admin", typeof(DenyingViewModel)));

        var result = await navigator.NavigateTo("/admin");

        var denied = Assert.IsType<NavigationDenied>(result.Value);
        Assert.Equal("Access denied", denied.Reason);
    }

    [Fact]
    public async Task NavigateTo_StringRouteDenied_DoesNotAddToHistory()
    {
        var (navigator, _, _) = Create(r =>
        {
            r.Register("/admin", typeof(DenyingViewModel));
        });

        await navigator.NavigateTo(new HomeRoute());
        await navigator.NavigateTo("/admin");

        Assert.Single(navigator.Breadcrumb);
    }

    [Fact]
    public async Task NavigateTo_StringRouteDenied_NotifiesNavPane()
    {
        var (navigator, navPane, _) = Create(r =>
            r.Register("/admin", typeof(DenyingViewModel)));

        await navigator.NavigateTo("/admin");

        Assert.Equal(1, navPane.DeniedCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RouteRegistry string route resolution
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_Resolve_ReturnsNullForUnknownPattern()
    {
        var (_, _, registry) = Create();

        var entry = registry.Resolve("/unknown");

        Assert.Null(entry);
    }

    [Fact]
    public void Registry_Resolve_ReturnsEntryForKnownPattern()
    {
        var (_, _, registry) = Create(r =>
            r.Register("/home", typeof(HomeViewModel)));

        var entry = registry.Resolve("/home");

        Assert.NotNull(entry);
        Assert.Equal(typeof(HomeViewModel), entry!.ViewModelType);
    }

    [Fact]
    public void Registry_Resolve_ExtractsRawSegmentAsString()
    {
        var (_, _, registry) = Create(r =>
            r.Register("/product/{id}", typeof(ProductViewModel)));

        var entry = registry.Resolve("/product/123");

        Assert.NotNull(entry);
        Assert.Equal("123", entry!.RawParameter);
    }

    [Fact]
    public void Registry_Resolve_NullParameterForStaticRoute()
    {
        var (_, _, registry) = Create(r =>
            r.Register("/home", typeof(HomeViewModel)));

        var entry = registry.Resolve("/home");

        Assert.NotNull(entry);
        Assert.Null(entry!.RawParameter);
    }

    [Fact]
    public void Registry_Resolve_FixedObjectParameter()
    {
        var fixedParam = new OrderParams(OrderId: 42);
        var (_, _, registry) = Create(r =>
            r.Register("/special", typeof(OrderViewModel), fixedParam));

        var entry = registry.Resolve("/special");

        Assert.NotNull(entry);
        Assert.Same(fixedParam, entry!.RawParameter);
    }
}
