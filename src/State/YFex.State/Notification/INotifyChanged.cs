namespace YFex.State.Notification;

/// <summary>
/// Represents a source that can notify subscribers when a property value changes.
/// </summary>
public interface INotifyChanged
{
    void Subscribe(IChangedHandler handler);
    void Unsubscribe(IChangedHandler handler);
}
