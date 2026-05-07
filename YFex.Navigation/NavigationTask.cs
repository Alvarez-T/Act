using System.Runtime.CompilerServices;

namespace YFex.NavigatR;

/// <summary>
/// Shared awaitable returned by <see cref="NavigationTask.WithResult{TResult}"/>
/// and <see cref="NavigationTask{TRoute,TParameter}.WithResult{TResult}"/>.
/// The caller is pinned until the ViewModel calls Returns(), Cancel(), or Deny().
/// </summary>
public sealed class NavigationTask<TResult>
{
    private readonly Func<Task<NavigationResult<TResult>>> _execute;

    internal NavigationTask(Func<Task<NavigationResult<TResult>>> execute)
        => _execute = execute;

    public TaskAwaiter<NavigationResult<TResult>> GetAwaiter()
        => _execute().GetAwaiter();
}

/// <summary>
/// Returned by <see cref="Navigator.NavigateTo(IRoute,CancellationToken)"/>
/// and <see cref="Navigator.NavigateTo(string,CancellationToken)"/>.
/// Await directly to suspend caller normally, or chain WithResult to pin caller.
/// </summary>
public sealed class NavigationTask
{
    private readonly Navigator _navigator;
    private readonly IRoute _route;
    private readonly object? _parameter;
    private readonly CancellationToken _ct;

    internal NavigationTask(Navigator navigator, IRoute route, object? parameter, CancellationToken ct)
    {
        _navigator = navigator;
        _route = route;
        _parameter = parameter;
        _ct = ct;
    }

    /// <summary>
    /// Fires the navigation. Caller suspended normally. Result discarded.
    /// </summary>
    public TaskAwaiter<NavigationResult> GetAwaiter()
        => _navigator.ExecuteNavigationAsync(_route, _parameter, _ct).GetAwaiter();

    /// <summary>
    /// Opts into result awaiting. Caller pinned.
    /// Runtime error if ViewModel does not implement INavigable&lt;TResult&gt;.
    /// </summary>
    public NavigationTask<TResult> WithResult<TResult>()
        => new(() => _navigator.ExecuteNavigationWithResultAsync<TResult>(_route, _parameter, _ct));

    /// <summary>
    /// Pins caller and waits until ViewModel calls Returns(), Cancel(), or Deny().
    /// Compile error if TRoute does not implement IRoute&lt;TParameter, TResult&gt;.
    /// </summary>
    public NavigationTask<TResult> UntilReturns<TResult>()
        => new(() => _navigator.ExecuteNavigationWithResultAsync<TResult>(_route, _parameter, _ct));

    public NavigationTask<NavigationResult> UntilReturns()
        => new(() => _navigator.ExecuteNavigationUntilClosedAsync(_route, _parameter, _ct));
}

/// <summary>
/// Returned by <see cref="Navigator.NavigateTo{TRoute,TParameter}"/>.
/// Await directly to suspend caller normally, or chain WithResult to pin caller.
/// </summary>
public sealed class NavigationTask<TRoute, TParameter>
    where TRoute : IRouteAccepts<TParameter>
{
    private readonly Navigator _navigator;
    private readonly TRoute _route;
    private readonly TParameter _parameter;
    private readonly CancellationToken _ct;

    internal NavigationTask(Navigator navigator, TRoute route, TParameter parameter, CancellationToken ct)
    {
        _navigator = navigator;
        _route = route;
        _parameter = parameter;
        _ct = ct;
    }

    /// <summary>
    /// Fires the navigation. Caller suspended normally. Result discarded.
    /// </summary>
    public TaskAwaiter<NavigationResult> GetAwaiter()
        => _navigator.ExecuteNavigationAsync(_route, _parameter, _ct).GetAwaiter();

    /// <summary>
    /// Opts into result awaiting. Caller pinned.
    /// Compile error if TRoute does not implement IRoute&lt;TParameter, TResult&gt;.
    /// </summary>
    public NavigationTask<TResult> WithResult<TResult>()
        => new(() => _navigator.ExecuteNavigationWithResultAsync<TParameter, TResult>(_route, _parameter, _ct));
}