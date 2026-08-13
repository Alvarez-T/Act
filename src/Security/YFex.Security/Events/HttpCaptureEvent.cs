using System.Text.Json;

namespace YFex.Security.Events;

public sealed record HttpCaptureEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.HttpRequest;
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public string Host { get; init; } = "";
    public string Path { get; init; } = "";
    public string QueryString { get; init; } = "";
    public Dictionary<string, string> RequestHeaders { get; init; } = new();
    public string? RequestBody { get; init; }
    public string? RequestContentType { get; init; }
    public int? ResponseStatusCode { get; init; }
    public Dictionary<string, string>? ResponseHeaders { get; init; }
    public string? ResponseBody { get; init; }
    public string? ResponseContentType { get; init; }
    public long? ResponseSizeBytes { get; init; }
    public double? DurationMs { get; init; }
    public CaptureSource Source { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}

public enum CaptureSource { Proxy, Hook, DevTools, Manual }
