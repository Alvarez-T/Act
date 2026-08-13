using Microsoft.Data.Sqlite;

namespace YFex.Security.Storage;

public sealed class SecurityDbConnectionFactory
{
    private readonly string _connectionString;

    public SecurityDbConnectionFactory(string databasePath)
    {
        string expanded = Environment.ExpandEnvironmentVariables(databasePath);
        string? dir = Path.GetDirectoryName(expanded);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = expanded,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct);
        return connection;
    }
}
