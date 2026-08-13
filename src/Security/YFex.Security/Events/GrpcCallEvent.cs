using System.Text.Json;

namespace YFex.Security.Events;

public sealed record GrpcCallEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.GrpcCall;
    public string ServiceName { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public byte[]? RequestProtobuf { get; init; }
    public string? RequestDecoded { get; init; }
    public byte[]? ResponseProtobuf { get; init; }
    public string? ResponseDecoded { get; init; }
    public int? GrpcStatusCode { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}
