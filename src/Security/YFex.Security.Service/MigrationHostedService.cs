using Microsoft.Extensions.Hosting;
using YFex.Security.Config;
using YFex.Security.Storage.Migrations;

namespace YFex.Security.Service;

/// <summary>Runs database migrations once at startup before other services use the DB.</summary>
public sealed class MigrationHostedService : IHostedService
{
    private readonly SecurityConfig _config;

    public MigrationHostedService(SecurityConfig config)
    {
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string dbPath = Environment.ExpandEnvironmentVariables(_config.DatabasePath);
        string? dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var runner = new MigrationRunner(connectionString);
        await runner.RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
