namespace YFex.NavigatR;

/// <summary>
/// Ambient locator for the current <see cref="Navigator"/>.
/// Uses <see cref="AsyncLocal{T}"/> so each async context (UI thread, test, tab)
/// has its own isolated Navigator without any static global state bleeding across.
/// </summary>
public static class NavigatorLocator
{
    private static readonly AsyncLocal<Navigator?> _current = new();

    /// <summary>
    /// Sets the Navigator for the current async context.
    /// Call this once per context (tab, window, test) before any navigation occurs.
    /// </summary>
    internal static void Set(Navigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        _current.Value = navigator;
    }

    /// <summary>
    /// Clears the Navigator for the current async context.
    /// Useful in tests to ensure isolation between test runs.
    /// </summary>
    internal static void Clear() => _current.Value = null;

    /// <summary>
    /// Returns the Navigator for the current async context.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no Navigator has been set for the current async context.
    /// Call <see cref="Set"/> before navigating.
    /// </exception>
    public static Navigator GetNavigator()
        => _current.Value
            ?? throw new InvalidOperationException(
                "No Navigator has been set for the current async context. " +
                "Call NavigatorLocator.Set(navigator) before navigating.");
}