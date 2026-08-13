using YFex.Persistence.Sqlite;
using YFex.System.Telemetry;

namespace YFex.System.Windows;

/// <summary>
/// SQLite-backed <see cref="IOfflineQueue"/> for telemetry events. A domain binding of the
/// generic <see cref="SqliteDurableQueue{T}"/> (YFex.Persistence.Sqlite) over
/// <see cref="TelemetryEvent"/> via <see cref="TelemetryEventQueueSerializer"/>. Stores under
/// <c>%LocalAppData%/YFex/telemetry_queue.db</c> (table <c>queued_events</c>) with a 7-day TTL.
/// </summary>
public sealed class SqliteOfflineQueue : SqliteDurableQueue<TelemetryEvent>, IOfflineQueue
{
    public SqliteOfflineQueue()
        : base(CreateConnectionFactory(), new TelemetryEventQueueSerializer(), TimeSpan.FromDays(7), "queued_events")
    {
    }

    private static SqliteConnectionFactory CreateConnectionFactory()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YFex",
            "telemetry_queue.db");
        return new SqliteConnectionFactory(dbPath);
    }
}
