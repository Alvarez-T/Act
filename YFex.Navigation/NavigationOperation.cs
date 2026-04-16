using System.Runtime.CompilerServices;

namespace YFex.NavigatR;

/// <summary>
/// Returned by route-based NavigateTo(string route).
/// Enables the .WithResult&lt;TResult&gt;() fluent call for typed result handling.
/// When no result is needed, simply discard — fire and forget.
/// </summary>
public sealed class NavigationOperation
{
    private readonly Func<Type?, CancellationToken, Task<NavigationResult<object?>>> _factory;
    private readonly CancellationToken _ct;

    internal NavigationOperation(
        Func<Type?, CancellationToken, Task<NavigationResult<object?>>> factory,
        CancellationToken ct)
    {
        _factory = factory;
        _ct = ct;
    }

    /// <summary>
    /// Upgrades this operation to an awaitable typed result.
    /// The explicit type argument is required because route navigation erases type
    /// information at compile time — inference is not possible from a string route.
    /// </summary>
    /// <typeparam name="TResult">The expected return type of the resolved navigable.</typeparam>
    /// <param name="ct">Optional cancellation token. Merged with the one passed to NavigateTo.</param>
    public async Task<NavigationResult<TResult>> WithResult<TResult>(
        CancellationToken ct = default)
    {
        var linked = ct == default ? _ct : ct;
        var raw = await _factory(typeof(TResult), linked);

        return raw switch
        {
            NavigationSuccess<object?> s when s.Value is TResult typed
                => new NavigationSuccess<TResult>(typed),

            NavigationSuccess<object?> s
                => throw new InvalidCastException(
                    $"Navigation returned '{s.Value?.GetType().Name ?? "null"}' " +
                    $"but '{typeof(TResult).Name}' was expected."),

            NavigationDenied => new NavigationDenied(),
            NavigationCancelled => new NavigationCancelled(),
            _ => new NavigationCancelled()
        };
    }
}

/// <summary>
/// Returned by typed NavigateTo overloads where TResult is known at compile time.
/// Directly awaitable — no .WithResult() call needed.
/// </summary>
public sealed class NavigationOperation<TResult>
{
    private readonly Task<NavigationResult<TResult>> _task;

    internal NavigationOperation(Task<NavigationResult<TResult>> task)
    {
        _task = task;
    }

    public TaskAwaiter<NavigationResult<TResult>> GetAwaiter()
        => _task.GetAwaiter();

    public ConfiguredTaskAwaitable<NavigationResult<TResult>> ConfigureAwait(bool continueOnCapturedContext)
        => _task.ConfigureAwait(continueOnCapturedContext);
}
