using System;
using YFex.State.Notification;

namespace YFex.State.Collections.Internal;

/// <summary>
/// Forwards <see cref="IChangedHandler"/> calls through a weak reference to the actual handler.
/// Used by <see cref="StateList{T}"/> to monitor <see cref="INotifyChanged"/> items without
/// creating a strong reference that would prevent GC of removed/unreferenced items.
/// When the target has been collected, the slot is marked dead and lazily reaped.
/// </summary>
internal sealed class WeakChangedHandler : IChangedHandler
{
    private readonly WeakReference<IChangedHandler> _target;

    internal bool IsAlive => _target.TryGetTarget(out _);

    internal WeakChangedHandler(IChangedHandler target) =>
        _target = new WeakReference<IChangedHandler>(target);

    public void OnChanged(object source, in ChangedNotification notification)
    {
        if (_target.TryGetTarget(out var handler))
            handler.OnChanged(source, in notification);
    }

    void IChangedHandler.OnChanging(object source, in ChangedNotification notification)
    {
        if (_target.TryGetTarget(out var handler))
            handler.OnChanging(source, in notification);
    }
}
