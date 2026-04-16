using Microsoft.Extensions.DependencyInjection;
using YFex.NavigatR.Exceptions;

namespace YFex.NavigatR;

/// <summary>
/// Manages a single navigation context: history stack, cursor, lifecycle calls,
/// and result completion sources. Fully portable — zero platform dependencies.
/// Context management (open, close, switch) is handled by NavigatorHost.
///
/// Created by NavigatorHost.OpenContext() — never instantiated directly.
/// Receives a ScopedPageResolver tied to its own DI scope, so navigables
/// resolved within this context get this navigator via constructor injection.
/// </summary>
public sealed class Navigator(IServiceScope scope, RouteRegistry routeRegistry) : IDisposable
{
    private readonly IServiceScope _scope = scope;
    private readonly RouteRegistry? _routeRegistry = routeRegistry;
    private readonly List<NavigationEntry> _history = new();
    private int _cursor = -1;

    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Controls what happens to the forward stack when navigating to a new page mid-history.
    /// Each context can have its own independent policy.
    /// Default: PruneForwardOnBranch.
    /// </summary>
    public NavigationHistoryPolicy HistoryPolicy { get; internal set; } = NavigationHistoryPolicy.PruneForwardOnBranch;

    /// <summary>The platform hook for this context.</summary>
    internal INavigation? NavPane { get; set; }

    /// <summary>
    /// Read-only projection of history up to and including the current cursor.
    /// Safe to bind directly to a breadcrumb UI component.
    /// </summary>
    public IReadOnlyList<NavigationEntry> Breadcrumb =>
        _history.Take(_cursor + 1).ToList().AsReadOnly();

    /// <summary>Navigate to a page. Fire and forget.</summary>
    //public void NavigateTo<T>(CancellationToken ct = default, bool keepAlive = false)
    //    where T : class, INavigable
    //{
    //    _ = ExecuteNavigationAsync(typeof(T), parameter: null, keepAlive: keepAlive, ct: ct);
    //}

    public void NavigateTo<T, TParameter>(
        TParameter parameter,
        CancellationToken ct = default,
        bool keepAlive = false)
        where T : class, INavigable<>
    { }

    ///// <summary>Navigate to a page that returns a value. TResult inferred from INavigable&lt;TResult&gt;.</summary>
    //public Task<NavigationResult<TResult>> NavigateTo<T, TResult>(CancellationToken ct = default, bool keepAlive = false)
    //    where T : class, INavigable<TResult>
    //{
    //    return ExecuteNavigationWithResultAsync<TResult>(typeof(T), parameter: null, keepAlive: keepAlive, ct: ct);
    //}

        /// <summary>Navigate to a page that receives a parameter and returns a value.</summary>
    public Task<NavigationResult<TResult>> NavigateTo<T, TParameter, TResult>(
        TParameter parameter,
        CancellationToken ct = default,
        bool keepAlive = false)
        where T : class, INavigable<TParameter, TResult>
    {
        return ExecuteNavigationWithResultAsync<TResult>(typeof(T), parameter: parameter, keepAlive: keepAlive, ct: ct);
    }

    /// <summary>
    /// Navigate via route string.
    /// Call .WithResult&lt;TResult&gt;() on the returned operation for typed result handling.
    /// </summary>
    public NavigationOperation NavigateTo(string route, CancellationToken ct = default, bool keepAlive = false)
    {
        if (_routeRegistry is null)
            throw new InvalidOperationException(
                "Route navigation requires an IRouteRegistry. Provide one via AddNavigatR().");

        var routeEntry = _routeRegistry.Resolve(route)
            ?? throw new InvalidOperationException($"No route registered for '{route}'.");

        return new NavigationOperation(async (_, innerCt) =>
        {
            var navigable = ResolveNavigable(routeEntry.NavigableType, routeEntry.Parameter);
            ValidateRouteNavigable(navigable, routeEntry.NavigableType, routeEntry.Parameter, route);

            var returnsIface = GetReturnsInterface(navigable.GetType());

            if (returnsIface is not null)
            {
                var tcs = new TaskCompletionSource<NavigationResult<object?>>();

                using var reg = innerCt.Register(() =>
                    tcs.TrySetResult(new NavigationCancelled()));

                NavigableReturnInterceptor.Register(navigable, result =>
                    tcs.TrySetResult(new NavigationSuccess<object?>(result)));

                if (!await navigable.CanNavigate(innerCt))
                {
                    NavigableReturnInterceptor.Unregister(navigable);
                    NavPane?.OnNavigationDenied();
                    return new NavigationDenied();
                }

                await ExecuteNavigationAsync(
                    routeEntry.NavigableType, routeEntry.Parameter, keepAlive, innerCt,
                    preResolvedNavigable: navigable);

                return await tcs.Task;
            }

            await ExecuteNavigationAsync(
                routeEntry.NavigableType, routeEntry.Parameter, keepAlive, innerCt,
                preResolvedNavigable: navigable);

            return new NavigationCancelled();
        }, ct);
    }

