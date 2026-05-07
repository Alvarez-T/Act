namespace YFex.NavigatR;

/// <summary>
/// Base navigation history entry.
/// The parameter is stored on the route itself — no separate boxing needed.
/// </summary>
public abstract class NavigationEntry
{
    public IRoute Route { get; }
    public DateTimeOffset NavigatedAt { get; } = DateTimeOffset.UtcNow;
    public NavigationEntryState State { get; internal set; } = NavigationEntryState.Active;
    public bool IsKeepAlive { get; }

    internal INavigable? NavigableInstance { get; set; }
    internal Type? ResolvedViewModelType { get; set; }

    /// <summary>
    /// Optional callback fired when this entry is closed via back navigation.
    /// Used by UntilReturns() to complete the caller's awaitable.
    /// </summary>
    internal Action? OnClosed { get; set; }

    private protected NavigationEntry(IRoute route)
    {
        Route = route;
        IsKeepAlive = route is IKeepAlive;
    }

    internal void Release()
    {
        if (State == NavigationEntryState.Pinned)
            throw new InvalidOperationException(
                $"Cannot release Pinned entry '{Route.GetType().Name}' — it is mid-await.");

        if (NavigableInstance is IDisposable d) d.Dispose();
        NavigableInstance = null;
        State = NavigationEntryState.Released;
    }
}

/// <summary>
/// Strongly-typed navigation entry.
/// The typed route is accessible without casting via <see cref="Route"/>.
/// </summary>
public sealed class NavigationEntry<TRoute> : NavigationEntry
    where TRoute : IRoute
{
    public new TRoute Route => (TRoute)base.Route;

    public NavigationEntry(TRoute route)
        : base(route) { }
}