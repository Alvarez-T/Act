using ActualLab.Fusion;
using YFex.Messaging;

namespace YFex.Messaging.Fusion;

/// <summary>
/// <see cref="ILiveStateFactory"/> implementation backed by ActualLab.Fusion.
/// Registered by <see cref="FusionMessagingExtensions.AddYFexFusion"/>.
///
/// Override <see cref="GetLoadFromCache{T}"/> / <see cref="GetSaveToCache{T}"/> in a subclass
/// (e.g. <c>RpcFusionLiveStateFactory</c> in <c>YFex.Messaging.Rpc</c>) to add persistence.
/// </summary>
public class FusionLiveStateFactory : ILiveStateFactory
{
    private readonly StateFactory _stateFactory;

    public FusionLiveStateFactory(StateFactory stateFactory)
        => _stateFactory = stateFactory;

    public ILiveState<T> Create<T>(
        Func<CancellationToken, Task<T>> computation,
        LiveStateOptions options = default)
    {
        return new FusionLiveState<T>(
            _stateFactory,
            computation,
            SynchronizationContext.Current,
            options.StaleTimeMs,
            GetLoadFromCache<T>(options),
            GetSaveToCache<T>(options));
    }

    /// <summary>
    /// Override to supply an offline cache-load delegate for a given state.
    /// Default returns <c>null</c> (no persistence).
    /// </summary>
    protected virtual Func<CancellationToken, ValueTask<T?>>? GetLoadFromCache<T>(LiveStateOptions options) => null;

    /// <summary>
    /// Override to supply a cache-write delegate after a successful fetch.
    /// Default returns <c>null</c> (no persistence).
    /// </summary>
    protected virtual Func<T, CancellationToken, ValueTask>? GetSaveToCache<T>(LiveStateOptions options) => null;
}
