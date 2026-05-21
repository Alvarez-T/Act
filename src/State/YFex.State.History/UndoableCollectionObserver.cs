using System;
using YFex.State.Collections;
using YFex.State.Notification;

namespace YFex.State.History;

/// <summary>
/// Subscribes to a <see cref="StateList{T}"/> and records collection mutations into an
/// <see cref="UndoContext"/> as full before/after snapshots. Maintains a shadow copy of the
/// list to capture the pre-mutation state (since <see cref="StateList{T}"/> only fires
/// post-change notifications).
/// <para>
/// Undo and redo restore the full list state via <c>Clear + AddRange</c>. This is O(N) per
/// undo step but correct for all mutation types including Remove without Insert support.
/// </para>
/// </summary>
public sealed class UndoableCollectionObserver<T> : IChangedHandler, IDisposable
{
    private readonly UndoContext     _ctx;
    private readonly StateList<T>    _list;
    private readonly string          _propertyName;
    private          T[]             _shadow;
    private          bool            _disposed;

    /// <summary>
    /// Creates the observer and immediately subscribes to <paramref name="list"/>.
    /// </summary>
    public UndoableCollectionObserver(
        UndoContext ctx,
        StateList<T> list,
        string propertyName = "")
    {
        _ctx          = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _list         = list ?? throw new ArgumentNullException(nameof(list));
        _propertyName = propertyName;
        _shadow       = list.AsSpan().ToArray();
        list.Subscribe(this);
    }

    /// <inheritdoc/>
    public void OnChanged(object source, in ChangedNotification notification)
    {
        if (_ctx.IsReplaying || _ctx.IsSuspended) return;

        // _shadow is the pre-mutation state; capture current list as post-mutation state
        T[] before = _shadow;
        T[] after  = _list.AsSpan().ToArray();

        // Update shadow for next notification
        _shadow = after;

        _ctx.RecordCollectionChange(_list, notification.Kind, before, after, _propertyName);
    }

    /// <summary>Unsubscribes from the list.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _list.Unsubscribe(this);
    }
}
