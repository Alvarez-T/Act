using System;
using System.Threading;
using System.Threading.Tasks;

namespace YFex.State.Timing;

/// <summary>
/// Wraps <see cref="PeriodicTimer"/> (.NET 8+) with <see cref="TimeProvider"/> support.
/// When poll work takes longer than the interval, the timer waits for the next future tick
/// and does NOT catch up — each missed tick is simply skipped.
/// </summary>
internal sealed class PollingTimer : IAsyncDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly TimeProvider _timeProvider;

    public PollingTimer(TimeSpan period, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = new PeriodicTimer(period, _timeProvider);
    }

    /// <summary>
    /// Waits for the next tick. Returns <c>false</c> when the timer is disposed.
    /// </summary>
    public ValueTask<bool> WaitForNextTickAsync(CancellationToken ct = default) =>
        _timer.WaitForNextTickAsync(ct);

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
