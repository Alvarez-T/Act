using Microsoft.Extensions.DependencyInjection;

namespace YFex.NavigatR;

/// <summary>
/// Owns multiple navigation contexts (tabs, windows, panes) and coordinates
/// suspend/resume when switching between them.
/// <para>
/// Required DI registration:
/// <code>
/// services.AddSingleton&lt;RouteRegistry&gt;();
/// services.AddSingleton&lt;NavigatorHost&gt;();
/// </code>
/// </para>
/// </summary>
public sealed class NavigatorHost(IServiceScopeFactory scopeFactory, RouteRegistry routeRegistry) : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly RouteRegistry _routeRegistry = routeRegistry;
    private readonly List<Navigator> _contexts = new();
    private bool _disposed;

    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>The currently active navigation context.</summary>
    public Navigator? ActiveContext { get; private set; }

    /// <summary>All registered navigation contexts.</summary>
    public IReadOnlyList<Navigator> Contexts => _contexts.AsReadOnly();

    /// <summary>
    /// Creates a new navigation context with a new DI scope.
    /// Optionally sets the context as active immediately.
    /// </summary>
    /// <param name="navPane">
    /// The platform navigation hook for this context.
    /// Each context has its own <see cref="INavigation"/> since each tab/pane
    /// has its own UI surface.
    /// </param>
    /// <param name="setActive">
    /// When true, immediately sets this context as the active one.
    /// The previously active context is suspended.
    /// Defaults to true when this is the first context, false otherwise.
    /// </param>
    /// <param name="poolCapacity">Pool capacity for this context. Default 10.</param>
    public Navigator CreateContext(
        INavigation? navPane = null,
        bool? setActive = null,
        int poolCapacity = 10)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scope = _scopeFactory.CreateScope();
        var navigator = new Navigator(scope, _routeRegistry, poolCapacity)
        {
            NavPane = navPane
        };

        if (ActiveContext is not null)
            navigator.HistoryPolicy = ActiveContext.HistoryPolicy;

        _contexts.Add(navigator);

        // Default: first context is automatically active, subsequent ones are not
        bool shouldSetActive = setActive ?? (_contexts.Count == 1);

        if (shouldSetActive)
            _ = SwitchContextAsync(ActiveContext, navigator, CancellationToken.None);

        return navigator;
    }

    /// <summary>
    /// Registers an existing <see cref="Navigator"/> with this host.
    /// Useful when the navigator was created outside the host (e.g. in tests).
    /// </summary>
    /// <param name="navigator">The navigator to register.</param>
    /// <param name="setActive">
    /// When true, immediately sets this context as the active one.
    /// Defaults to true when this is the first context, false otherwise.
    /// </param>
    public void RegisterContext(Navigator navigator, bool? setActive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(navigator);

        if (_contexts.Contains(navigator))
            throw new InvalidOperationException("Navigator is already registered with this host.");

        if (ActiveContext is not null && navigator.HistoryPolicy != ActiveContext.HistoryPolicy)
            navigator.HistoryPolicy = ActiveContext.HistoryPolicy;

        _contexts.Add(navigator);

        bool shouldSetActive = setActive ?? (_contexts.Count == 1);

        if (shouldSetActive)
            _ = SwitchContextAsync(ActiveContext, navigator, CancellationToken.None);
    }

    /// <summary>
    /// Closes and disposes the context with the given <paramref name="id"/>.
    /// If it was the active context, <see cref="ActiveContext"/> is set to null.
    /// </summary>
    public void CloseContext(Guid id)
    {
        var context = FindContext(id);
        _contexts.Remove(context);

        if (ActiveContext?.Id == id)
            ActiveContext = null;

        context.Dispose();
    }

    /// <summary>
    /// Suspends the currently active context and resumes the one identified by <paramref name="id"/>.
    /// </summary>
    public Task SwitchContextAsync(Guid id, CancellationToken ct = default)
    {
        var next = FindContext(id);
        return SwitchContextAsync(ActiveContext, next, ct);
    }

    /// <summary>
    /// Synchronous overload — fire and forget. Exceptions are silently swallowed.
    /// Use <see cref="SwitchContextAsync(Guid, CancellationToken)"/> when you need to await.
    /// </summary>
    public void SwitchContext(Guid id, CancellationToken ct = default)
        => _ = SwitchContextAsync(id, ct);

    private async Task SwitchContextAsync(Navigator? from, Navigator to, CancellationToken ct)
    {
        if (ReferenceEquals(from, to)) return;

        // Suspend current — skips Pinned entries automatically
        if (from is not null)
            await from.SuspendTopAsync(ct);

        ActiveContext = to;

        // Resume target — skips Pinned entries, restores view for Pinned via NavPane
        await to.ResumeTopAsync(ct);
    }

    private Navigator FindContext(Guid id)
    {
        foreach (var ctx in _contexts)
            if (ctx.Id == id) return ctx;

        throw new InvalidOperationException($"No navigation context found with id '{id}'.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ctx in _contexts)
            ctx.Dispose();
        _contexts.Clear();
    }
}