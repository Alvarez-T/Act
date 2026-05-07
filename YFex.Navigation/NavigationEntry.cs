namespace YFex.NavigatR;

public abstract class NavigationEntry
{
    public IRoute Route { get; }
    public DateTimeOffset NavigatedAt { get; } = DateTimeOffset.UtcNow;
    public NavigationEntryState State { get; internal set; } = NavigationEntryState.Active;
    public bool IsKeepAlive { get; }

    internal INavigable? NavigableInstance { get; set; }
    internal Type? ResolvedViewModelType { get; set; }
    internal abstract object? BoxedParameter { get; }

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

public sealed class NavigationEntry<TRoute> : NavigationEntry
    where TRoute : IRoute
{
    public new TRoute Route => (TRoute)base.Route;
    public object? Parameter { get; }
    internal override object? BoxedParameter => Parameter;

    public NavigationEntry(TRoute route, object? parameter)
        : base(route) => Parameter = parameter;
}