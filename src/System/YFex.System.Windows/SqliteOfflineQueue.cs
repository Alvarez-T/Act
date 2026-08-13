using Microsoft.Data.Sqlite;
using YFex.Data;
using YFex.Persistence.Data;
using YFex.Sqlite;
using YFex.System.Telemetry;

namespace YFex.System.Windows;

/// <summary>
/// SQLite-backed <see cref="IOfflineQueue"/> for telemetry events. Binds the engine-neutral
/// <see cref="DataDurableQueue{T}"/> to <see cref="TelemetryEvent"/> via the domain
/// <see cref="TelemetryEventQueueSerializer"/> and a SQLite <see cref="YFexConnection"/>.
/// Stores under <c>%LocalAppData%/YFex/telemetry_queue.db</c> with a 7-day time-to-live.
/// </summary>
public sealed class SqliteOfflineQueue : DataDurableQueue<TelemetryEvent>, IOfflineQueue
{
    public SqliteOfflineQueue()
        : base(CreateConnectionFactory(), new TelemetryEventQueueSerializer(), TimeSpan.FromDays(7), "queued_events")
    {
    }

    private static YFexConnectionFactory CreateConnectionFactory()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YFex",
            "telemetry_queue.db");

        var dir = Path.GetDirectoryName(dbPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        return new YFexConnectionFactory(
            () => new SqliteConnection(connectionString),
            SqliteDialect.Instance);
    }
}
