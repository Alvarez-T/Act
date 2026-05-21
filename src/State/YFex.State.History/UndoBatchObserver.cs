using YFex.State;
using YFex.State.Notification;

namespace YFex.State.History;

/// <summary>
/// Bridges <see cref="StateObject.BeginUpdate"/> batch boundaries into
/// <see cref="UndoContext"/> group boundaries. Subscribe an instance of this class to a
/// <see cref="StateObject"/> to ensure that all property changes within one
/// <c>BeginUpdate</c> scope are collapsed into a single undo step.
/// <para>
/// Subscribes itself to the owner on construction; no activation lifecycle is required.
/// </para>
/// </summary>
public sealed class UndoBatchObserver : IChangedHandler
{
    private readonly UndoContext[] _contexts;

    /// <summary>
    /// Creates the observer and immediately subscribes to <paramref name="owner"/>.
    /// </summary>
    public UndoBatchObserver(StateObject owner, UndoContext[] contexts)
    {
        _contexts = contexts;
        owner.Subscribe(this);
    }

    /// <inheritdoc/>
    public void OnChanged(object source, in ChangedNotification notification) { }

    /// <inheritdoc/>
    public void OnBatchFlushStarting(object source)
    {
        foreach (var ctx in _contexts) ctx.BeginGroup();
    }

    /// <inheritdoc/>
    public void OnBatchFlushCompleted(object source)
    {
        foreach (var ctx in _contexts) ctx.EndGroup();
    }
}
