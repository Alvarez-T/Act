namespace YFex.NavigatR.Exceptions;

/// <summary>
/// Thrown when a route resolves to an INavigable&lt;TResult&gt; but .WithResult() was not called
/// and the result is needed to complete the navigation.
/// </summary>
public sealed class NavigationResultExpectedException : NavigationException
{
    public Type NavigableType { get; }
    public string Route { get; }

    public NavigationResultExpectedException(Type navigableType, string route)
        : base(
            $"Route '{route}' resolves to '{navigableType.Name}' which returns a value. " +
            $"Call .WithResult<TResult>() on the NavigationOperation to handle the return value.")
    {
        NavigableType = navigableType;
        Route = route;
    }
}
