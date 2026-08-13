using YFex.Persistence;

namespace YFex.System.Telemetry;

/// <summary>
/// Durable queue for telemetry events that could not be shipped (offline / transport failure).
/// A domain-typed <see cref="IDurableQueue{T}"/> over <see cref="TelemetryEvent"/>; the SQLite
/// backing store lives in YFex.Persistence.Sqlite.
/// </summary>
public interface IOfflineQueue : IDurableQueue<TelemetryEvent>
{
}
