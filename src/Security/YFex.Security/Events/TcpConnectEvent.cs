using System.Text.Json;

namespace YFex.Security.Events;

public sealed record TcpConnectEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.TcpConnect;
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string Protocol { get; init; } = "TCP";
    public string ToJson() => JsonSerializer.Serialize(this);
}
