using YFex.System.Unions;

namespace YFex.System.Telemetry;

public interface ITelemetryCollector : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    void Track(string eventName, Dictionary<string, object?>? properties = null);
    Task<SnapshotResult> CollectSnapshotAsync(CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
