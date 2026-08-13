using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace YFex.Security.Storage.Migrations;

public sealed class MigrationRunner
{
    private readonly string _connectionString;

    public MigrationRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _migrations (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        var applied = (await connection.QueryAsync<string>(
            "SELECT name FROM _migrations ORDER BY name")).ToHashSet();

        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "YFex.Security.Storage.Migrations.Sql.";

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".sql"))
            .OrderBy(n => n)
            .ToList();

        foreach (var resource in resources)
        {
            string migrationName = resource[prefix.Length..];
            if (applied.Contains(migrationName)) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            string sql = await reader.ReadToEndAsync(ct);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(sql, transaction: transaction);
            await connection.ExecuteAsync(
                "INSERT INTO _migrations (name) VALUES (@Name)",
                new { Name = migrationName },
                transaction);
            await transaction.CommitAsync(ct);
        }
    }
}
