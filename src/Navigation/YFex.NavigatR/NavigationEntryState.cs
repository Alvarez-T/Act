namespace YFex.NavigatR;


/// <summary>
/// Lifecycle state of a navigation history entry.
/// </summary>
public enum NavigationEntryState
{
    /// <summary>Currently on screen.</summary>
    Active,

    /// <summary>
    /// Alive in memory, not visible. Subject to pool eviction unless <see cref="IKeepAlive"/>.
    /// </summary>
    Suspended,

    /// <summary>
    /// Mid-execution awaiting a child NavigateTo result.
    /// Never evictable. OnSuspend/OnResume never called.
    /// </summary>
    Pinned,

    /// <summary>Disposed. History entry still exists but instance is gone.</summary>
    Released,

    /// <summary>
    /// ViewModel is resolved and OnPrefetch is running or completed.
    /// Not yet navigated to. Subject to prefetch timeout.
    /// Never counts toward pool capacity.
    /// </summary>
    Prefetching
}
