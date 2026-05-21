using System.Collections.Concurrent;
using YFex.Messaging.Internal;

namespace YFex.Messaging;

/// <summary>
/// Default in-process implementation of <see cref="IEventBus"/>.
/// Type-keyed buckets of handlers; lock-free reads via snapshot, locked writes.
/// </summary>
public sealed class DefaultEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, object> _buckets = new();

    private EventHandlerBucket<T> GetOrCreateBucket<T>()
    {
        var type = typeof(T);
        if (_buckets.TryGetValue(type, out var existing))
            return (EventHandlerBucket<T>)existing;
        var bucket = new EventHandlerBucket<T>();
        // GetOrAdd(key, value) returns the winner on race — discard the loser
        return (EventHandlerBucket<T>)_buckets.GetOrAdd(type, bucket);
    }

    public IDisposable Subscribe<T>(IEventRecipient<T> recipient, SubscribeOptions options = default)
        => GetOrCreateBucket<T>().Subscribe(recipient, options);

    public IDisposable SubscribeAsync<T>(IAsyncEventRecipient<T> recipient, SubscribeOptions options = default)
        => GetOrCreateBucket<T>().SubscribeAsync(recipient, options);

    public void Publish<T>(in T @event, PublishOptions options = default)
    {
        if (_buckets.TryGetValue(typeof(T), out var bucket))
            ((EventHandlerBucket<T>)bucket).Publish(in @event, options);
    }

    public ValueTask PublishAsync<T>(T @event, PublishOptions options = default, CancellationToken ct = default)
    {
        if (_buckets.TryGetValue(typeof(T), out var bucket))
            return ((EventHandlerBucket<T>)bucket).PublishAsync(@event, options, ct);
        return ValueTask.CompletedTask;
    }
}
