using System.Text.Json.Serialization;

namespace YFex.Security.Proxy;

/// <summary>One JSON line emitted by the mitmproxy capture addon over stdout.</summary>
public sealed record ProxyMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    // http
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("query_string")] public string? QueryString { get; init; }
    [JsonPropertyName("request_headers")] public Dictionary<string, string>? RequestHeaders { get; init; }
    [JsonPropertyName("request_body")] public string? RequestBody { get; init; }
    [JsonPropertyName("request_content_type")] public string? RequestContentType { get; init; }
    [JsonPropertyName("response_status")] public int? ResponseStatus { get; init; }
    [JsonPropertyName("response_headers")] public Dictionary<string, string>? ResponseHeaders { get; init; }
    [JsonPropertyName("response_body")] public string? ResponseBody { get; init; }
    [JsonPropertyName("response_content_type")] public string? ResponseContentType { get; init; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; init; }

    // websocket
    [JsonPropertyName("direction")] public string? Direction { get; init; }
    [JsonPropertyName("opcode")] public int? Opcode { get; init; }
    [JsonPropertyName("payload")] public string? Payload { get; init; }

    [JsonPropertyName("client_port")] public int? ClientPort { get; init; }
}
