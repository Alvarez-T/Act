namespace YFex.Messaging;

/// <summary>
/// Static locator for the application-scoped <see cref="IEventBus"/>.
/// Call <see cref="Configure"/> once at startup (via <c>AddYFexMessaging()</c>)
/// before any publish or subscribe calls.
/// </summary>
public static class EventBusProvider
{
    private static IEventBus? _instance;

    /// <summary>
    /// The configured event bus. Throws if <see cref="Configure"/> has not been called.
    /// </summary>
    public static IEventBus Current => _instance
        ?? throw new InvalidOperationException(
            "YFex.Messaging is not configured. Call AddYFexMessaging() in your DI setup.");

    /// <summary>
    /// Registers the bus instance used by all static facades.
    /// Called automatically by <c>AddYFexMessaging()</c>.
    /// </summary>
    public static void Configure(IEventBus bus) => _instance = bus;
}
