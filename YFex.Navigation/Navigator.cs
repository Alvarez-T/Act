using Microsoft.Extensions.DependencyInjection;

namespace YFex.NavigatR;

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

    public IReadOnlyList<NavigationEntry> Breadcrumb =>
        _history.Take(_cursor + 1).ToList().AsReadOnly();

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
        => new(this, route, ct);

    public NavigationTask NavigateTo(string route, CancellationToken ct = default)
    {
        var entry = _routeRegistry.Resolve(route)
            ?? throw new InvalidOperationException($"No route registered for '{route}'.");

        // Resolve the typed route type for this ViewModel
        var routeType = _routeRegistry.ResolveRouteType(entry.ViewModelType);
        IRoute resolvedRoute;

        if (routeType is null)
        {
            // No typed route registered — use anonymous route
            resolvedRoute = new AnonymousRoute(entry.ViewModelType);
        }
        else
        {
            resolvedRoute = ConstructRoute(routeType, entry.RawParameter);
        }

        return new NavigationTask(this, resolvedRoute, ct);
    }

    /// <summary>
    /// Constructs a typed route instance from a route type and a raw parameter.
    /// Handles three cases:
    /// 1. No parameter — parameterless constructor
    /// 2. Fixed object — passed directly to constructor
    /// 3. String segment — parsed to the constructor param type via IParsable or direct assignment
    /// </summary>
    private static IRoute ConstructRoute(Type routeType, object? rawParameter)
    {
        // Case 1 — no parameter extracted
        if (rawParameter is null)
        {
            var ctor = routeType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Route '{routeType.Name}' has no parameterless constructor " +
                    $"and no parameter was extracted from the URL.");
            return (IRoute)ctor.Invoke(null);
        }

        // Find the single-parameter constructor on the route
        var ctors = routeType.GetConstructors();
        foreach (var ctor in ctors)
        {
            var ctorParams = ctor.GetParameters();
            if (ctorParams.Length != 1) continue;

            var paramType = ctorParams[0].ParameterType;

            // Case 2 — fixed object already the right type
            if (paramType.IsInstanceOfType(rawParameter))
                return (IRoute)ctor.Invoke(new[] { rawParameter });

            // Case 3a — string segment, target is string
            if (rawParameter is string rawStr && paramType == typeof(string))
                return (IRoute)ctor.Invoke(new[] { (object)rawStr });

            // Case 3b — string segment, target is IParsable<T>
            if (rawParameter is string segment)
            {
                var parsed = TryParse(paramType, segment);
                if (parsed is not null)
                    return (IRoute)ctor.Invoke(new[] { parsed });

                throw new InvalidOperationException(
                    $"Cannot parse '{segment}' to '{paramType.Name}' for route '{routeType.Name}'. " +
                    $"Ensure the parameter type implements IParsable<T> or register a fixed parameter.");
            }
        }

        throw new InvalidOperationException(
            $"Route '{routeType.Name}' has no suitable constructor for parameter " +
            $"of type '{rawParameter.GetType().Name}'.");
    }

    /// <summary>
    /// Attempts to parse a string to the target type via IParsable&lt;T&gt;.
    /// Handles all BCL primitives and any user type implementing IParsable&lt;T&gt;.
    /// </summary>
    private static object? TryParse(Type targetType, string value)
    {
        // Unwrap nullable
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Find IParsable<T>.Parse(string, IFormatProvider) via interface
        var parsableInterface = underlying
            .GetInterfaces()
            .FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IParsable<>) &&
                i.GetGenericArguments()[0] == underlying);

        if (parsableInterface is not null)
        {
            var parseMethod = parsableInterface.GetMethod("Parse",
                new[] { typeof(string), typeof(IFormatProvider) });

            if (parseMethod is not null)
            {
                try { return parseMethod.Invoke(null, new object?[] { value, null }); }
                catch { return null; }
            }
        }

        // Fallback — TypeConverter for older types
        try
        {
            var converter = System.ComponentModel.TypeDescriptor.GetConverter(underlying);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromInvariantString(value);
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Moves back by one, or to the specified absolute index.
    /// Caller can await or discard — await for deterministic state, discard for fire-and-forget.
    /// </summary>
    public Task NavigateBackward(int? index = null, CancellationToken ct = default)
    {
        int target = index ?? (_cursor > 0 ? _cursor - 1 : 0);
        return target < _cursor ? MoveToIndexAsync(target, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Moves forward by one, or to the specified absolute index.
    /// Caller can await or discard.
    /// </summary>
    public Task NavigateForward(int? index = null, CancellationToken ct = default)
    {
        int target = index ?? (_cursor < _history.Count - 1 ? _cursor + 1 : _cursor);
        return target > _cursor ? MoveToIndexAsync(target, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Moves backward to the most recent entry whose route is <typeparamref name="TRoute"/>.
    /// Caller can await or discard.
    /// </summary>
    public Task NavigateBackwardTo<TRoute>(CancellationToken ct = default)
        where TRoute : class, IRoute
    {
        int idx = FindLastIndex<TRoute>(_cursor - 1);
        return idx >= 0 ? MoveToIndexAsync(idx, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Moves forward to the next entry whose route is <typeparamref name="TRoute"/>.
    /// Caller can await or discard.
    /// </summary>
    public Task NavigateForwardTo<TRoute>(CancellationToken ct = default)
        where TRoute : class, IRoute
    {
        int idx = FindFirstIndex<TRoute>(_cursor + 1);
        return idx >= 0 ? MoveToIndexAsync(idx, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Jumps directly to the entry at the given absolute index.
    /// Caller can await or discard.
    /// </summary>
    public Task NavigateToIndex(int index)
    {
        if (index < 0 || index >= _history.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return MoveToIndexAsync(index, CancellationToken.None);
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

        if (entry.State == NavigationEntryState.Pinned)
        {
            // Pinned = mid-await, never call OnResume.
            // But we still need to restore the view so the platform shows the correct screen.
            var vmType = entry.ResolvedViewModelType
                ??= (entry.Route is AnonymousRoute anon
                    ? anon.ViewModelType
                    : _routeRegistry.ResolveViewModel(entry.Route));
            NavPane?.PerformNavigation(_scope.Resolve(vmType));
            return;
        }

        // Tab switch back — treat as Forward (returning to context, not navigating back in history)
        await ResumeEntryAsync(entry, ct, NavigationDirection.Forward);
    }

    // -----------------------------------------------------------------------
    // Internal — called by NavigationTask
    // -----------------------------------------------------------------------

    internal Task<NavigationResult> ExecuteNavigationAsync(IRoute route, CancellationToken ct)
        => ExecuteNavigationCoreAsync(route, ct);

    internal Task<NavigationResult> ExecuteNavigationUntilClosedAsync(IRoute route, CancellationToken ct)
        => ExecuteUntilClosedCoreAsync(route, ct);

    internal Task<NavigationResult<TResult>> ExecuteNavigationWithResultAsync<TResult>(
        IRoute route, CancellationToken ct)
        => ExecuteWithResultCoreAsync<TResult>(route, ct);

    // -----------------------------------------------------------------------
    // Private — core execution
    // -----------------------------------------------------------------------

    private async Task<NavigationResult> ExecuteNavigationCoreAsync(IRoute route, CancellationToken ct)
    {
        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);
        var ctx = BuildContext(route);

        // OnNavigation first — caller untouched until navigation confirmed
        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return NavigationResult.Deny(ctx.DeniedReason);
        }

        // Navigation confirmed — NOW suspend caller
        await SuspendCurrentAsync(ct);

        var entry = new NavigationEntry<IRoute>(route)
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

    private async Task<NavigationResult> ExecuteUntilClosedCoreAsync(IRoute route, CancellationToken ct)
    {
        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);
        var ctx = BuildContext(route);

        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return NavigationResult.Deny(ctx.DeniedReason);
        }

        // Navigation confirmed — pin caller
        var callerEntry = _cursor >= 0 ? _history[_cursor] : null;
        if (callerEntry is not null)
            callerEntry.State = NavigationEntryState.Pinned;

        var entry = new NavigationEntry<IRoute>(route)
        {
            NavigableInstance = navigable,
            ResolvedViewModelType = vmType,
            State = NavigationEntryState.Active
        };

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

    private async Task<NavigationResult<TResult>> ExecuteWithResultCoreAsync<TResult>(
        IRoute route, CancellationToken ct)
    {
        var vmType = ResolveViewModelType(route);
        var navigable = ResolveNavigable(vmType);

        if (navigable is not INavigable<TResult> producer)
        {
            if (navigable is IDisposable d) d.Dispose();
            throw new InvalidOperationException(
                $"ViewModel '{vmType.Name}' does not implement INavigable<{typeof(TResult).Name}>.");
        }

        var ctx = BuildContext(route);

        // OnNavigation first — caller untouched until navigation confirmed
        await navigable.OnNavigation(ctx, ct);

        if (ctx.IsDenied)
        {
            if (navigable is IDisposable d) d.Dispose();
            NavPane?.OnNavigationDenied();
            return new NavigationDenied(ctx.DeniedReason);
        }

        // Navigation confirmed — pin caller
        var callerEntry = _cursor >= 0 ? _history[_cursor] : null;
        if (callerEntry is not null)
            callerEntry.State = NavigationEntryState.Pinned;

        var entry = new NavigationEntry<IRoute>(route)
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
            return new NavigationCancelled();
        }
    }

    private async Task MoveToIndexAsync(int targetIndex, CancellationToken ct)
    {
        if (targetIndex == _cursor) return;

        // Fire OnClosed on entries being navigated away from (backward navigation)
        if (targetIndex < _cursor)
        {
            for (int i = _cursor; i > targetIndex; i--)
            {
                var closing = _history[i];
                closing.OnClosed?.Invoke();
                closing.OnClosed = null;
            }
        }

        var direction = targetIndex < _cursor
            ? NavigationDirection.Backward
            : NavigationDirection.Forward;

        await SuspendCurrentAsync(ct);
        _cursor = targetIndex;
        await ResumeEntryAsync(_history[_cursor], ct, direction);
    }

    private async Task ResumeEntryAsync(NavigationEntry entry, CancellationToken ct, NavigationDirection direction)
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
                // Reconstruct — direction reflects actual travel direction (Backward or Forward)
                entry.NavigableInstance = ResolveNavigable(vmType);
                entry.State = NavigationEntryState.Active;
                _pool.OnActivated(entry);
                var ctx = BuildContext(entry.Route, direction);
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

    private NavigationContext BuildContext(IRoute route, NavigationDirection? direction = null)
    {
        var previousRoute = _cursor >= 0 && _cursor < _history.Count
            ? _history[_cursor].Route : null;

        var resolvedDirection = direction ?? (_cursor < 0
            ? NavigationDirection.Initial
            : NavigationDirection.Forward);

        return new NavigationContext(route, previousRoute, resolvedDirection, _cursor + 1);
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

file static class ServiceScopeExtensions
{
    internal static object Resolve(this IServiceScope scope, Type viewModelType)
        => scope.ServiceProvider.GetService(viewModelType);
}