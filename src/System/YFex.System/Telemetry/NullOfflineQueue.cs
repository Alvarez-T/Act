namespace YFex.System.Telemetry;

public sealed class NullOfflineQueue : IOfflineQueue
{
    public Task EnqueueAsync(IReadOnlyList<TelemetryEvent> events, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<TelemetryEvent>> DequeueAsync(int maxCount, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TelemetryEvent>>([]);
    public Task PurgeExpiredAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
