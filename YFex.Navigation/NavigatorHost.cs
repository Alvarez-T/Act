using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR.Exceptions;

namespace YFex.NavigatR;

/// <summary>
/// Manages independent navigation contexts, each backed by its own DI scope.
///
/// Each call to OpenContext():
///   1. Creates a new IServiceScope
///   2. Creates a Navigator bound to that scope's IPageResolver
///   3. Registers the Navigator instance into the scope so INavigator
///      resolves to it for all navigables created within that context
///
/// This means a LoginViewModel that declares INavigator in its constructor
/// automatically gets this context's navigator — no property injection needed.
///
/// Registered as Singleton in DI — one host manages all contexts for the app.
/// </summary>
public sealed class NavigatorHost(IServiceScopeFactory scopeFactory, RouteRegistry routeRegistry) : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly RouteRegistry? _routeRegistry = routeRegistry;
    private readonly List<Navigator> _contexts = new();
    private bool _disposed;

    /// <summary>Unique identifier for this host.</summary>
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>The currently active navigation context.</summary>
    public Navigator? ActiveContext { get; private set; }

    public IReadOnlyList<Navigator> Contexts
        => _contexts.AsReadOnly();

    public Navigator CreateContext()
    {
        IServiceScope scope = _scopeFactory.CreateScope();

        Navigator navigator = scope.ServiceProvider.GetRequiredService<Navigator>();

        if (ActiveContext is not null)
            navigator.HistoryPolicy = ActiveContext.HistoryPolicy;

        _contexts.Add(navigator);

        return navigator;
    }

    public void CloseContext(Guid id)
    {
        var context = FindContext(id);

        // Dispose releases the DI scope — all scoped services are cleaned up
        context.Dispose();
        _contexts.Remove(context);

        if (ActiveContext?.Id == id)
            ActiveContext = _contexts.FirstOrDefault();
    }

    public void SwitchContext(Guid id, CancellationToken ct = default)
    {
        if (ActiveContext?.Id == id) return;

        var target = FindContext(id);
        _ = SwitchContextAsync(ActiveContext, target, ct);
    }

    private async Task SwitchContextAsync(
        Navigator? from,
        Navigator to,
        CancellationToken ct)
    {
        if (from is not null)
            await from.SuspendTopAsync(ct);

        ActiveContext = to;

        await to.ResumeTopAsync(ct);
    }

    private Navigator FindContext(Guid id)
        => _contexts.FirstOrDefault(c => c.Id == id)
            ?? throw new NavigationContextException(
                $"No context with id '{id}' found.", id);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ctx in _contexts)
            ctx.Dispose();
        _contexts.Clear();
    }
}