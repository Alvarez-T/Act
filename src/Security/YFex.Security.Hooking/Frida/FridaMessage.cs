using System.Text.Json.Serialization;

namespace YFex.Security.Hooking.Frida;

/// <summary>One JSON line emitted by a Frida hook script over stdout.</summary>
public sealed record FridaMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    [JsonPropertyName("data")]
    public FridaPayload? Data { get; init; }
}

public sealed record FridaPayload
{
    // http_request / http_response
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }

    // ws_frame
    [JsonPropertyName("direction")] public string? Direction { get; init; }
    [JsonPropertyName("opcode")] public int? Opcode { get; init; }
    [JsonPropertyName("payload")] public string? Payload { get; init; }

    // raw_write
    [JsonPropertyName("base64")] public string? Base64 { get; init; }
}
