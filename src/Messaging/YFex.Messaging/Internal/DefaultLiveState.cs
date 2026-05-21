namespace YFex.Messaging.Internal;

/// <summary>
/// Task-based live state with no caching. Re-runs the computation on every
/// <see cref="RecomputeAsync"/> call and on each polling tick.
/// When <c>YFex.Messaging.Fusion</c> is added, this is replaced by a
/// Fusion-backed implementation that caches and invalidates correctly.
/// </summary>
internal sealed class DefaultLiveState<T> : ILiveState<T>
{
    private readonly Func<CancellationToken, Task<T>> _compute;
    private readonly int _pollMs;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public T? Value { get; private set; }
    public bool IsLoading { get; private set; }
    public Exception? Error { get; private set; }
    public event Action<ILiveState<T>>? Updated;

    public DefaultLiveState(Func<CancellationToken, Task<T>> compute, LiveStateOptions options)
    {
        _compute = compute;
        _pollMs  = options.PollMs;

        // Yield before the first fetch so subscribers have a chance to attach before
        // Updated fires — avoids a race when the computation completes synchronously.
        _ = InitialFetchAsync(_cts.Token);

        if (_pollMs > 0)
            _ = PollLoopAsync(_cts.Token);
    }

    private async Task InitialFetchAsync(CancellationToken ct)
    {
        await Task.Yield();
        await RecomputeAsync(ct).ConfigureAwait(false);
    }

    public async Task RecomputeAsync(CancellationToken ct = default)
    {
        if (_disposed) return;

        IsLoading = true;
        // Note: Updated fires once — after the fetch completes — so consumers see
        // a consistent state (Value / Error / IsLoading) in a single notification.
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            Value = await _compute(linked.Token).ConfigureAwait(false);
            Error = null;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return; // disposed — don't fire Updated
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        finally
        {
            IsLoading = false;
        }

        if (!_disposed) Updated?.Invoke(this);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
                await RecomputeAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
