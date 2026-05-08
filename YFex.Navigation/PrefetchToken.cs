namespace YFex.NavigatR;

/// <summary>
/// Returned by <see cref="Navigator.Prefetch"/> — links a prefetch operation
/// to a subsequent <see cref="Navigator.NavigateTo(PrefetchToken, CancellationToken)"/> call.
/// <para>
/// The token carries the route (with parameter) so the developer does not need to
/// repeat it at the NavigateTo call site.
/// </para>
/// <para>
/// If the token has expired or been cancelled when NavigateTo is called,
/// the Navigator falls back to fresh navigation automatically.
/// </para>
/// </summary>
public sealed class PrefetchToken
{
    private readonly CancellationTokenSource _cts;
    private readonly DateTimeOffset _expiresAt;

    internal IRoute Route { get; }
    internal NavigationEntry? PrefetchedEntry { get; set; }

    internal PrefetchToken(IRoute route, TimeSpan timeout)
    {
        Route = route;
        _cts = new CancellationTokenSource();
        _expiresAt = DateTimeOffset.UtcNow.Add(timeout);
    }

    /// <summary>True if the token has expired or been cancelled.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= _expiresAt || _cts.IsCancellationRequested;

    /// <summary>CancellationToken passed to OnPrefetch.</summary>
    internal CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Whether this token represents the same navigation as another route.
    /// Uses route record structural equality.
    /// </summary>
    internal bool IsSameRoute(IRoute other)
        => Route.GetType() == other.GetType() && Route.Equals(other);

    /// <summary>Cancels the prefetch operation.</summary>
    internal void Cancel()
    {
        _cts.Cancel();
        PrefetchedEntry = null;
    }

    public void Dispose() => _cts.Dispose();
}