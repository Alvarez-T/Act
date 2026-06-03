using ActualLab.Fusion;
using YFex.Messaging;
using FusionStateBase = ActualLab.Fusion.State; // alias: 'State' conflicts with YFex.State namespace

namespace YFex.Messaging.Fusion;

/// <summary>
/// Wraps an ActualLab.Fusion <see cref="ComputedState{T}"/> and exposes it as an
/// <see cref="ILiveState{T}"/> so generated <c>[Live]</c> code works with Fusion's reactive
/// cache without importing ActualLab.Fusion into the ViewModel layer.
///
/// Supports optional cache persistence delegates injected by <c>YFex.Messaging.Rpc</c>:
/// <list type="bullet">
///   <item><c>loadFromCache</c> — called when the computation fails, to serve a cached value offline.</item>
///   <item><c>saveToCache</c>  — called after every successful fetch to persist the result.</item>
/// </list>
/// </summary>
internal sealed class FusionLiveState<T> : ILiveState<T>
{
    private readonly ComputedState<T> _state;
    private readonly SynchronizationContext? _syncContext;
    private readonly int _staleAfterMs;
    private volatile bool _isFromOfflineCache;
    private DateTimeOffset? _lastFetchedAt;
    private bool _disposed;

    public event Action<ILiveState<T>>? Updated;

    /// <param name="stateFactory">Fusion state factory used to create the underlying computed state.</param>
    /// <param name="computation">The async computation that produces the live value.</param>
    /// <param name="syncContext">SynchronizationContext used to marshal Updated events to the UI thread.</param>
    /// <param name="staleAfterMs">
    ///   Milliseconds after the last successful fetch before <see cref="IsStale"/> returns true.
    ///   Zero means never stale (time-based).
    /// </param>
    /// <param name="loadFromCache">
    ///   Optional: called when the computation throws to serve a cached value.
    ///   Return <c>null</c> to re-throw.
    /// </param>
    /// <param name="saveToCache">
    ///   Optional: called after every successful fetch to persist the value.
    /// </param>
    public FusionLiveState(
        StateFactory stateFactory,
        Func<CancellationToken, Task<T>> computation,
        SynchronizationContext? syncContext,
        int staleAfterMs = 0,
        Func<CancellationToken, ValueTask<T?>>? loadFromCache = null,
        Func<T, CancellationToken, ValueTask>? saveToCache = null)
    {
        _syncContext = syncContext;
        _staleAfterMs = staleAfterMs;

        // Wrap the computation so we can track LastFetchedAt and cache I/O.
        Func<CancellationToken, Task<T>> wrappedComputation;
        if (loadFromCache is not null || saveToCache is not null)
        {
            wrappedComputation = async ct =>
            {
                try
                {
                    var result = await computation(ct).ConfigureAwait(false);
                    _lastFetchedAt = DateTimeOffset.UtcNow;
                    _isFromOfflineCache = false;
                    if (saveToCache is not null)
                        await saveToCache(result, ct).ConfigureAwait(false);
                    return result;
                }
                catch when (loadFromCache is not null)
                {
                    var cached = await loadFromCache(ct).ConfigureAwait(false);
                    if (cached is not null)
                    {
                        _isFromOfflineCache = true;
                        return cached;
                    }
                    throw;
                }
            };
        }
        else
        {
            wrappedComputation = async ct =>
            {
                var result = await computation(ct).ConfigureAwait(false);
                _lastFetchedAt = DateTimeOffset.UtcNow;
                _isFromOfflineCache = false;
                return result;
            };
        }

        _state = stateFactory.NewComputed<T>(wrappedComputation);
        _state.Updated += OnFusionUpdated;
    }

    // ── ILiveState<T> ─────────────────────────────────────────────────────────

    // LastNonErrorValue is T (typed), safe even when Error is set (returns last good value).
    public T? Value => _state.HasValue ? _state.LastNonErrorValue : default;

    // IsLoading: true while a computation is in flight.
    public bool IsLoading => _state.HasValue && _state.Error is null
        ? !_state.Computed.IsConsistent()
        : !_state.HasValue;

    public Exception? Error => _state.Error;

    public DateTimeOffset? LastFetchedAt => _lastFetchedAt;

    public bool IsStale
    {
        get
        {
            if (_lastFetchedAt is null) return true;
            if (Error is not null) return true;
            if (_staleAfterMs <= 0) return false;
            return (DateTimeOffset.UtcNow - _lastFetchedAt.Value).TotalMilliseconds > _staleAfterMs;
        }
    }

    public bool IsFromOfflineCache => _isFromOfflineCache;

    public Task RecomputeAsync(CancellationToken ct = default)
    {
        // Invalidate the underlying computed → triggers the state's UpdateCycle to re-fetch.
        _state.GetExistingComputed()?.Invalidate();
        return Task.CompletedTask;
    }

    // ── Fusion internal ───────────────────────────────────────────────────────

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
