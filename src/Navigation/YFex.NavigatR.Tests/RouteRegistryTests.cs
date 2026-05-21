using YFex.NavigatR;
using Xunit;

namespace YFex.NavigatR.Tests;

public sealed class RouteRegistryTests
{
    private readonly RouteRegistry _registry = new();

    [Fact]
    public void Register_TypedRoute_ResolvesCorrectViewModel()
    {
        _registry.Register<HomeRoute, HomeViewModel>();

        var vmType = _registry.ResolveViewModel(new HomeRoute());

        Assert.Equal(typeof(HomeViewModel), vmType);
    }

    [Fact]
    public void Register_MultipleRoutes_EachResolvesCorrectly()
    {
        _registry.Register<HomeRoute, HomeViewModel>();
        _registry.Register<OrderRoute, OrderViewModel>();
        _registry.Register<PickerRoute, PickerViewModel>();

        Assert.Equal(typeof(HomeViewModel), _registry.ResolveViewModel(new HomeRoute()));
        Assert.Equal(typeof(OrderViewModel), _registry.ResolveViewModel(new OrderRoute(new OrderParams(1))));
        Assert.Equal(typeof(PickerViewModel), _registry.ResolveViewModel(new PickerRoute()));
    }

    [Fact]
    public void Register_SameRoute_OverwritesPreviousRegistration()
    {
        _registry.Register<HomeRoute, HomeViewModel>();
        _registry.Register<HomeRoute, OrderViewModel>();

        Assert.Equal(typeof(OrderViewModel), _registry.ResolveViewModel(new HomeRoute()));
    }

    [Fact]
    public void ResolveViewModel_UnregisteredRoute_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => _registry.ResolveViewModel(new HomeRoute()));

        Assert.Contains("HomeRoute", ex.Message);
        Assert.Contains("RegisterAll", ex.Message);
    }

    [Fact]
    public void ResolveRouteType_ReturnsRegisteredRouteType()
    {
        _registry.Register<OrderRoute, OrderViewModel>();

        var routeType = _registry.ResolveRouteType(typeof(OrderViewModel));

        Assert.Equal(typeof(OrderRoute), routeType);
    }

    [Fact]
    public void ResolveRouteType_UnregisteredViewModel_ReturnsNull()
    {
        var routeType = _registry.ResolveRouteType(typeof(HomeViewModel));

        Assert.Null(routeType);
    }

    // ─── String pattern routing ───────────────────────────────────────────────

    [Fact]
    public void Register_StringPattern_ResolvesMatchingRoute()
    {
        _registry.Register("/home", typeof(HomeViewModel));

        var entry = _registry.Resolve("/home");

        Assert.NotNull(entry);
        Assert.Equal(typeof(HomeViewModel), entry!.ViewModelType);
    }

    [Fact]
    public void Resolve_StringPattern_ReturnsNullForNoMatch()
    {
        _registry.Register("/home", typeof(HomeViewModel));

        var entry = _registry.Resolve("/orders");

        Assert.Null(entry);
    }

    [Fact]
    public void Resolve_StringPattern_ExtractsSegmentAsRawString()
    {
        _registry.Register("/product/{id}", typeof(OrderViewModel));

        var entry = _registry.Resolve("/product/42");

        Assert.NotNull(entry);
        Assert.Equal("42", entry!.RawParameter);
    }

    [Fact]
    public void Register_WithFixedParameter_ReturnsFixedObject()
    {
        var fixedParam = new OrderParams(OrderId: 99);
        _registry.Register("/orders/special", typeof(OrderViewModel), fixedParam);

        var entry = _registry.Resolve("/orders/special");

        Assert.NotNull(entry);
        Assert.Same(fixedParam, entry!.RawParameter);
    }
}