using System.Text.Json;

namespace YFex.Security.Events;

public sealed record ProcessSecurityEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType { get; init; }
    public int ParentProcessId { get; init; }
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public string? ModulePath { get; init; }
    public bool? IsSigned { get; init; }
    public string? SignerName { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}
