using Microsoft.Data.Sqlite;

namespace YFex.Messaging.Rpc.Sqlite;

/// <summary>
/// Owns the single shared <see cref="SqliteConnection"/> and applies the schema on first open.
/// All storage types share this factory; WAL mode allows concurrent reads with one write at a time.
/// </summary>
public sealed class SqliteConnectionFactory : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteConnectionFactory(SqliteStorageOptions opts)
    {
        var path = opts.ResolveDbPath();
        _conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
    }

    public SqliteConnection Connection => _conn;

    public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _conn.OpenAsync(ct);
            await ApplyPragmasAsync(ct);
            await CreateSchemaAsync(ct);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private async Task ApplyPragmasAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS _schema_version (Version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO _schema_version VALUES (1);

            CREATE TABLE IF NOT EXISTS Cache (
                Key       TEXT    PRIMARY KEY,
                Value     BLOB    NOT NULL,
                IsStale   INTEGER NOT NULL DEFAULT 0,
                WrittenAt INTEGER NOT NULL,
                ExpiresAt INTEGER
            );
            CREATE INDEX IF NOT EXISTS Cache_ExpiresAt ON Cache(ExpiresAt) WHERE ExpiresAt IS NOT NULL;

            CREATE TABLE IF NOT EXISTS Outbox (
                IdempotencyKey    TEXT    PRIMARY KEY,
                CommandTypeName   TEXT    NOT NULL,
                Payload           BLOB    NOT NULL,
                EnqueuedAt        INTEGER NOT NULL,
                AttemptCount      INTEGER NOT NULL DEFAULT 0,
                LastAttemptAt     INTEGER,
                LastFailureReason TEXT
            );
            CREATE INDEX IF NOT EXISTS Outbox_EnqueuedAt ON Outbox(EnqueuedAt);

            CREATE TABLE IF NOT EXISTS SyncFailures (
                Id              TEXT    PRIMARY KEY,
                CommandTypeName TEXT    NOT NULL,
                Payload         BLOB    NOT NULL,
                FailureReason   TEXT    NOT NULL,
                OccurredAt      INTEGER NOT NULL,
                IsAcknowledged  INTEGER NOT NULL DEFAULT 0
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
        _initLock.Dispose();
    }
}
