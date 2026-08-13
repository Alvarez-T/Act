using Dapper;

namespace YFex.Security.Storage.Repositories;

public sealed class BaselineRepository : IBaselineRepository
{
    private readonly SecurityDbConnectionFactory _factory;

    public BaselineRepository(SecurityDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task UpsertBaselineEntryAsync(string baselineType, string processName, string key, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);
        string now = DateTimeOffset.UtcNow.ToString("O");

        await connection.ExecuteAsync(
            """
            INSERT INTO baselines (baseline_type, process_name, key, first_seen, last_seen, count)
            VALUES (@BaselineType, @ProcessName, @Key, @Now, @Now, 1)
            ON CONFLICT(baseline_type, process_name, key)
            DO UPDATE SET last_seen = @Now, count = count + 1
            """,
            new { BaselineType = baselineType, ProcessName = processName, Key = key, Now = now });
    }

    public async Task<bool> IsKnownAsync(string baselineType, string processName, string key, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM baselines WHERE baseline_type = @BaselineType AND process_name = @ProcessName AND key = @Key",
            new { BaselineType = baselineType, ProcessName = processName, Key = key }) > 0;
    }

    public async Task<IReadOnlyList<BaselineRecord>> GetBaselineAsync(string baselineType, string processName, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        var rows = await connection.QueryAsync<BaselineRecord>(
            """
            SELECT id as Id, baseline_type as BaselineType, process_name as ProcessName,
                   key as Key, first_seen as FirstSeen, last_seen as LastSeen, count as Count
            FROM baselines
            WHERE baseline_type = @BaselineType AND process_name = @ProcessName
            ORDER BY last_seen DESC
            """,
            new { BaselineType = baselineType, ProcessName = processName });

        return rows.ToList();
    }
}
