namespace YFex.NavigatR;

/// <summary>
/// Carries all information about the current navigation event.
/// Passed to <see cref="INavigable.OnNavigation"/> on every fresh navigation.
/// When <c>[Route(Parameter = typeof(T))]</c> is declared, the generator hides this
/// behind a typed partial method — the developer rarely needs to use it directly.
/// </summary>
public sealed class NavigationContext
{
    private bool _denied;
    private string? _deniedReason;
    private readonly object? _parameter;

    internal NavigationContext(
        IRoute route,
        IRoute? previousRoute,
        NavigationDirection direction,
        int stackDepth,
        object? parameter)
    {
        Route = route;
        PreviousRoute = previousRoute;
        Direction = direction;
        StackDepth = stackDepth;
        _parameter = parameter;
    }

    /// <summary>The route that triggered this navigation.</summary>
    public IRoute Route { get; }

    /// <summary>The route active before this navigation, or null if initial.</summary>
    public IRoute? PreviousRoute { get; }

    /// <summary>Direction of travel in the navigation stack.</summary>
    public NavigationDirection Direction { get; }

    /// <summary>Current depth in the navigation stack (0 = root).</summary>
    public int StackDepth { get; }

    /// <summary>True when a parameter was passed with this navigation.</summary>
    public bool HasParameter => _parameter is not null;

    /// <summary>
    /// Returns the typed parameter.
    /// </summary>
    /// <exception cref="InvalidOperationException">No parameter or type mismatch.</exception>
    public T GetParameter<T>()
    {
        if (_parameter is T typed) return typed;
        throw new InvalidOperationException(
            _parameter is null
                ? "No parameter was passed to this navigation."
                : $"Parameter is '{_parameter.GetType().Name}', not '{typeof(T).Name}'.");
    }

    /// <summary>Safe parameter extraction. Returns false if none or type mismatch.</summary>
    public bool TryGetParameter<T>(out T value)
    {
        if (_parameter is T typed) { value = typed; return true; }
        value = default!;
        return false;
    }

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

public enum NavigationDirection
{
    Initial,
    Forward,
    Backward
}