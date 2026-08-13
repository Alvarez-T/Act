using System.Text.Json;

namespace YFex.Security.Events;

public sealed record AnomalyEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.AnomalyDetected;
    public AnomalyType AnomalyType { get; init; }
    public string Description { get; init; } = "";
    public AnomalySeverity Severity { get; init; }
    public string? RelatedEventJson { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}

public enum AnomalyType
{
    NewDomain,
    NewListeningPort,
    UnexpectedNetworkAccess,
    Ja3Mismatch,
    UnsignedModuleLoad,
    RemoteThreadInjection,
    UnusualHour,
    ByteVolumeSpike,
    NewPersistence,
    DnsExfiltration
}

public enum AnomalySeverity { Low, Medium, High, Critical }
