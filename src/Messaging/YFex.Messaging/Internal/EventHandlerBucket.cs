namespace YFex.Messaging.Internal;

/// <summary>
/// Per-type handler storage. Holds weak (default) and strong (KeepAlive) references to
/// registered sync and async recipients. Thread-safe via snapshot-on-read.
/// </summary>
internal sealed class EventHandlerBucket<T>
{
    private readonly List<SyncEntry>  _sync  = new(2);
    private readonly List<AsyncEntry> _async = new(2);
    private readonly object _lock = new();

    private readonly record struct SyncEntry(
        WeakReference<IEventRecipient<T>> Weak,
        IEventRecipient<T>?               Strong,   // non-null when KeepAlive = true
        SubscribeOptions                  Options);

    private readonly record struct AsyncEntry(
        WeakReference<IAsyncEventRecipient<T>> Weak,
        IAsyncEventRecipient<T>?               Strong,
        SubscribeOptions                        Options);

    public IDisposable Subscribe(IEventRecipient<T> recipient, SubscribeOptions options)
    {
        var weak = new WeakReference<IEventRecipient<T>>(recipient);
        lock (_lock)
            _sync.Add(new(weak, options.KeepAlive ? recipient : null, options));
        return new DisposeAction(() => RemoveSync(weak));
    }

    public IDisposable SubscribeAsync(IAsyncEventRecipient<T> recipient, SubscribeOptions options)
    {
        var weak = new WeakReference<IAsyncEventRecipient<T>>(recipient);
        lock (_lock)
            _async.Add(new(weak, options.KeepAlive ? recipient : null, options));
        return new DisposeAction(() => RemoveAsync(weak));
    }

    public void Publish(in T @event, PublishOptions options)
    {
        SyncEntry[]  syncSnapshot;
        AsyncEntry[] asyncSnapshot;
        lock (_lock)
        {
            syncSnapshot  = _sync.ToArray();
            asyncSnapshot = _async.ToArray();
        }

        bool hasDead = false;

        foreach (var entry in syncSnapshot)
        {
            if (!entry.Weak.TryGetTarget(out var r)) { hasDead = true; continue; }
            if (!Matches(options, entry.Options)) continue;
            r.Receive(in @event);
        }

        foreach (var entry in asyncSnapshot)
        {
            if (!entry.Weak.TryGetTarget(out var r)) { hasDead = true; continue; }
            if (!Matches(options, entry.Options)) continue;
            _ = r.ReceiveAsync(@event, CancellationToken.None);
        }

        if (hasDead) Compact();
    }

    public async ValueTask PublishAsync(T @event, PublishOptions options, CancellationToken ct)
    {
        SyncEntry[]  syncSnapshot;
        AsyncEntry[] asyncSnapshot;
        lock (_lock)
        {
            syncSnapshot  = _sync.ToArray();
            asyncSnapshot = _async.ToArray();
        }

        bool hasDead = false;

        foreach (var entry in syncSnapshot)
        {
            if (!entry.Weak.TryGetTarget(out var r)) { hasDead = true; continue; }
            if (!Matches(options, entry.Options)) continue;
            r.Receive(in @event);
        }

        foreach (var entry in asyncSnapshot)
        {
            if (!entry.Weak.TryGetTarget(out var r)) { hasDead = true; continue; }
            if (!Matches(options, entry.Options)) continue;
            await r.ReceiveAsync(@event, ct).ConfigureAwait(false);
        }

        if (hasDead) Compact();
    }

    // Publish/subscribe options match when:
    //   • The publish has no target/group filter (broadcast), OR
    //   • The subscribe registered for exactly that target/group
    private static bool Matches(PublishOptions pub, SubscribeOptions sub) =>
        (pub.TargetId is null || pub.TargetId == sub.TargetId) &&
        (pub.GroupId  is null || pub.GroupId  == sub.GroupId);

    private void Compact()
    {
        lock (_lock)
        {
            _sync.RemoveAll(e  => !e.Weak.TryGetTarget(out _));
            _async.RemoveAll(e => !e.Weak.TryGetTarget(out _));
        }
    }

    private void RemoveSync(WeakReference<IEventRecipient<T>> weak)
    {
        lock (_lock)
            _sync.RemoveAll(e => ReferenceEquals(e.Weak, weak));
    }

    private void RemoveAsync(WeakReference<IAsyncEventRecipient<T>> weak)
    {
        lock (_lock)
            _async.RemoveAll(e => ReferenceEquals(e.Weak, weak));
    }
}
