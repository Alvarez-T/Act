namespace YFex.Messaging;

/// <summary>
/// A reactive value produced by a <see cref="ILiveStateFactory"/>.
/// Backed by either the built-in task-based implementation or a Stl.Fusion
/// computed state when <c>YFex.Messaging.Fusion</c> is wired up.
/// </summary>
public interface ILiveState<T> : IDisposable
{
    /// <summary>Current value. Returns <c>default(T)</c> while first fetch is in progress.</summary>
    T? Value { get; }

    /// <summary>True while a fetch is executing.</summary>
    bool IsLoading { get; }

    /// <summary>Last exception thrown by the fetch method. Null when succeeded.</summary>
    Exception? Error { get; }

    /// <summary>Triggers a fresh fetch regardless of current state.</summary>
    Task RecomputeAsync(CancellationToken ct = default);

    /// <summary>Fires after every completed fetch (successful or failed).</summary>
    event Action<ILiveState<T>> Updated;
}

/// <summary>Options forwarded to the live-state implementation.</summary>
public readonly struct LiveStateOptions
{
    /// <summary>
    /// Polling interval in milliseconds. Zero means no polling — rely on
    /// explicit <see cref="ILiveState{T}.RecomputeAsync"/> or dependency subscriptions.
    /// </summary>
    public int PollMs { get; init; }

    /// <summary>When true, polling continues even when the host is suspended.</summary>
    public bool PollDuringSuspend { get; init; }

    /// <summary>
    /// Cache tier selected by <c>[Live(Cache = ...)]</c>.
    /// The factory uses this to decide where to cache and how to deliver invalidations.
    /// </summary>
    public LiveCache Cache { get; init; }
}
