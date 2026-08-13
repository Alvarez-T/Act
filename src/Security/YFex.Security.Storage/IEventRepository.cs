using YFex.Security.Events;

namespace YFex.Security.Storage;

public interface IEventRepository
{
    Task InsertEventAsync(ISecurityEvent evt, CancellationToken ct = default);
    Task InsertBatchAsync(IReadOnlyList<ISecurityEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<ISecurityEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default);
    Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public sealed record EventQuery
{
    public DateTimeOffset? After { get; init; }
    public DateTimeOffset? Before { get; init; }
    public string? ProcessName { get; init; }
    public SecurityEventType? EventType { get; init; }
    public string? RemoteAddress { get; init; }
    public string? Domain { get; init; }
    public int Limit { get; init; } = 1000;
    public int Offset { get; init; } = 0;
}
