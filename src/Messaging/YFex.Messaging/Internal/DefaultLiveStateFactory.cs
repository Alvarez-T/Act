namespace YFex.Messaging;

/// <summary>
/// Task-based <see cref="ILiveStateFactory"/> that recomputes the computation
/// function directly without any caching layer. Suitable for testing and for
/// scenarios where Fusion is not wired up.
/// </summary>
public sealed class DefaultLiveStateFactory : ILiveStateFactory
{
    public ILiveState<T> Create<T>(Func<CancellationToken, Task<T>> computation, LiveStateOptions options = default)
        => new Internal.DefaultLiveState<T>(computation, options);
}
