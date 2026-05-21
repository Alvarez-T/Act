using ActualLab.Fusion;
using YFex.State;
using YFex.State.Notification;
using FusionStateBase = ActualLab.Fusion.State; // alias: 'State' class conflicts with 'YFex.State' namespace

namespace YFex.Messaging.Fusion;

/// <summary>
/// Wraps a Fusion <see cref="IState{T}"/> and exposes it as <see cref="INotifyChanged"/>
/// + <see cref="IActivatable"/>. Declare as an <c>[Observable]</c> property on a
/// <c>StateObject</c> and the activation cascade handles lifecycle automatically
/// once <c>ObservableParser</c> recognises <c>IActivatable</c> (not only StateObject).
/// </summary>
public sealed class FusionStateBinding<T> : INotifyChanged, IActivatable, IDisposable
{
    private readonly IState<T> _state;
    private readonly SynchronizationContext? _syncContext;
    private readonly List<IChangedHandler> _handlers = new();
    private readonly object _handlersLock = new();
    private bool _isActive;

    private static readonly ChangedNotification s_descriptor = new()
    {
        PropertyName = "Value",
        PropertyId   = 0u,
    };

    public FusionStateBinding(IState<T> state, SynchronizationContext? syncContext = null)
    {
        _state       = state;
        _syncContext = syncContext ?? SynchronizationContext.Current;
    }

    /// <summary>Current value. Returns <c>default</c> when no value is available yet.</summary>
    public T? Value => _state.HasValue ? _state.LastNonErrorValue : default;

    public bool IsActive => _isActive;

    public void Subscribe(IChangedHandler handler)
    {
        lock (_handlersLock) _handlers.Add(handler);
    }

    public void Unsubscribe(IChangedHandler handler)
    {
        lock (_handlersLock) _handlers.Remove(handler);
    }

    public void Activate()
    {
        if (_isActive) return;
        _isActive = true;
        _state.Updated += OnFusionUpdated;
    }

    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        _state.Updated -= OnFusionUpdated;
    }

    public void Dispose() => Deactivate();

    private void OnFusionUpdated(FusionStateBase s, StateEventKind kind)
    {
        if (!_isActive) return;

        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
            NotifyHandlers();
        else
            _syncContext.Post(static obj => ((FusionStateBinding<T>)obj!).NotifyHandlers(), this);
    }

    private void NotifyHandlers()
    {
        IChangedHandler[] snapshot;
        lock (_handlersLock) snapshot = _handlers.ToArray();
        foreach (var h in snapshot)
            h.OnChanged(this, in s_descriptor);
    }
}
