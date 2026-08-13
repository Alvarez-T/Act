using System.Text.Json;

namespace YFex.Security.Events;

public sealed record WebSocketFrameEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.WebSocketFrame;
    public string ConnectionUrl { get; init; } = "";
    public WsDirection Direction { get; init; }
    public WsOpcode Opcode { get; init; }
    public string? TextPayload { get; init; }
    public byte[]? BinaryPayload { get; init; }
    public long PayloadLength { get; init; }
    public bool IsFinal { get; init; } = true;
    public string ToJson() => JsonSerializer.Serialize(this);
}

public enum WsDirection { Sent, Received }
public enum WsOpcode { Text = 1, Binary = 2, Close = 8, Ping = 9, Pong = 10 }
