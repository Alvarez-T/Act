using System.Text.Json;

namespace YFex.Security.Events;

public sealed record DnsQueryEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.DnsQuery;
    public string QueryName { get; init; } = "";
    public string QueryType { get; init; } = "A";
    public string[] ResolvedAddresses { get; init; } = [];
    public string ToJson() => JsonSerializer.Serialize(this);
}
