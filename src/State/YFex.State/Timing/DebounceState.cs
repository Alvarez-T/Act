using System.Threading;

namespace YFex.State.Timing;

/// <summary>
/// Allocation-free debounce/throttle primitive.
/// After warmup (first invocation), <see cref="NextToken"/> reuses the same
/// <see cref="CancellationTokenSource"/> via <c>TryReset()</c> (.NET 6+) —
/// zero allocations per subsequent invocation.
/// </summary>
public struct DebounceState
{
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Cancels any in-flight token and returns a fresh token for the next debounced operation.
    /// </summary>
    public CancellationToken NextToken()
    {
        if (_cts is null)
        {
            _cts = new CancellationTokenSource();
        }
        else if (!_cts.TryReset())
        {
            // TryReset fails if the CTS was already cancelled or disposed.
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
        return _cts.Token;
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