    /// <summary>Navigate backward one step or to a specific history index.</summary>
    public void NavigateBackward(int? index = null, CancellationToken ct = default)
    {
        var target = index ?? _cursor - 1;
        if (target < 0 || target >= _cursor) return;
        _ = MoveToIndexAsync(target, skipGuard: false, ct);
    }

    /// <summary>Navigate forward one step or to a specific history index.</summary>
    public void NavigateForward(int? index = null, CancellationToken ct = default)
    {
        var target = index ?? _cursor + 1;
        if (target <= _cursor || target >= _history.Count) return;
        _ = MoveToIndexAsync(target, skipGuard: false, ct);
    }

    /// <summary>Navigate backward to the most recent entry of type T.</summary>
    public void NavigateBackwardTo<T>(CancellationToken ct = default)
        where T : class, INavigable
    {
        var idx = FindLastIndex<T>(0, _cursor - 1);
        if (idx >= 0) NavigateBackward(idx, ct);
    }

    /// <summary>Navigate forward to the next entry of type T.</summary>
    public void NavigateForwardTo<T>(CancellationToken ct = default)
        where T : class, INavigable
    {
        var idx = FindFirstIndex<T>(_cursor + 1, _history.Count - 1);
        if (idx >= 0) NavigateForward(idx, ct);
    }

    /// <summary>
    /// Jump directly to a history entry by index.
    /// Used for breadcrumb clicks. Skips CanNavigate guard.
    /// </summary>
    public void NavigateToIndex(int index)
    {
        if (index < 0 || index >= _history.Count) return;
        _ = MoveToIndexAsync(index, skipGuard: true, CancellationToken.None);
    }

    /// <summary>
    /// Wipes the entire history stack and resets the cursor.
    /// Use for context resets such as post-login flows.
    /// </summary>
    public void ClearHistory()
    {
        foreach (var entry in _history)
        {
            var instance = entry.NavigableInstance;
            if (instance is not null)
                NavigableReturnInterceptor.Unregister(instance);
            entry.Release();
        }

        _history.Clear();
        _cursor = -1;
    }

    internal async Task SuspendTopAsync(CancellationToken ct)
        => await SuspendCurrentAsync(ct);

    internal async Task ResumeTopAsync(CancellationToken ct)
    {
        if (_cursor < 0 || _cursor >= _history.Count) return;
        await ReplayEntryAsync(_history[_cursor], skipGuard: true, ct);
    }

    private async Task ExecuteNavigationAsync(
        Type navigableType,
        object? parameter,
        bool keepAlive,
        CancellationToken ct,
        INavigable? preResolvedNavigable = null)
    {
        INavigable? navigable = preResolvedNavigable ?? ResolveNavigable(navigableType, parameter);

        if (!await navigable.CanNavigate(ct))
        {
            NavPane?.OnNavigationDenied();
            return;
        }

        await SuspendCurrentAsync(ct);
        PruneForwardStack();

        var entry = new NavigationEntry(navigableType, parameter, keepAlive);
        entry.NavigableInstance = navigable;
        InsertEntry(entry);

        var calledParameterized = await TryCallParameterizedNavigationAsync(navigable, parameter, ct);
        if (!calledParameterized)
            await navigable.OnNavigation(ct);

        var view = _scope.Resolve(navigableType, parameter);
        NavPane?.PerformNavigation(view);
    }

    private async Task<NavigationResult<TResult>> ExecuteNavigationWithResultAsync<TResult>(
        Type navigableType,
        object? parameter,
        bool keepAlive,
        CancellationToken ct)
    {
        var navigable = ResolveNavigable(navigableType, parameter);

        if (navigable is not INavigable<TResult> _)
            throw new InvalidOperationException(
                $"'{navigableType.Name}' does not implement INavigable<{typeof(TResult).Name}>.");

        var tcs = new TaskCompletionSource<NavigationResult<TResult>>();

        using var reg = ct.Register(() => tcs.TrySetResult(new NavigationCancelled()));

        NavigableReturnInterceptor.Register(navigable, result =>
        {
            if (result is TResult typed)
                tcs.TrySetResult(new NavigationSuccess<TResult>(typed));
            else
                tcs.TrySetResult(new NavigationCancelled());
        });

        if (!await navigable.CanNavigate(ct))
        {
            NavigableReturnInterceptor.Unregister(navigable);
            NavPane?.OnNavigationDenied();
            return new NavigationDenied();
        }

        await SuspendCurrentAsync(ct);
        PruneForwardStack();

        var entry = new NavigationEntry(navigableType, parameter, keepAlive);
        entry.NavigableInstance = navigable;
        InsertEntry(entry);

        var calledParameterized = await TryCallParameterizedNavigationAsync(navigable, parameter, ct);
        if (!calledParameterized)
            await navigable.OnNavigation(ct);

        var view = _scope.Resolve(navigableType, parameter);
        NavPane?.PerformNavigation(view);

        return await tcs.Task;
    }

