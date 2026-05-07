using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;

namespace YFex.NavigatR.Tests;

internal sealed class FakeNavigation : INavigation
{
    public List<object> NavigatedViews { get; } = new();
    public int DeniedCount { get; private set; }
    public void PerformNavigation(object view) => NavigatedViews.Add(view);
    public void OnNavigationDenied() => DeniedCount++;

    public void PerformContextSwitch(object view)
    {
        throw new NotImplementedException();
    }

    public object? LastView => NavigatedViews.LastOrDefault();
}

// ─── Params / Results ────────────────────────────────────────────────────────

internal sealed record OrderParams(int OrderId);
internal sealed record PickerResult(string SelectedItem);
internal sealed record CheckoutParams(int CartId);
internal sealed record CheckoutResult(string TransactionId);

// ─── Routes — parameter lives on constructor ─────────────────────────────────

internal sealed record HomeRoute : IRoute, IKeepAlive
{
    public string? DisplayName => "Home";
}

// Route carries OrderParams
internal sealed record OrderRoute(OrderParams Params) : IRoute
{
    public string? DisplayName => "Orders";
}

internal sealed record PickerRoute : IRouteProduces<PickerResult>
{
    public string? DisplayName => "Picker";
}

// Route carries CheckoutParams + produces result
internal sealed record CheckoutRoute(CheckoutParams Params) : IRouteProduces<CheckoutResult>
{
    public string? DisplayName => "Checkout";
}

internal sealed record SearchRoute : IRoute { public string? DisplayName => "Search"; }
internal sealed record AdminRoute : IRoute { public string? DisplayName => "Admin"; }
internal sealed record SettingsRoute : IRoute { public string? DisplayName => "Settings"; }

// ─── ViewModels ───────────────────────────────────────────────────────────────

internal class HomeViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    { 
        Calls.Add($"OnNavigation:{ctx.Direction}"); 
        return Task.CompletedTask; 
    }

    public Task OnResume(CancellationToken ct)
    { 
        Calls.Add("OnResume");
        return Task.CompletedTask; 
    }

    public Task OnSuspend(CancellationToken ct) 
    { 
        Calls.Add("OnSuspend");
        return Task.CompletedTask; 
    }

    public void Dispose() 
    {
        Calls.Add("Dispose"); 
    }
}

internal class OrderViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public OrderParams? ReceivedParams { get; private set; }

    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    {
        // Direct cast — simulates generator-produced bridge
        ReceivedParams = ((OrderRoute)ctx.Route).Params;
        Calls.Add($"OnNavigation:{ctx.Direction}:{ReceivedParams.OrderId}");
        return Task.CompletedTask;
    }

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class PickerViewModel : INavigable<PickerResult>
{
    public List<string> Calls { get; } = new();
    private readonly TaskCompletionSource<NavigationResult<PickerResult>> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    { Calls.Add("OnNavigation"); return Task.CompletedTask; }

    public void Returns(PickerResult r) => _tcs.TrySetResult(NavigationResult.Ok(r));
    public void Cancel() => _tcs.TrySetResult(NavigationResult.Cancel());
    public void Deny(string? reason) => _tcs.TrySetResult(NavigationResult.Deny(reason));

    Task<NavigationResult<PickerResult>> INavigable<PickerResult>.WaitForResultAsync() => _tcs.Task;

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class CheckoutViewModel : INavigable<CheckoutResult>
{
    public List<string> Calls { get; } = new();
    public CheckoutParams? ReceivedParams { get; private set; }
    private readonly TaskCompletionSource<NavigationResult<CheckoutResult>> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    {
        ReceivedParams = ((CheckoutRoute)ctx.Route).Params;
        Calls.Add($"OnNavigation:{ReceivedParams.CartId}");
        return Task.CompletedTask;
    }

    public void Returns(CheckoutResult r) => _tcs.TrySetResult(NavigationResult.Ok(r));
    public void Cancel() => _tcs.TrySetResult(NavigationResult.Cancel());
    public void Deny(string? reason) => _tcs.TrySetResult(NavigationResult.Deny(reason));

    Task<NavigationResult<CheckoutResult>> INavigable<CheckoutResult>.WaitForResultAsync() => _tcs.Task;

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class DenyingViewModel : INavigable
{
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    { ctx.Deny("Access denied"); return Task.CompletedTask; }
    public Task OnResume(CancellationToken ct) => Task.CompletedTask;
    public Task OnSuspend(CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}

internal class SettingsViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    { Calls.Add("OnNavigation"); return Task.CompletedTask; }
    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

// ─── Factory ─────────────────────────────────────────────────────────────────

internal static class NavigatorFactory
{
    public static (Navigator navigator, FakeNavigation navPane, RouteRegistry registry) Create(
        Action<IServiceCollection>? configureServices = null,
        int poolCapacity = 10)
    {
        var services = new ServiceCollection();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<OrderViewModel>();
        services.AddTransient<PickerViewModel>();
        services.AddTransient<CheckoutViewModel>();
        services.AddTransient<DenyingViewModel>();
        services.AddTransient<SettingsViewModel>();
        configureServices?.Invoke(services);

        var scope = services.BuildServiceProvider().CreateScope();
        var registry = new RouteRegistry();
        registry.Register<HomeRoute, HomeViewModel>();
        registry.Register<OrderRoute, OrderViewModel>();
        registry.Register<PickerRoute, PickerViewModel>();
        registry.Register<CheckoutRoute, CheckoutViewModel>();
        registry.Register<AdminRoute, DenyingViewModel>();
        registry.Register<SearchRoute, HomeViewModel>();
        registry.Register<SettingsRoute, SettingsViewModel>();

        var navPane = new FakeNavigation();
        var navigator = new Navigator(scope, registry, poolCapacity) { NavPane = navPane };
        NavigatorLocator.Set(navigator);
        return (navigator, navPane, registry);
    }
}