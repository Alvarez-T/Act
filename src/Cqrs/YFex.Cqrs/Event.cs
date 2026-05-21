using YFex.Cqrs;
using YFex.Messaging;

namespace YFex.Cqrs;

/// <summary>
/// Static facade for publishing and subscribing to domain events.
/// Mirrors the <see cref="Command"/> and <see cref="Query"/> facades.
/// Backed by the ambient <see cref="EventBusProvider.Current"/> instance.
/// </summary>
public static class Event
{
    /// <summary>
    /// Publishes an event to all in-process subscribers (fire-and-forget for async handlers).
    /// </summary>
    public static void Publish<T>(in T e) where T : IEvent
        => EventBusProvider.Current.Publish(in e);

    /// <summary>
    /// Publishes an event and awaits all async subscribers.
    /// </summary>
    public static Task PublishAsync<T>(T e) where T : IEvent
        => EventBusProvider.Current.PublishAsync(e).AsTask();

    /// <summary>
    /// Publishes an event only to the subscriber registered for the given target id.
    /// Used for point-to-point delivery (e.g. a specific session or user).
    /// </summary>
    public static Task PublishToAsync<T>(string targetId, T e) where T : IEvent
        => EventBusProvider.Current.PublishAsync(e, new PublishOptions { TargetId = targetId }).AsTask();

    /// <summary>
    /// Publishes an event to all subscribers registered for the given group id.
    /// </summary>
    public static Task PublishToGroupAsync<T>(string groupId, T e) where T : IEvent
        => EventBusProvider.Current.PublishAsync(e, new PublishOptions { GroupId = groupId }).AsTask();

    /// <summary>
    /// Subscribes an <see cref="IEventRecipient{T}"/> to events of type <typeparamref name="T"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    public static IDisposable Subscribe<T>(IEventRecipient<T> recipient, SubscribeOptions options = default) where T : IEvent
        => EventBusProvider.Current.Subscribe(recipient, options);

    /// <summary>
    /// Registers an app-wide delegate handler. Strong reference by default (permanent, never GC'd).
    /// Intended for startup-time cross-cutting registrations (analytics, cache invalidation, etc.).
    /// </summary>
    public static IDisposable On<T>(Action<T> handler) where T : IEvent
        => EventBusProvider.Current.On(handler, new SubscribeOptions { KeepAlive = true });

    /// <summary>Allows injecting a mock bus in tests.</summary>
    internal static void SetMock(IEventBus mockBus)
        => EventBusProvider.Configure(mockBus);
}
