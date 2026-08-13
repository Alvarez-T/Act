namespace YFex.System.Telemetry;

public interface ITelemetryTransport : IAsyncDisposable
{
    Task<bool> ShipBatchAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken ct);
}
