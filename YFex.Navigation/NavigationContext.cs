namespace YFex.NavigatR;

/// <summary>
/// Carries all information about the current navigation event.
/// Passed to <see cref="INavigable.OnNavigation"/> on every fresh navigation.
/// <para>
/// When <c>[Route(Parameter = typeof(T))]</c> is declared, the generator produces
/// a file-scoped extension that reads the parameter directly from the route property
/// via a direct cast — no reflection, no type argument needed at the call site.
/// Developers with a typed <c>OnNavigation(T parameter, ct)</c> partial never
/// need to interact with this class directly.
/// </para>
/// </summary>
public sealed class NavigationContext
{
    private bool _denied;
    private string? _deniedReason;

    internal NavigationContext(
        IRoute route,
        IRoute? previousRoute,
        NavigationDirection direction,
        int stackDepth)
    {
        Route = route;
        PreviousRoute = previousRoute;
        Direction = direction;
        StackDepth = stackDepth;
    }

    /// <summary>The route that triggered this navigation.</summary>
    public IRoute Route { get; }

    /// <summary>The route active before this navigation, or null if initial.</summary>
    public IRoute? PreviousRoute { get; }

    /// <summary>Direction of travel in the navigation stack.</summary>
    public NavigationDirection Direction { get; }

    /// <summary>Current depth in the navigation stack (0 = root).</summary>
    public int StackDepth { get; }

    /// <summary>
    /// Blocks navigation. Caller receives <see cref="NavigationResult.Denied"/>.
    /// Call <c>return</c> immediately after.
    /// </summary>
    public void Deny(string? reason = null)
    {
        _denied = true;
        _deniedReason = reason;
    }

    internal bool IsDenied => _denied;
    internal string? DeniedReason => _deniedReason;
}

public enum NavigationDirection { Initial, Forward, Backward }