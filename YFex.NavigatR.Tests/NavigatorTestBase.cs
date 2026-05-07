using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;

namespace YFex.NavigatR.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Fake INavigation — records what the Navigator told the platform to show
// ─────────────────────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────────────────────
// Fake routes
// ─────────────────────────────────────────────────────────────────────────────

internal sealed record HomeRoute : IRoute, IKeepAlive
{
    public string? DisplayName => "Home";
}

internal sealed record OrderRoute : IRouteAccepts<OrderParams>
{
    public string? DisplayName => "Orders";
}

internal sealed record PickerRoute : IRouteProduces<PickerResult>
{
    public string? DisplayName => "Picker";
}

internal sealed record CheckoutRoute : IRoute<CheckoutParams, CheckoutResult>
{
    public string? DisplayName => "Checkout";
}

internal sealed record SearchRoute : IRoute
{
    public string? DisplayName => "Search";
}

internal sealed record AdminRoute : IRoute
{
    public string? DisplayName => "Admin";
}

// ─────────────────────────────────────────────────────────────────────────────
// Fake params / results
// ─────────────────────────────────────────────────────────────────────────────

internal sealed record OrderParams(int OrderId);
internal sealed record PickerResult(string SelectedItem);
internal sealed record CheckoutParams(int CartId);
internal sealed record CheckoutResult(string TransactionId);

// ─────────────────────────────────────────────────────────────────────────────
// Fake ViewModels — manually implement what the generator would produce
// ─────────────────────────────────────────────────────────────────────────────

[Route("order", Parameter = typeof(OrderParams))]
internal class HomeViewModel : INavigable
{
    public List<string> Calls { get; } = new();

    public Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        Calls.Add($"OnNavigation:{context.Direction}");
        return Task.CompletedTask;
    }

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class OrderViewModel : INavigable, INavigableAccepts<OrderParams>
{
    public List<string> Calls { get; } = new();
    public OrderParams? ReceivedParams { get; private set; }

    public Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        ReceivedParams = context.GetParameter<OrderParams>();
        Calls.Add($"OnNavigation:{context.Direction}:{ReceivedParams.OrderId}");
        return Task.CompletedTask;
    }

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class PickerViewModel : INavigable<PickerResult>
{
    public List<string> Calls { get; } = new();
    private readonly TaskCompletionSource<NavigationResult<PickerResult>> _tcs = new();

    public Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        Calls.Add("OnNavigation");
        return Task.CompletedTask;
    }

    public void Returns(PickerResult result)
        => _tcs.TrySetResult(NavigationResult.Ok(result));

    public void Cancel()
        => _tcs.TrySetResult(NavigationResult.Cancel());

    public void Deny(string? reason = null)
        => _tcs.TrySetResult(NavigationResult.Deny(reason));

    Task<NavigationResult<PickerResult>> INavigable<PickerResult>.WaitForResultAsync()
        => _tcs.Task;

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class CheckoutViewModel : INavigable<CheckoutResult>, INavigableAccepts<CheckoutParams>
{
    public List<string> Calls { get; } = new();
    public CheckoutParams? ReceivedParams { get; private set; }
    private readonly TaskCompletionSource<NavigationResult<CheckoutResult>> _tcs = new();

    public Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        ReceivedParams = context.GetParameter<CheckoutParams>();
        Calls.Add($"OnNavigation:{ReceivedParams.CartId}");
        return Task.CompletedTask;
    }

    public void Returns(CheckoutResult result)
        => _tcs.TrySetResult(NavigationResult.Ok(result));

    public void Cancel()
        => _tcs.TrySetResult(NavigationResult.Cancel());

    public void Deny(string? reason = null)
        => _tcs.TrySetResult(NavigationResult.Deny(reason));

    Task<NavigationResult<CheckoutResult>> INavigable<CheckoutResult>.WaitForResultAsync()
        => _tcs.Task;

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal class DenyingViewModel : INavigable
{
    public Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        context.Deny("Access denied");
        return Task.CompletedTask;
    }

    public Task OnResume(CancellationToken ct) => Task.CompletedTask;
    public Task OnSuspend(CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}

internal static class NavigatorFactory
{
    public static (Navigator navigator, FakeNavigation navPane, RouteRegistry registry) Create(
        Action<IServiceCollection>? configureServices = null,
        int poolCapacity = 10)
    {
        var services = new ServiceCollection();

        // Register all fake ViewModels
        services.AddTransient<HomeViewModel>();
        services.AddTransient<OrderViewModel>();
        services.AddTransient<PickerViewModel>();
        services.AddTransient<CheckoutViewModel>();
        services.AddTransient<DenyingViewModel>();

        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var registry = new RouteRegistry();

        // Register routes
        registry.Register<HomeRoute, HomeViewModel>();
        registry.Register<OrderRoute, OrderViewModel>();
        registry.Register<PickerRoute, PickerViewModel>();
        registry.Register<CheckoutRoute, CheckoutViewModel>();
        registry.Register<AdminRoute, DenyingViewModel>();
        registry.Register<SearchRoute, HomeViewModel>(); // reuse for simplicity

        var navPane = new FakeNavigation();
        var navigator = new Navigator(scope, registry, poolCapacity)
        {
            NavPane = navPane
        };

        NavigatorLocator.Set(navigator);
        return (navigator, navPane, registry);
    }
}