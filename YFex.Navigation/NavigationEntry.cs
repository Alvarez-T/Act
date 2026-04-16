namespace YFex.NavigatR;

/// <summary>
/// Represents a single entry in the navigation history stack.
/// </summary>
public sealed class NavigationEntry
{
    /// <summary>
    /// The route that identifies this navigation destination.
    /// Used as the primary identity for breadcrumb display and history traversal.
    /// </summary>
    public IRoute Route { get; }

    /// <summary>
    /// The optional parameter passed to the ViewModel when navigation occurred.
    /// </summary>
    public object? Parameter { get; }

    /// <summary>
    /// Whether this entry's ViewModel instance is kept alive after navigating away
    /// (i.e. <see cref="INavigable.OnSuspend"/> is called instead of disposal).
    /// </summary>
    public bool KeepAlive { get; }

    /// <summary>
    /// The UTC timestamp of when this entry was created.
    /// </summary>
    public DateTimeOffset NavigatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The live ViewModel instance for this entry, or <c>null</c> when not yet resolved
    /// or after the instance has been released.
    /// </summary>
    internal INavigable? NavigableInstance { get; set; }

    /// <summary>
    /// Cached ViewModel <see cref="Type"/> resolved from <see cref="Route"/> via
    /// <see cref="RouteRegistry.ResolveViewModel"/>. Populated on first resolve and
    /// reused for reconstruction during replay.
    /// </summary>
    internal Type? ResolvedViewModelType { get; set; }

    /// <summary>
    /// Creates a new <see cref="NavigationEntry"/>.
    /// </summary>
    public NavigationEntry(IRoute route, object? parameter, bool keepAlive)
    {
        Route = route;
        Parameter = parameter;
        KeepAlive = keepAlive;
    }

    /// <summary>
    /// Disposes the live ViewModel instance (if disposable) and clears the reference.
    /// The <see cref="Route"/> and <see cref="Parameter"/> are retained for potential
    /// future reconstruction.
    /// </summary>
    internal void Release()
    {
        if (NavigableInstance is IDisposable disposable)
            disposable.Dispose();
        NavigableInstance = null;
        // Route and Parameter kept for reconstruction via ReplayEntryAsync
    }
}