using YFex.Data;

namespace YFex.Persistence.Data;

/// <summary>
/// <see cref="IDurableQueue{T}"/> implemented over <c>YFex.Data</c>. Items are stored in a single
/// table as serialized strings with creation and expiry timestamps; expired rows are skipped on
/// dequeue and removed by <see cref="PurgeExpiredAsync"/>.
/// </summary>
/// <remarks>
/// Engine-neutral: the queue reaches the database through a <see cref="YFexConnection"/> and uses
/// the connection's <see cref="IQueryDialect"/> for engine-specific SQL (e.g. pagination), so the
/// same queue works against SQLite, Oracle, Postgres, etc. Domain libraries bind a concrete
/// <typeparamref name="T"/> and <see cref="IQueueItemSerializer{T}"/> (see a telemetry offline queue).
/// The queue table is created on first use.
/// </remarks>
public class DataDurableQueue<T> : IDurableQueue<T> where T : class
{
    private readonly YFexConnectionFactory _connections;
    private readonly IQueueItemSerializer<T> _serializer;
    private readonly TimeSpan _ttl;
    private readonly string _table;
    private volatile bool _tableEnsured;

    /// <param name="connections">Factory that produces open-able connections bound to a dialect.</param>
    /// <param name="serializer">Serializer for queued items.</param>
    /// <param name="ttl">How long a queued item remains eligible for dequeue before it expires.</param>
    /// <param name="table">
    /// Table name to store items in. Must be a trusted constant (interpolated into DDL/DML).
    /// </param>
    public DataDurableQueue(
        YFexConnectionFactory connections,
        IQueueItemSerializer<T> serializer,
        TimeSpan ttl,
        string table = "durable_queue")
    {
        _connections = connections;
        _serializer = serializer;
        _ttl = ttl;
        _table = table;
    }

    public async Task EnqueueAsync(IReadOnlyList<T> items, CancellationToken ct)
    {
        if (items.Count == 0) return;

        await using var conn = _connections.Create();
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn);

        await conn.StartScopedTransaction(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in items)
            {
                await conn.Execute(
                    $"INSERT INTO {_table} (id, payload, created_at, expires_at) VALUES (@id, @payload, @created, @expires)",
                    new
                    {
                        id = Guid.NewGuid().ToString("N"),
                        payload = _serializer.Serialize(item),
                        created = now.ToString("O"),
                        expires = now.Add(_ttl).ToString("O")
                    });
            }
        });
    }

    public async Task<IReadOnlyList<T>> DequeueAsync(int maxCount, CancellationToken ct)
    {
        await using var conn = _connections.Create();
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn);

        return await conn.StartScopedTransaction(async () =>
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            string pagination = conn.Dialect.BuildPagination(0, maxCount);

            var rows = (await conn.Query<QueueRow>(
                $"SELECT id AS Id, payload AS Payload FROM {_table} WHERE expires_at > @now ORDER BY created_at, id {pagination}",
                new { now })).ToList();

            if (rows.Count > 0)
            {
                var ids = rows.Select(r => r.Id).ToArray();
                await conn.Execute($"DELETE FROM {_table} WHERE id IN @ids", new { ids });
            }

            var items = new List<T>(rows.Count);
            foreach (var row in rows)
            {
                var item = _serializer.Deserialize(row.Payload);
                if (item is not null) items.Add(item);
            }

            return (IReadOnlyList<T>)items;
        });
    }

    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        await using var conn = _connections.Create();
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn);

        await conn.Execute(
            $"DELETE FROM {_table} WHERE expires_at <= @now",
            new { now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = _connections.Create();
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn);

        return await conn.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM {_table} WHERE expires_at > @now",
            new { now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task EnsureTableAsync(YFexConnection conn)
    {
        if (_tableEnsured) return;

        await conn.Execute($$"""
            CREATE TABLE IF NOT EXISTS {{_table}} (
                id         TEXT PRIMARY KEY,
                payload    TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            )
            """);

        _tableEnsured = true;
    }

    private sealed class QueueRow
    {
        public string Id { get; init; } = "";
        public string Payload { get; init; } = "";
    }
}