    private async Task MoveToIndexAsync(int targetIndex, bool skipGuard, CancellationToken ct)
    {
        await SuspendCurrentAsync(ct);
        _cursor = targetIndex;
        await ReplayEntryAsync(_history[_cursor], skipGuard, ct);
    }

    private async Task ReplayEntryAsync(NavigationEntry entry, bool skipGuard, CancellationToken ct)
    {
        var instance = entry.NavigableInstance;
        var isReconstruction = instance is null;

        if (isReconstruction)
        {
            instance = ResolveNavigable(entry.NavigableType, entry.Parameter);
            entry.NavigableInstance = instance;
        }

        if (!skipGuard && !await instance.CanNavigate(ct))
        {
            NavPane?.OnNavigationDenied();
            return;
        }

        if (isReconstruction)
        {
            var calledParameterized = await TryCallParameterizedNavigationAsync(
                instance, entry.Parameter, ct);
            if (!calledParameterized)
                await instance.OnNavigation(ct);
        }
        else
        {
            await instance.OnResume(ct);
        }

        var view = _scope.Resolve(entry.NavigableType, entry.Parameter);
        NavPane?.PerformNavigation(view);
    }

    private async Task SuspendCurrentAsync(CancellationToken ct)
    {
        if (_cursor < 0 || _cursor >= _history.Count) return;

        var current = _history[_cursor].NavigableInstance;
        if (current is null) return;

        await current.OnSuspend(ct);

        if (!_history[_cursor].KeepAlive)
            _history[_cursor].Release();
    }

    private INavigable ResolveNavigable(Type type, object? parameter)
    {
        Type navigableType = type;
        
        if (parameter is not null)
        {
            var paramType = parameter.GetType();
            var genericNavType = typeof(INavigable<,>).MakeGenericType(paramType, typeof(object));
            if (genericNavType.IsAssignableFrom(type))
                navigableType = genericNavType;
        }

        var resolved = _scope.ServiceProvider.GetRequiredService(navigableType);

        return resolved as INavigable
            ?? throw new InvalidOperationException(
                $"Resolved instance of '{type.Name}' does not implement INavigable.");
    }

    private static async Task<bool> TryCallParameterizedNavigationAsync(
        INavigable navigable,
        object? parameter,
        CancellationToken ct)
    {
        if (parameter is null) return false;

        foreach (Type iface in navigable.GetType().GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            if (iface.GetGenericTypeDefinition() != typeof(INavigable<,>)) continue;

            var paramType = iface.GetGenericArguments()[0];
            if (!paramType.IsInstanceOfType(parameter)) continue;

            var method = iface.GetMethod(
                nameof(INavigable<,>.OnNavigation),
                new[] { paramType, typeof(CancellationToken) });

            if (method is null) continue;

            var task = method.Invoke(navigable, new[] { parameter, ct }) as Task;
            if (task is not null) await task;

            return true;
        }

        return false;
    }

    private static void ValidateRouteNavigable(
        INavigable navigable,
        Type navigableType,
        object? parameter,
        string route)
    {
        foreach (var iface in navigableType.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            if (iface.GetGenericTypeDefinition() != typeof(INavigable<,>)) continue;

            var expectedParamType = iface.GetGenericArguments()[0];

            if (parameter is null)
                throw new NavigationParameterMissingException(
                    navigableType, expectedParamType, route);

            if (!expectedParamType.IsInstanceOfType(parameter))
                throw new NavigationParameterTypeMismatchException(
                    navigableType, expectedParamType, parameter.GetType(), route);
        }
    }

    private static Type? GetReturnsInterface(Type navigableType)
    {
        foreach (var iface in navigableType.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(INavigable<>) || def == typeof(INavigable<,>))
                return iface;
        }
        return null;
    }

    private void PruneForwardStack()
    {
        if (_cursor >= _history.Count - 1) return;

        if (HistoryPolicy == NavigationHistoryPolicy.PreserveForwardOnBranch)
            return;

        for (var i = _cursor + 1; i < _history.Count; i++)
        {
            var instance = _history[i].NavigableInstance;
            if (instance is not null)
                NavigableReturnInterceptor.Unregister(instance);

            _history[i].Release();
        }

        _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
    }

    private void InsertEntry(NavigationEntry entry)
    {
        if (HistoryPolicy == NavigationHistoryPolicy.PreserveForwardOnBranch
            && _cursor < _history.Count - 1)
        {
            _history.Insert(_cursor + 1, entry);
        }
        else
        {
            _history.Add(entry);
        }

        _cursor++;
    }

    private void ReleaseAllStrong()
    {
        foreach (var entry in _history)
            entry.Release();
    }

    private int FindLastIndex<T>(int from, int to)
    {
        for (var i = to; i >= from; i--)
            if (_history[i].NavigableType == typeof(T)) return i;
        return -1;
    }

    private int FindFirstIndex<T>(int from, int to)
    {
        for (var i = from; i <= to; i++)
            if (_history[i].NavigableType == typeof(T)) return i;
        return -1;
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}