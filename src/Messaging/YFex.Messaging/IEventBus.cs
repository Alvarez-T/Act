namespace YFex.Messaging;

/// <summary>
/// In-process event bus. Singleton at application scope.
/// Supports broadcast, targeted delivery, and group fan-out.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribe a synchronous recipient to events of type <typeparamref name="T"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    IDisposable Subscribe<T>(IEventRecipient<T> recipient, SubscribeOptions options = default);

    /// <summary>
    /// Subscribe an asynchronous recipient to events of type <typeparamref name="T"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    IDisposable SubscribeAsync<T>(IAsyncEventRecipient<T> recipient, SubscribeOptions options = default);

    /// <summary>
    /// Publish an event synchronously. Sync recipients are called inline.
    /// Async recipients are invoked fire-and-forget.
    /// </summary>
    void Publish<T>(in T @event, PublishOptions options = default);

    /// <summary>
    /// Publish an event and await all async recipients sequentially.
    /// Sync recipients are called inline before async recipients are awaited.
    /// </summary>
    ValueTask PublishAsync<T>(T @event, PublishOptions options = default, CancellationToken ct = default);
}
