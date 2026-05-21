using ActualLab.Fusion;
using YFex.Messaging;

namespace YFex.Messaging.Fusion;

/// <summary>
/// <see cref="ILiveStateFactory"/> implementation backed by ActualLab.Fusion.
/// Registered by <see cref="FusionMessagingExtensions.AddYFexFusion"/>.
/// </summary>
public sealed class FusionLiveStateFactory : ILiveStateFactory
{
    private readonly StateFactory _stateFactory;

    public FusionLiveStateFactory(StateFactory stateFactory)
        => _stateFactory = stateFactory;

    public ILiveState<T> Create<T>(
        Func<CancellationToken, Task<T>> computation,
        LiveStateOptions options = default)
    {
        var computedState = _stateFactory.NewComputed<T>(
            ct => computation(ct));

        return new FusionLiveState<T>(computedState, SynchronizationContext.Current);
    }
}
