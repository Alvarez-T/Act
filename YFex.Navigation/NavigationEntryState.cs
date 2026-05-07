namespace YFex.NavigatR;

/// <summary>
/// Represents the lifecycle state of a single navigation history entry.
/// </summary>
public enum NavigationEntryState
{
    /// <summary>
    /// The screen is currently visible and active.
    /// </summary>
    Active,

    /// <summary>
    /// The screen is alive in memory but not visible.
    /// <see cref="INavigable.OnSuspend"/> has been called.
    /// Subject to pool eviction if not <see cref="IKeepAlive"/>.
    /// </summary>
    Suspended,

    /// <summary>
    /// The screen is mid-execution awaiting a child NavigateTo result.
    /// Instance is always alive, never evictable, never closeable.
    /// <see cref="INavigable.OnSuspend"/> and <see cref="INavigable.OnResume"/>
    /// are never called while pinned.
    /// </summary>
    Pinned,

    /// <summary>
    /// The screen has been disposed and its history entry removed.
    /// Can only transition here from <see cref="Suspended"/>.
    /// </summary>
    Released
}