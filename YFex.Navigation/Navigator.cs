using Microsoft.Extensions.DependencyInjection;

namespace YFex.NavigatR;

/// <summary>
/// Manages a navigation stack for a single UI context.
/// Must be registered as Scoped in DI.
/// </summary>
public sealed class Navigator : IDisposable
{
    private readonly IServiceScope _scope;
    private readonly RouteRegistry _routeRegistry;
    private readonly NavigablePool _pool;
    private readonly List<NavigationEntry> _history = new();
    private int _cursor = -1;

    internal Guid Id { get; } = Guid.NewGuid();
    public NavigationHistoryPolicy HistoryPolicy { get; internal set; } = NavigationHistoryPolicy.PruneForwardOnBranch;
    internal INavigation? NavPane { get; set; }

    public IReadOnlyList<NavigationEntry> Breadcrumb => _history.Take(_cursor + 1).ToList().AsReadOnly();

    public Navigator(IServiceScope scope, RouteRegistry routeRegistry, int poolCapacity = 10)
    {
        _scope = scope;
        _routeRegistry = routeRegistry;
        _pool = new NavigablePool(poolCapacity);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public NavigationTask NavigateTo(IRoute route, CancellationToken ct = default)
        => new(this, route, parameter: null, ct);

    public NavigationTask<TRoute, TParameter> NavigateTo<TRoute, TParameter>(
        TRoute route,
        TParameter parameter,
        CancellationToken ct = default)
        where TRoute : IRouteAccepts<TParameter>
        => new(this, route, parameter, ct);

    public NavigationTask NavigateTo(string route, CancellationToken ct = default)
    {
        var routeEntry = _routeRegistry.Resolve(route)
            ?? throw new InvalidOperationException($"No route registered for '{route}'.");

        var syntheticRoute = new AnonymousRoute(routeEntry.RouteType);
        return new NavigationTask(this, syntheticRoute, routeEntry.Parameter, ct);
    }

    public void NavigateBackward(int? index = null, CancellationToken ct = default)
    {
        int target = index ?? (_cursor > 0 ? _cursor - 1 : 0);
        if (target < _cursor) _ = MoveToIndexAsync(target, ct);
    }

    public void NavigateForward(int? index = null, CancellationToken ct = default)
    {
        int target = index ?? (_cursor < _history.Count - 1 ? _cursor + 1 : _cursor);
        if (target > _cursor) _ = MoveToIndexAsync(target, ct);
    }

    public void NavigateBackwardTo<TRoute>(CancellationToken ct = default)
        where TRoute : class, IRoute
    {
        int idx = FindLastIndex<TRoute>(_cursor - 1);
        if (idx >= 0) _ = MoveToIndexAsync(idx, ct);
    }

    public void NavigateForwardTo<TRoute>(CancellationToken ct = default)
        where TRoute : class, IRoute
    {
        int idx = FindFirstIndex<TRoute>(_cursor + 1);
        if (idx >= 0) _ = MoveToIndexAsync(idx, ct);
    }

    public void NavigateToIndex(int index)
    {
        if (index < 0 || index >= _history.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _ = MoveToIndexAsync(index, CancellationToken.None);
    }

    public void ClearHistory()
    {
        foreach (var entry in _history)
        {
            if (entry.State == NavigationEntryState.Pinned) continue;
            _pool.Remove(entry);
            if (entry.State != NavigationEntryState.Released)
                entry.Release();
        }
        _history.Clear();
        _cursor = -1;
    }

    // -----------------------------------------------------------------------
    // Internal — NavigatorHost surface
    // -----------------------------------------------------------------------

    internal async Task SuspendTopAsync(CancellationToken ct)
    {
        if (_cursor < 0 || _cursor >= _history.Count) return;
        var entry = _history[_cursor];
        if (entry.State == NavigationEntryState.Pinned) return;
        if (entry.NavigableInstance is not null)
            await entry.NavigableInstance.OnSuspend(ct);
        entry.State = NavigationEntryState.Suspended;
        _pool.OnSuspended(entry);
    }

    internal async Task ResumeTopAsync(CancellationToken ct)
    {
        if (_cursor < 0 || _cursor >= _history.Count) return;
        var entry = _history[_cursor];
        if (entry.State == NavigationEntryState.Pinned) return;
        await ResumeEntryAsync(entry, ct);
    }

    // -----------------------------------------------------------------------
    // Internal — called by NavigationTask and NavigationTask<TRoute, TParameter>
    // -----------------------------------------------------------------------

    // Called by NavigationTask.GetAwaiter() and NavigationTask<TRoute,TParameter>.GetAwaiter()
    internal Task<NavigationResult> ExecuteNavigationAsync(
        IRoute route,
        object? parameter,
        CancellationToken ct)
        => ExecuteNavigationCoreAsync(route, parameter, ct);

    // Called by NavigationTask.WithResult<TResult>()
    // No compile-time constraint — runtime throw if ViewModel doesn't implement INavigable<TResult>
    internal Task<NavigationResult<TResult>> ExecuteNavigationWithResultAsync<TResult>(
        IRoute route,
        object? parameter,
        CancellationToken ct)
        => ExecuteNavigationWithResultImpl<TResult>(route, parameter, ct);

    // Called by NavigationTask<TRoute,TParameter>.WithResult<TResult>()
    // Compile-time constraint via IRoute<TParameter,TResult>
    internal Task<NavigationResult<TResult>> ExecuteNavigationWithResultAsync<TRoute, TParameter, TResult>(
        TRoute route,
        TParameter parameter,
        CancellationToken ct)
        where TRoute : IRoute<TParameter, TResult>
        => ExecuteNavigationWithResultImpl<TResult>(route, (object?)parameter, ct);

    // Called by NavigationTask.UntilReturns() and NavigationTask<TRoute,TParameter>.UntilReturns()
    // Pins caller and waits until the child screen is closed via back navigation.
    // No typed result — completes with NavigationResult.Success when user navigates back,
    // NavigationResult.Denied if OnNavigation denies, NavigationResult.Cancelled if ct cancelled.
    internal async Task<NavigationResult> ExecuteNavigationUntilClosedAsync(
        IRoute route,
        object? parameter,
        CancellationToken ct)
    {
        var callerEntry = _cursor >= 0 ? _history[_cursor] : null;

        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);
        var ctx = BuildContext(route, parameter);

        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return NavigationResult.Deny(ctx.DeniedReason);
        }

        // Navigation confirmed — pin caller now
        if (callerEntry is not null)
            callerEntry.State = NavigationEntryState.Pinned;

        var entry = new NavigationEntry<IRoute>(route, parameter)
        {
            NavigableInstance = navigable,
            ResolvedViewModelType = vmType,
            State = NavigationEntryState.Active
        };

        // TCS completes when the child is closed via back navigation
        var tcs = new TaskCompletionSource<NavigationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        entry.OnClosed = () => tcs.TrySetResult(NavigationResult.Ok());

        PruneForwardStack();
        InsertEntry(entry);
        NavPane?.PerformNavigation(_scope.Resolve(vmType));

        try
        {
            var result = await tcs.Task.WaitAsync(ct);

            if (callerEntry is not null)
            {
                callerEntry.State = NavigationEntryState.Active;
                _pool.OnActivated(callerEntry);
                NavPane?.PerformNavigation(_scope.Resolve(
                    callerEntry.ResolvedViewModelType ?? ResolveViewModelType(callerEntry.Route)));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (callerEntry is not null) callerEntry.State = NavigationEntryState.Active;
            return NavigationResult.Cancel();
        }
    }

    // -----------------------------------------------------------------------
    // Private — core execution
    // -----------------------------------------------------------------------

    private async Task<NavigationResult> ExecuteNavigationCoreAsync(
        IRoute route,
        object? parameter,
        CancellationToken ct)
    {
        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);
        var ctx = BuildContext(route, parameter);

        // OnNavigation first — caller untouched until we know navigation is confirmed
        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            // Caller never knew anything happened — no OnSuspend was called
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return NavigationResult.Deny(ctx.DeniedReason);
        }

