using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR;

namespace YFex.NavigatR.Tests;

internal sealed class FakeNavigation : INavigation
{
    public List<object> NavigatedViews { get; } = new();
    public int DeniedCount { get; private set; }
    public void PerformNavigation(object view) => NavigatedViews.Add(view);
    public void OnNavigationDenied() => DeniedCount++;
    public object? LastView => NavigatedViews.LastOrDefault();

    public event Action? UserBecameInactive;
    public event Action? UserBecameActive;

    // Test helpers — simulate AFK
    public void SimulateInactive() => UserBecameInactive?.Invoke();
    public void SimulateActive() => UserBecameActive?.Invoke();
}

// ─── Params / Results ────────────────────────────────────────────────────────

internal sealed record OrderParams(int OrderId);
internal sealed record PickerResult(string SelectedItem);
internal sealed record CheckoutParams(int CartId);
internal sealed record CheckoutResult(string TransactionId);
internal sealed record ProductParams(int ProductId);
internal sealed record ProductData(string Title, decimal Price);
internal sealed record CustomerData(string Name);

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
internal sealed record ProductRoute(ProductParams Params) : IRoute { public string? DisplayName => "Product"; }
internal sealed record ArticleRoute(int Id) : IRoute { public string? DisplayName => "Article"; }

// ─── ViewModels ───────────────────────────────────────────────────────────────

internal class HomeViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    { Calls.Add($"OnNavigation:{ctx.Direction}"); return Task.CompletedTask; }
    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
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

// ─── Prefetch ViewModels ─────────────────────────────────────────────────────

/// <summary>
/// Simulates what the generator produces for a ViewModel with one [Prefetch] Task&lt;T&gt; method.
/// OnNavigation receives the prefetched ProductData directly.
/// </summary>
internal class ProductViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public ProductData? ReceivedData { get; private set; }
    public bool PrefetchWasCalled { get; private set; }
    public bool OnNavigationWasCalled { get; private set; }

    // Simulates the Task<T>? backing field generated by [Prefetch]
    private Task<ProductData>? _fetchDataTask;

    // Simulates generated INavigable.OnPrefetch bridge
    public Task OnPrefetch(NavigationContext ctx, CancellationToken ct)
    {
        PrefetchWasCalled = true;
        var p = ((ProductRoute)ctx.Route).Params;
        _fetchDataTask = Task.Run(() => FetchDataCore(p, ct), ct);
        Calls.Add("OnPrefetch");
        return _fetchDataTask;
    }

    // Simulates generated INavigable.OnNavigation bridge
    // Awaits prefetch if running, calls fresh if not
    public async Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    {
        OnNavigationWasCalled = true;
        var p = ((ProductRoute)ctx.Route).Params;
        var data = _fetchDataTask is not null
            ? await _fetchDataTask.ConfigureAwait(false)
            : await FetchDataCore(p, ct).ConfigureAwait(false);
        _fetchDataTask = null;

        // Calls developer's typed OnNavigation
        ReceivedData = data;
        Calls.Add($"OnNavigation:{data.Title}");
    }

    // Simulates developer's FetchDataCore (renamed from FetchData by generator)
    private Task<ProductData> FetchDataCore(ProductParams p, CancellationToken ct)
        => Task.FromResult(new ProductData($"Product-{p.ProductId}", 9.99m));

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

/// <summary>
/// Simulates a ViewModel with a slow prefetch — useful for testing in-flight awaiting.
/// </summary>
internal class SlowProductViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public ProductData? ReceivedData { get; private set; }
    public TaskCompletionSource<ProductData> PrefetchTcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<ProductData>? _fetchDataTask;

    public Task OnPrefetch(NavigationContext ctx, CancellationToken ct)
    {
        _fetchDataTask = Task.Run(() => PrefetchTcs.Task, ct);
        Calls.Add("OnPrefetch");
        return _fetchDataTask;
    }

    public async Task OnNavigation(NavigationContext ctx, CancellationToken ct)
    {
        var p = ((ProductRoute)ctx.Route).Params;
        var data = _fetchDataTask is not null
            ? await _fetchDataTask.ConfigureAwait(false)
            : await Task.FromResult(new ProductData($"Fresh-{p.ProductId}", 0m));
        _fetchDataTask = null;
        ReceivedData = data;
        Calls.Add($"OnNavigation:{data.Title}");
    }

    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

/// <summary>
/// Simulates a ViewModel with AFK tracking.
/// </summary>
internal class AfkViewModel : INavigable
{
    public List<string> Calls { get; } = new();
    public Task OnNavigation(NavigationContext ctx, CancellationToken ct) { Calls.Add("OnNavigation"); return Task.CompletedTask; }
    public Task OnResume(CancellationToken ct) { Calls.Add("OnResume"); return Task.CompletedTask; }
    public Task OnSuspend(CancellationToken ct) { Calls.Add("OnSuspend"); return Task.CompletedTask; }
    public Task OnActive(CancellationToken ct) { Calls.Add("OnActive"); return Task.CompletedTask; }
    public void Dispose() { Calls.Add("Dispose"); }
}

internal sealed record AfkRoute : IRoute { public string? DisplayName => "Afk"; }

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
        services.AddTransient<ProductViewModel>();
        services.AddTransient<SlowProductViewModel>();
        services.AddTransient<AfkViewModel>();
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

        registry.Register<ProductRoute, ProductViewModel>();
        registry.Register<AfkRoute, AfkViewModel>();

        var navPane = new FakeNavigation();
        var navigator = new Navigator(scope, registry, poolCapacity) { NavPane = navPane };
        NavigatorLocator.Set(navigator);
        return (navigator, navPane, registry);
    }
}