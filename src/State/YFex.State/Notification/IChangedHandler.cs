namespace YFex.State.Notification;

/// <summary>
/// Receives property-change notifications from a <see cref="INotifyChanged"/> source.
/// Interface-based (not delegate-based) to avoid multicast-delegate allocation on every subscribe.
/// </summary>
public interface IChangedHandler
{
    /// <summary>Fired after a property value has been updated.</summary>
    void OnChanged(object source, in ChangedNotification notification);

    /// <summary>
    /// Fired before a property value is updated. Always fires synchronously at mutation time —
    /// never deferred by <see cref="StateObject.BeginUpdate"/>. Default no-op.
    /// </summary>
    void OnChanging(object source, in ChangedNotification notification) { }

    /// <summary>
    /// Called once at the start of a batch flush, before any per-property <see cref="OnChanged"/> calls
    /// in the same drain. Lets handlers coalesce work across the batch (e.g. one Post per flush).
    /// Default no-op.
    /// </summary>
    void OnBatchFlushStarting(object source) { }

    /// <summary>
    /// Called once at the end of a batch flush, after all per-property <see cref="OnChanged"/> calls
    /// in the drain. Default no-op.
    /// </summary>
    void OnBatchFlushCompleted(object source) { }
}
