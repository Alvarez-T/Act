namespace YFex.Messaging;

/// <summary>
/// Marks a method as a handler for in-process events of type <typeparamref name="T"/>.
/// The source generator emits a private adapter class and wires subscription/unsubscription
/// into <c>OnActivateCascading</c> / <c>OnDeactivateCascading</c>.
/// </summary>
/// <remarks>
/// Method signatures:
/// <list type="bullet">
///   <item>Sync:  <c>void Method(in TEvent e)</c></item>
///   <item>Async: <c>ValueTask Method(TEvent e, CancellationToken ct)</c></item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class SubscribeAttribute<T> : Attribute
{
    /// <summary>
    /// When true, the bus pins the subscription with a strong reference.
    /// Use for handlers that must survive the host's activation lifecycle.
    /// Default: false (weak reference — subscription auto-drops when host is GC'd).
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>
    /// Comma-separated property paths used to filter incoming events.
    /// Each path's last segment is matched against the same-named property on the event.
    /// Example: <c>FilterBy = "Model.Id"</c> — only fires when <c>event.Id == this.Model.Id</c>.
    /// Example: <c>FilterBy = "Model.OrderId,Model.LineId"</c> — both must match.
    /// </summary>
    public string? FilterBy { get; init; }

    /// <summary>
    /// Name of a property on this class whose runtime value is used as the target id filter.
    /// Only events published via <c>Event.PublishToAsync(targetId, ...)</c> with a matching
    /// value will be received. Pass the property name (e.g. <c>nameof(SessionId)</c>).
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Name of a property on this class whose runtime value is used as the group id filter.
    /// Only events published via <c>Event.PublishToGroupAsync(groupId, ...)</c> with a
    /// matching value will be received. Pass the property name (e.g. <c>nameof(RoomId)</c>).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Debounce interval in milliseconds. Intermediate events within the window are dropped;
    /// the handler fires once after <c>DebounceMs</c> ms of silence.
    /// Useful for fast event streams such as search-box keystrokes.
    /// Mutually exclusive with <see cref="ThrottleMs"/>.
    /// </summary>
    public int DebounceMs { get; init; }

    /// <summary>
    /// Throttle interval in milliseconds. The handler fires immediately on the first event,
    /// then ignores subsequent events for <c>ThrottleMs</c> ms.
    /// Useful when you want instant feedback but want to rate-limit downstream work.
    /// Mutually exclusive with <see cref="DebounceMs"/>.
    /// </summary>
    public int ThrottleMs { get; init; }
}
