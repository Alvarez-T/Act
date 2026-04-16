namespace YFex.NavigatR;

/// <summary>
/// Marker interface for all route types.
/// A route is a lightweight value object that identifies a navigation destination
/// and optionally carries typed parameters.
/// </summary>
public interface IRoute
{
    /// <summary>
    /// Optional human-readable name shown in breadcrumbs or tab titles.
    /// Returns <c>null</c> by default.
    /// </summary>
    string? DisplayName => null;
}

/// <summary>
/// Typed route that declares the parameter and result types for a navigation destination.
/// </summary>
/// <typeparam name="TParams">The type of the navigation parameter passed to the ViewModel.</typeparam>
/// <typeparam name="TResult">The type of the result returned when the ViewModel completes.</typeparam>
public interface IRoute<TParams, TResult> : IRoute
    where TParams : notnull
    where TResult : notnull
{
}