        // Navigation confirmed — NOW suspend caller
        await SuspendCurrentAsync(ct);

        var entry = new NavigationEntry<IRoute>(route, parameter)
        {
            NavigableInstance = navigable,
            ResolvedViewModelType = vmType,
            State = NavigationEntryState.Active
        };

        PruneForwardStack();
        InsertEntry(entry);
        NavPane?.PerformNavigation(_scope.Resolve(vmType));
        return NavigationResult.Ok();
    }

    // Single shared implementation for all result-producing navigation
    private async Task<NavigationResult<TResult>> ExecuteNavigationWithResultImpl<TResult>(
        IRoute route,
        object? parameter,
        CancellationToken ct)
    {
        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);

        // Check INavigable<TResult> before touching caller or running OnNavigation
        if (navigable is not INavigable<TResult> producer)
        {
            if (navigable is IDisposable d) d.Dispose();
            throw new InvalidOperationException(
                $"ViewModel '{vmType.Name}' does not implement INavigable<{typeof(TResult).Name}>.");
        }

        var ctx = BuildContext(route, parameter);

        // OnNavigation first — caller untouched until navigation confirmed
        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            // Caller never knew anything happened
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return new NavigationResult<TResult>.Denied(ctx.DeniedReason);
        }

        // Navigation confirmed — NOW pin caller
        var callerEntry = _cursor >= 0 ? _history[_cursor] : null;
        if (callerEntry is not null)
            callerEntry.State = NavigationEntryState.Pinned;

        var entry = new NavigationEntry<IRoute>(route, parameter)
        {
            NavigableInstance = navigable,
            ResolvedViewModelType = vmType,
            State = NavigationEntryState.Active
        };

        PruneForwardStack();
        InsertEntry(entry);
        NavPane?.PerformNavigation(_scope.Resolve(vmType));

        try
        {
            var result = await producer.WaitForResultAsync().WaitAsync(ct);

            _pool.Remove(entry);
            entry.Release();
            _history.Remove(entry);
            _cursor = _history.Count - 1;

            if (callerEntry is not null)
            {
                callerEntry.State = NavigationEntryState.Active;
                _pool.OnActivated(callerEntry);
                NavPane?.PerformNavigation(_scope.Resolve(
                    callerEntry.ResolvedViewModelType ?? ResolveViewModelType(callerEntry.Route)));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (callerEntry is not null) callerEntry.State = NavigationEntryState.Active;
            return new NavigationResult<TResult>.Cancelled();
        }
    }

    private async Task MoveToIndexAsync(int targetIndex, CancellationToken ct)
    {
        if (targetIndex == _cursor) return;

        // Fire OnClosed on all entries being navigated away from (backward navigation)
        // This completes any UntilReturns() awaitables on those entries
        if (targetIndex < _cursor)
        {
            for (int i = _cursor; i > targetIndex; i--)
            {
                var closing = _history[i];
                closing.OnClosed?.Invoke();
                closing.OnClosed = null;
            }
        }

        await SuspendCurrentAsync(ct);
        _cursor = targetIndex;
        await ResumeEntryAsync(_history[_cursor], ct);
    }

    private async Task ResumeEntryAsync(NavigationEntry entry, CancellationToken ct)
    {
        var vmType = entry.ResolvedViewModelType
            ??= (entry.Route is AnonymousRoute anon
                ? anon.ViewModelType
                : _routeRegistry.ResolveViewModel(entry.Route));

        switch (entry.State)
        {
            case NavigationEntryState.Suspended when entry.NavigableInstance is not null:
                entry.State = NavigationEntryState.Active;
                _pool.OnActivated(entry);
                await entry.NavigableInstance.OnResume(ct);
                break;

            case NavigationEntryState.Suspended:
            case NavigationEntryState.Released:
                entry.NavigableInstance = ResolveNavigable(vmType);
                entry.State = NavigationEntryState.Active;
                _pool.OnActivated(entry);
                var ctx = BuildContext(entry.Route, entry.BoxedParameter);
                await entry.NavigableInstance.OnNavigation(ctx, ct);
                break;

            case NavigationEntryState.Active:
            case NavigationEntryState.Pinned:
                break;
        }

        NavPane?.PerformNavigation(_scope.Resolve(vmType));
    }

    private async Task SuspendCurrentAsync(CancellationToken ct)
    {
        if (_cursor < 0 || _cursor >= _history.Count) return;
        var current = _history[_cursor];
        if (current.State == NavigationEntryState.Pinned) return;
        if (current.NavigableInstance is null) return;

        await current.NavigableInstance.OnSuspend(ct);
        current.State = NavigationEntryState.Suspended;
        _pool.OnSuspended(current);
    }

    private NavigationContext BuildContext(IRoute route, object? parameter)
    {
        var previousRoute = _cursor >= 0 && _cursor < _history.Count
            ? _history[_cursor].Route : null;

        var direction = _cursor < 0
            ? NavigationDirection.Initial
            : NavigationDirection.Forward;

        return new NavigationContext(route, previousRoute, direction, _cursor + 1, parameter);
    }

    private Type ResolveViewModelType(IRoute route)
        => route is AnonymousRoute anon
            ? anon.ViewModelType
            : _routeRegistry.ResolveViewModel(route);

    private INavigable ResolveNavigable(Type viewModelType)
    {
        var instance = _scope.Resolve(viewModelType)
            ?? throw new InvalidOperationException(
                $"DI scope could not resolve '{viewModelType.FullName}'.");
        return instance as INavigable
            ?? throw new InvalidOperationException(
                $"'{viewModelType.FullName}' does not implement INavigable.");
    }

    private void PruneForwardStack()
    {
        if (HistoryPolicy != NavigationHistoryPolicy.PruneForwardOnBranch) return;
        if (_cursor >= _history.Count - 1) return;
        for (int i = _cursor + 1; i < _history.Count; i++)
        {
            var e = _history[i];
            if (e.State == NavigationEntryState.Pinned) continue;
            _pool.Remove(e);
            if (e.State != NavigationEntryState.Released) e.Release();
        }
        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
    }

    private void InsertEntry(NavigationEntry entry)
    {
        _history.Add(entry);
        _cursor = _history.Count - 1;
    }

    private int FindLastIndex<TRoute>(int startFrom) where TRoute : class, IRoute
    {
        for (int i = Math.Min(startFrom, _history.Count - 1); i >= 0; i--)
            if (_history[i].Route is TRoute) return i;
        return -1;
    }

    private int FindFirstIndex<TRoute>(int startFrom) where TRoute : class, IRoute
    {
        for (int i = Math.Max(startFrom, 0); i < _history.Count; i++)
            if (_history[i].Route is TRoute) return i;
        return -1;
    }

    public void Dispose()
    {
        ClearHistory();
        _scope.Dispose();
    }

    private sealed class AnonymousRoute : IRoute
    {
        public Type ViewModelType { get; }
        public string? DisplayName => ViewModelType.Name;
        public AnonymousRoute(Type viewModelType) => ViewModelType = viewModelType;
    }
}

internal static class ServiceScopeExtensions
{
    internal static object? Resolve(this IServiceScope scope, Type viewModelType)
        => scope.ServiceProvider.GetService(viewModelType);
}