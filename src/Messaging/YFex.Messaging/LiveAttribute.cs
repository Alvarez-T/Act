namespace YFex.Messaging;

/// <summary>
/// Marks a method as the data source for a live reactive property.
/// The source generator emits: the property itself, <c>IsXLoading</c>,
/// <c>XError</c>, <c>RefreshXAsync()</c>, and lifecycle wiring
/// into <c>OnActivateCascading</c> / <c>OnDeactivateCascading</c>.
/// </summary>
/// <remarks>
/// Method signature must return <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>
/// and take a <c>CancellationToken</c> as last parameter:
/// <code>
/// [Live]
/// private Task&lt;int&gt; TotalCustomersAsync(CancellationToken ct)
///     => Customer.Queries.GetCount(ct);
/// </code>
/// The generated property name is derived from the method name by
/// stripping the "Async" suffix (e.g. <c>TotalCustomersAsync</c> → <c>TotalCustomers</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveAttribute : Attribute
{
    /// <summary>
    /// Polling interval in milliseconds. Zero (default) means no polling —
    /// the property only refreshes when explicitly invalidated or
    /// <c>RefreshXAsync()</c> is called.
    /// </summary>
    public int PollMs { get; init; }

    /// <summary>
    /// When true, polling continues even while the host page is suspended
    /// (back-stack). Default false pauses polling on suspend.
    /// </summary>
    public bool PollDuringSuspend { get; init; }

    /// <summary>
    /// Suspend behaviour for <see cref="PageViewModel"/>-hosted live properties.
    /// </summary>
    public LiveSuspendBehavior SuspendBehavior { get; init; } = LiveSuspendBehavior.PauseAndRefreshOnResume;

    /// <summary>
    /// Where the computed value is cached and how invalidation is delivered.
    /// Defaults to <see cref="LiveCache.Local"/> (in-process Fusion cache).
    /// Set to <see cref="LiveCache.ServerShared"/> when the backing computation
    /// is a Fusion compute-service method on the server.
    /// </summary>
    public LiveCache Cache { get; init; } = LiveCache.Local;

    /// <summary>
    /// Explicit list of <c>[Observable]</c> property names on the host class whose
    /// changes should trigger an automatic re-fetch. Use <c>nameof()</c> for
    /// compile-time safety.
    /// <example>
    /// <code>
    /// [Live(DependsOn = [nameof(Filter), nameof(Page)])]
    /// private Task&lt;List&lt;Customer&gt;&gt; SearchAsync(CancellationToken ct) => ...;
    /// </code>
    /// </example>
    /// When not set and <c>PollMs</c> is 0, the generator emits a YFLIV0002 info
    /// diagnostic to remind you that the property will only refresh manually.
    /// </summary>
    public string[]? DependsOn { get; init; }
}

/// <summary>Controls what happens to a <c>[Live]</c> property when the host page is suspended.</summary>
public enum LiveSuspendBehavior
{
    /// <summary>Stop receiving updates while suspended; re-fetch on resume if stale.</summary>
    PauseAndRefreshOnResume,
    /// <summary>Keep the subscription active even on the back stack.</summary>
    StayLive,
    /// <summary>Force a fresh fetch on every resume regardless of staleness.</summary>
    AlwaysRefetchOnResume,
    /// <summary>Freeze the cached value; no auto-refresh.</summary>
    FreezeOnSuspend,
}
