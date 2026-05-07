using System.Runtime.CompilerServices;

namespace YFex.NavigatR;

/// <summary>
/// Awaitable returned by <see cref="NavigationTask.UntilReturns{TResult}"/>.
/// Execution starts eagerly when UntilReturns is called — not when awaited.
/// The caller is pinned until the ViewModel calls Returns(), Cancel(), or Deny().
/// </summary>
public sealed class NavigationTask<TResult>
{
    private readonly Task<NavigationResult<TResult>> _task;

    // Eager — task already running
    internal NavigationTask(Task<NavigationResult<TResult>> task)
        => _task = task;

    public TaskAwaiter<NavigationResult<TResult>> GetAwaiter()
        => _task.GetAwaiter();
}

/// <summary>
/// Returned by all <see cref="Navigator.NavigateTo"/> overloads.
/// <para>
/// <b>Await directly</b> — lazy execution. Fires navigation when awaited.
/// Caller suspended normally after <see cref="INavigable.OnNavigation"/> confirms.
/// Resumes immediately after OnNavigation completes. Result discarded.
/// </para>
/// <para>
/// <b><see cref="UntilReturns()"/></b> — eager execution. Starts immediately when called.
/// Pins caller after OnNavigation confirms. Waits until screen closes via back navigation.
/// Returns <see cref="NavigationResult"/>.
/// </para>
/// <para>
/// <b><see cref="UntilReturns{TResult}"/></b> — eager execution. Starts immediately when called.
/// Pins caller after OnNavigation confirms. Waits until ViewModel calls Returns(), Cancel(), or Deny().
/// Returns <see cref="NavigationResult{TResult}"/>.
/// </para>
/// </summary>
public sealed class NavigationTask
{
    private readonly Navigator _navigator;
    private readonly IRoute _route;
    private readonly CancellationToken _ct;

    // Eager task — set when UntilReturns() is called
    private readonly Task<NavigationResult>? _eagerTask;

    // Lazy constructor — used by Navigator.NavigateTo(), runs on await
    internal NavigationTask(Navigator navigator, IRoute route, CancellationToken ct)
    {
        _navigator = navigator;
        _route = route;
        _ct = ct;
    }

    // Eager constructor — used internally by UntilReturns()
    private NavigationTask(Navigator navigator, IRoute route, CancellationToken ct,
        Task<NavigationResult> eagerTask)
    {
        _navigator = navigator;
        _route = route;
        _ct = ct;
        _eagerTask = eagerTask;
    }

    /// <summary>
    /// Lazy — fires navigation when awaited.
    /// Caller suspended normally. Result discarded.
    /// </summary>
    public TaskAwaiter<NavigationResult> GetAwaiter()
        => (_eagerTask ?? _navigator.ExecuteNavigationAsync(_route, _ct)).GetAwaiter();

    /// <summary>
    /// Eager — starts execution immediately.
    /// Pins caller after OnNavigation confirms.
    /// Waits until screen closes via back navigation.
    /// </summary>
    public NavigationTask UntilReturns()
    {
        var task = _navigator.ExecuteNavigationUntilClosedAsync(_route, _ct);
        return new NavigationTask(_navigator, _route, _ct, task);
    }

    /// <summary>
    /// Eager — starts execution immediately.
    /// Pins caller after OnNavigation confirms.
    /// Waits until ViewModel calls Returns(), Cancel(), or Deny().
    /// Runtime error if ViewModel does not implement INavigable&lt;TResult&gt;.
    /// </summary>
    public NavigationTask<TResult> UntilReturns<TResult>()
        => new(_navigator.ExecuteNavigationWithResultAsync<TResult>(_route, _ct));
}