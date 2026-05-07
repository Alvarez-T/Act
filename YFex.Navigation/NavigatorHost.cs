using Microsoft.Extensions.DependencyInjection;

namespace YFex.NavigatR;

/// <summary>
/// Owns multiple navigation contexts (tabs, windows, panes) and coordinates
/// suspend/resume when switching between them.
/// </para>
/// </summary>
public sealed class NavigatorHost(IServiceScopeFactory scopeFactory, RouteRegistry routeRegistry) : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly RouteRegistry _routeRegistry = routeRegistry;
    private readonly List<Navigator> _contexts = new();
    private bool _disposed;

    internal Guid Id { get; } = Guid.NewGuid();
    public Navigator? ActiveContext { get; private set; }
    public IReadOnlyList<Navigator> Contexts => _contexts.AsReadOnly();

    public Navigator CreateContext(int poolCapacity = 10)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scope = _scopeFactory.CreateScope();
        var navigator = new Navigator(scope, _routeRegistry, poolCapacity);

        if (ActiveContext is not null)
            navigator.HistoryPolicy = ActiveContext.HistoryPolicy;

        _contexts.Add(navigator);
        return navigator;
    }

    public void CloseContext(Guid id)
    {
        var context = FindContext(id);
        _contexts.Remove(context);

        if (ActiveContext?.Id == id)
            ActiveContext = null;

        context.Dispose();
    }

    public void SwitchContext(Guid id, CancellationToken ct = default)
    {
        var next = FindContext(id);
        _ = SwitchContextAsync(ActiveContext, next, ct);
    }

    private async Task SwitchContextAsync(Navigator? from, Navigator to, CancellationToken ct)
    {
        if (ReferenceEquals(from, to)) return;

        if (from is not null)
            await from.SuspendTopAsync(ct);

        ActiveContext = to;

        await to.ResumeTopAsync(ct);
    }

    private Navigator FindContext(Guid id)
    {
        foreach (var ctx in _contexts)
            if (ctx.Id == id) return ctx;

        throw new InvalidOperationException($"No navigation context found with id '{id}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ctx in _contexts)
            ctx.Dispose();
        _contexts.Clear();
    }
}