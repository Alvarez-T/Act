namespace YFex.Messaging;

/// <summary>
/// Options for subscribing to events on the bus.
/// </summary>
public readonly struct SubscribeOptions
{
    /// <summary>
    /// When set, only events published to this specific target id will be received.
    /// Used for point-to-point delivery (e.g. session-specific messages).
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// When set, only events published to this group id will be received.
    /// Used for group fan-out (e.g. chat rooms, tenants).
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// When false (default), the bus holds a weak reference to the recipient.
    /// The recipient can be garbage-collected without explicit unsubscription.
    /// When true, the bus pins the recipient in memory until disposed.
    /// </summary>
    public bool KeepAlive { get; init; }
}

/// <summary>
/// Options for publishing events to the bus.
/// </summary>
public readonly struct PublishOptions
{
    /// <summary>
    /// Deliver only to subscribers that registered with this target id.
    /// Null means broadcast to all (no target filter).
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// Deliver only to subscribers that registered with this group id.
    /// Null means broadcast to all (no group filter).
    /// </summary>
    public string? GroupId { get; init; }
}
