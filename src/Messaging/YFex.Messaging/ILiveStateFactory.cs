namespace YFex.Messaging;

/// <summary>
/// Creates <see cref="ILiveState{T}"/> instances. The default implementation
/// uses a simple task-based approach. Add <c>YFex.Messaging.Fusion</c> to
/// replace this with a Stl.Fusion-backed reactive cache.
/// </summary>
public interface ILiveStateFactory
{
    ILiveState<T> Create<T>(Func<CancellationToken, Task<T>> computation, LiveStateOptions options = default);
}
