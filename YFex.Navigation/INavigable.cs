namespace YFex.NavigatR;

/// <summary>
/// Base interface for any page or view model that participates in navigation.
/// </summary>
public interface INavigable : IDisposable
{
    /// <summary>
    /// Async guard. Return false to cancel navigation and trigger OnNavigationDenied on INavigation.
    /// </summary>
    Task<bool> CanNavigate(CancellationToken ct = default);

    /// <summary>
    /// Called when entering this page for the first time, or after reconstruction from a dead weak reference.
    /// </summary>
    Task OnNavigation(CancellationToken ct = default);

    /// <summary>
    /// Called when returning to an already-alive instance (back navigation or context switch back).
    /// </summary>
    Task OnResume(CancellationToken ct = default);

    /// <summary>
    /// Called when leaving this page due to forward navigation or a context switch away.
    /// </summary>
    Task OnSuspend(CancellationToken ct = default);
}

/// <summary>
/// Opt-in interface for navigables that return a value to their caller.
/// </summary>
/// <typeparam name="TResult">The type of value returned to the caller.</typeparam>
public interface INavigable<TResult> : INavigable
{
    /// <summary>
    /// Called by the navigable itself before navigating back.
    /// Completes the awaited Task&lt;NavigationResult&lt;TResult&gt;&gt; on the caller side.
    /// </summary>
    void Returns(TResult result);
}

/// <summary>
/// Opt-in interface for navigables that receive a typed parameter and return a typed value.
/// </summary>
/// <typeparam name="TParameter">The type of parameter received on navigation.</typeparam>
/// <typeparam name="TResult">The type of value returned to the caller.</typeparam>
public interface INavigable<TParameter, TResult> : INavigable<TResult>
{
    /// <summary>
    /// Called when entering this page with a typed parameter.
    /// The base OnNavigation() is not called by the navigator when this interface is implemented.
    /// </summary>
    Task OnNavigation(TParameter parameter, CancellationToken ct = default);
}