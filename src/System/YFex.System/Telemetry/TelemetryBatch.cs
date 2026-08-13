using System.Threading.Channels;

namespace YFex.System.Telemetry;

public sealed class TelemetryBatch : IAsyncDisposable
{
    private readonly Channel<TelemetryEvent> _channel;
    private readonly ITelemetryTransport _transport;
    private readonly IOfflineQueue _offlineQueue;
    private readonly TelemetryOptions _options;
    private CancellationTokenSource? _cts;
    private Task? _processTask;

    public TelemetryBatch(ITelemetryTransport transport, TelemetryOptions options, IOfflineQueue offlineQueue)
    {
        _transport = transport;
        _options = options;
        _offlineQueue = offlineQueue;
        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(options.MaxQueueSize)
        {
            FullMode = options.DropOnFull
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.Wait
        });
    }

    public bool Enqueue(TelemetryEvent evt) => _channel.Writer.TryWrite(evt);

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _processTask = ProcessAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        if (_processTask is not null)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            try { await _processTask.WaitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        // Drain offline queue on startup
        try { await DrainOfflineQueueAsync(ct); }
        catch { }

        var batch = new List<TelemetryEvent>(_options.BatchSize);
        var reader = _channel.Reader;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timerCts.CancelAfter(_options.FlushInterval);

                while (batch.Count < _options.BatchSize)
                {
                    try
                    {
                        var evt = await reader.ReadAsync(timerCts.Token);
                        batch.Add(evt);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ChannelClosedException) { break; }
                }

                if (batch.Count > 0)
                    await ShipOrQueueAsync(batch, ct);

                if (reader.Completion.IsCompleted && !reader.TryPeek(out _))
                    break;
            }
            catch (OperationCanceledException) { break; }
            catch { /* telemetry must never crash the host */ }
        }

        // Final flush of remaining items
        batch.Clear();
        while (reader.TryRead(out var remaining))
            batch.Add(remaining);
        if (batch.Count > 0)
        {
            try { await ShipOrQueueAsync(batch, CancellationToken.None); }
            catch { }
        }
    }

    private async Task ShipOrQueueAsync(List<TelemetryEvent> batch, CancellationToken ct)
    {
        var scrubbed = batch.Select(e => PrivacyScrubber.Scrub(e, _options.TransportSaltKey)).ToList();

        bool shipped = false;
        try { shipped = await _transport.ShipBatchAsync(scrubbed, ct); }
        catch { /* network failure */ }

        if (!shipped)
        {
            try { await _offlineQueue.EnqueueAsync(scrubbed, ct); }
            catch { /* telemetry must never crash the host */ }
        }
    }

    private async Task DrainOfflineQueueAsync(CancellationToken ct)
    {
        await _offlineQueue.PurgeExpiredAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var queued = await _offlineQueue.DequeueAsync(_options.BatchSize, ct);
            if (queued.Count == 0) break;

            bool shipped = false;
            try { shipped = await _transport.ShipBatchAsync(queued, ct); }
            catch { }

            if (!shipped)
            {
                try { await _offlineQueue.EnqueueAsync(queued, ct); }
                catch { }
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_processTask is not null)
            {
                try { await _processTask; }
                catch { }
            }
            _cts.Dispose();
        }
    }
}
