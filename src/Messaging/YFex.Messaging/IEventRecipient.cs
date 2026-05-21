namespace YFex.Messaging;

/// <summary>
/// Typed in-process event recipient. Interface-based (no delegate) — zero-allocation on the
/// hot publish path. The 'in' modifier lets struct events pass by reference without copying.
/// </summary>
public interface IEventRecipient<T>
{
    void Receive(in T @event);
}

/// <summary>
/// Async variant for recipients that need to await work (e.g. save to DB, send HTTP request).
/// When <see cref="IEventBus.Publish{T}"/> is called synchronously, async recipients are
/// invoked fire-and-forget. Use <see cref="IEventBus.PublishAsync{T}"/> to await all handlers.
/// </summary>
public interface IAsyncEventRecipient<T>
{
    ValueTask ReceiveAsync(T @event, CancellationToken ct = default);
}
