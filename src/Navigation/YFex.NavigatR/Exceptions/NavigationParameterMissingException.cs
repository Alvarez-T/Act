namespace YFex.NavigatR.Exceptions;

/// <summary>
/// Thrown when a route resolves to an INavigable&lt;TParameter, TResult&gt; but no parameter was provided.
/// </summary>
public sealed class NavigationParameterMissingException : NavigationException
{
    public Type NavigableType { get; }
    public Type ExpectedParameterType { get; }
    public string? Route { get; }

    public NavigationParameterMissingException(
        Type navigableType,
        Type expectedParameterType,
        string? route = null)
        : base(
            $"'{navigableType.Name}' requires a parameter of type '{expectedParameterType.Name}'" +
            (route is not null ? $" (resolved from route '{route}')" : string.Empty) +
            " but none was provided.")
    {
        NavigableType = navigableType;
        ExpectedParameterType = expectedParameterType;
        Route = route;
    }
}
