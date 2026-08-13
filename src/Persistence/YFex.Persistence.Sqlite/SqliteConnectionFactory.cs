using Microsoft.Data.Sqlite;

namespace YFex.Persistence.Sqlite;

/// <summary>
/// Resolves a local SQLite database path (creating the directory) and hands out opened
/// connections with WAL pragmas applied. The single shared foundation for every SQLite-backed
/// YFex.Persistence store — replaces the per-project connection factories that each re-did
/// "open WAL connection + create schema".
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        string expanded = Environment.ExpandEnvironmentVariables(databasePath);
        string? dir = Path.GetDirectoryName(expanded);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = expanded,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
