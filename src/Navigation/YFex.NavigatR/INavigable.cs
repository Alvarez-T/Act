namespace YFex.NavigatR;

/// <summary>
/// Base interface for all navigable ViewModels.
/// Use when the screen requires no parameter and produces no result.
/// </summary>
public interface INavigable : IDisposable
{
    /// <summary>
    /// Called once when the screen is first navigated to.
    /// Call <see cref="NavigationContext.Deny"/> to block navigation.
    /// When a <c>Parameter</c> is declared on <c>[Route]</c>, the source generator
    /// implements this explicitly and enforces a typed
    /// <c>OnNavigation(TParameter, CancellationToken)</c> partial method instead.
    /// </summary>
    Task OnNavigation(NavigationContext context, CancellationToken ct = default);

    /// <summary>
    /// Called when the screen comes back into focus after being suspended.
    /// Never called when returning from an awaited NavigateTo — the ViewModel
    /// was Pinned and never truly left.
    /// </summary>
    Task OnResume(CancellationToken ct = default);

    /// <summary>
    /// Called when the screen loses focus without being closed.
    /// Never called when the ViewModel is Pinned awaiting a child NavigateTo result.
    /// Also called by the Navigator when AFK is detected — state does not change.
    /// </summary>
    Task OnSuspend(CancellationToken ct = default);

    /// <summary>
    /// Called by the Navigator when prefetching this ViewModel before navigation.
    /// Runs off the UI thread. Developer never implements this directly —
    /// the source generator bridges it from <c>[Prefetch]</c>-marked methods.
    /// Default implementation is a no-op.
    /// </summary>
    Task OnPrefetch(NavigationContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called by the Navigator when the user returns from AFK.
    /// State does not change — the ViewModel remains Active.
    /// Default implementation is a no-op.
    /// </summary>
    Task OnActive(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A navigable ViewModel that produces a typed result.
/// The source generator implements <c>Returns(TResult)</c>, <c>Cancel()</c>,
/// and <c>Deny(string?)</c> automatically on the partial class.
/// </summary>
public interface INavigable<TResult> : INavigable
{
    /// <summary>
    /// Awaited by the Navigator to receive the result.
    /// Generated automatically — do not call directly.
    /// </summary>
    Task<NavigationResult<TResult>> WaitForResultAsync();
}

/// <summary>
/// Internal interface used by the generator to wire parameterized navigation.
/// Not part of the developer-facing API.
/// </summary>
internal interface INavigableAccepts<TParameter> : INavigable
{
}