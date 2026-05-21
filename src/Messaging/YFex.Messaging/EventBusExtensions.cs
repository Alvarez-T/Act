using YFex.Messaging.Internal;

namespace YFex.Messaging;

/// <summary>
/// Extension methods on <see cref="IEventBus"/> for ergonomic delegate-based registration.
/// </summary>
public static class EventBusExtensions
{
    /// <summary>
    /// Subscribe a delegate handler to events of type <typeparamref name="T"/>.
    /// By default the registration is strong (KeepAlive), matching app-wide handler semantics.
    /// </summary>
    public static IDisposable On<T>(
        this IEventBus bus,
        Action<T> handler,
        SubscribeOptions options = default)
    {
        var adapter = new DelegateAdapter<T>(handler);
        return bus.Subscribe(adapter, options);
    }
}
