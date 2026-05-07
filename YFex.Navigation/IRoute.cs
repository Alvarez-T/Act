namespace YFex.NavigatR;

/// <summary>
/// Marker interface for all route types.
/// Implement <see cref="IKeepAlive"/> on the route to prevent pool eviction.
/// </summary>
public interface IRoute
{
    string? DisplayName => null;
}

/// <summary>
/// A route whose destination ViewModel produces a typed result.
/// Enables compiler inference: <c>await navigator.NavigateTo(new PickerRoute())</c>
/// returns <c>NavigationResult&lt;TResult&gt;</c>.
/// </summary>
public interface IRouteProduces<TResult> : IRoute
{
}

/// <summary>
/// Internal — used by the generator to wire parameter inference on NavigateTo.
/// Not part of the developer-facing API.
/// </summary>
public interface IRouteAccepts<TParameter> : IRoute
{
}

/// <summary>
/// Internal — shorthand for parameter + result route.
/// </summary>
public interface IRoute<TParameter, TResult>
    : IRouteAccepts<TParameter>, IRouteProduces<TResult>
{
}

/// <summary>
/// Marker interface for routes whose ViewModel should never be evicted
/// from the navigation pool regardless of pool pressure.
/// </summary>
public interface IKeepAlive { }