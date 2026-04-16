namespace YFex.NavigatR.Exceptions;

/// <summary>
/// Thrown when a parameter is provided but its runtime type does not match
/// the type expected by INavigable&lt;TParameter, TResult&gt;.
/// </summary>
public sealed class NavigationParameterTypeMismatchException : NavigationException
{
    public Type NavigableType { get; }
    public Type ExpectedParameterType { get; }
    public Type ActualParameterType { get; }
    public string? Route { get; }

    public NavigationParameterTypeMismatchException(
        Type navigableType,
        Type expectedParameterType,
        Type actualParameterType,
        string? route = null)
        : base(
            $"'{navigableType.Name}' expects a parameter of type '{expectedParameterType.Name}' " +
            $"but received '{actualParameterType.Name}'" +
            (route is not null ? $" (resolved from route '{route}')" : string.Empty) + ".")
    {
        NavigableType = navigableType;
        ExpectedParameterType = expectedParameterType;
        ActualParameterType = actualParameterType;
        Route = route;
    }
}
