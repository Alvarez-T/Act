namespace YFex.Security.Storage;

public interface IBaselineRepository
{
    Task UpsertBaselineEntryAsync(string baselineType, string processName, string key, CancellationToken ct = default);
    Task<bool> IsKnownAsync(string baselineType, string processName, string key, CancellationToken ct = default);
    Task<IReadOnlyList<BaselineRecord>> GetBaselineAsync(string baselineType, string processName, CancellationToken ct = default);
}
