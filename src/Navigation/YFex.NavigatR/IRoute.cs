namespace YFex.NavigatR;

/// <summary>
/// Marker interface for all route types.
/// A route is a value object that identifies a navigation destination.
/// When a parameter is declared via [Route(Parameter = typeof(T))], the generated
/// route record carries it in the constructor.
/// </summary>
public interface IRoute
{
    string? DisplayName => null;
}

/// <summary>
/// A route whose destination ViewModel produces a typed result.
/// Enables: await navigator.NavigateTo(new PickerRoute()).UntilReturns&lt;PickerResult&gt;()
/// </summary>
public interface IRouteProduces<TResult> : IRoute
{
}

/// <summary>
/// Internal — shorthand for a route that produces a result.
/// Used by generator only.
/// </summary>
internal interface IRoute<TParameter, TResult> : IRouteProduces<TResult>
{
}

/// <summary>
/// Marker interface for routes whose ViewModel should never be evicted
/// from the navigation pool regardless of pool pressure.
/// </summary>
public interface IKeepAlive { }