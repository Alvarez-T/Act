using ActualLab.Fusion;
using YFex.Messaging;
using FusionStateBase = ActualLab.Fusion.State; // alias: 'State' class conflicts with 'YFex.State' namespace

namespace YFex.Messaging.Fusion;

/// <summary>
/// Wraps an ActualLab.Fusion <see cref="ComputedState{T}"/> and exposes it as an
/// <see cref="ILiveState{T}"/> so generated <c>[Live]</c> code works with Fusion's
/// reactive cache without importing ActualLab.Fusion into the ViewModel layer.
/// </summary>
internal sealed class FusionLiveState<T> : ILiveState<T>
{
    private readonly ComputedState<T> _state;
    private readonly SynchronizationContext? _syncContext;
    private bool _disposed;

    public event Action<ILiveState<T>>? Updated;

    public FusionLiveState(ComputedState<T> state, SynchronizationContext? syncContext)
    {
        _state       = state;
        _syncContext = syncContext;
        _state.Updated += OnFusionUpdated;
    }

    // LastNonErrorValue is T (typed), safe even when Error is set (returns last good value).
    // Returns default(T) when there is no value yet (e.g. during first fetch).
    public T? Value => _state.HasValue ? _state.LastNonErrorValue : default;

    // Fusion marks the state inconsistent during the update cycle (fetch in flight).
    public bool IsLoading => _state.HasValue && _state.Error is null
        ? !_state.Computed.IsConsistent()
        : !_state.HasValue;

    public Exception? Error => _state.Error;

    public Task RecomputeAsync(CancellationToken ct = default)
    {
        // Invalidating the underlying computed triggers the state's UpdateCycle to re-fetch.
        _state.GetExistingComputed()?.Invalidate();
        return Task.CompletedTask;
    }

    private void OnFusionUpdated(FusionStateBase s, StateEventKind kind)
    {
        if (_disposed) return;

        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
            NotifyUpdated();
        else
            _syncContext.Post(static obj => ((FusionLiveState<T>)obj!).NotifyUpdated(), this);
    }

    private void NotifyUpdated() => Updated?.Invoke(this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Updated -= OnFusionUpdated;
        _state.Dispose();
    }
}
