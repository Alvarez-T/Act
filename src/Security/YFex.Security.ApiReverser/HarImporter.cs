using System.Text.Json;
using YFex.Security.Events;
using YFex.Security.Storage;

namespace YFex.Security.ApiReverser;

/// <summary>
/// [PERSONAL USE] Imports browser DevTools HAR files into <see cref="HttpCaptureEvent"/>
/// and <see cref="WebSocketFrameEvent"/> records. HAR has no PID, so ProcessId defaults
/// to 0 and ProcessName to the supplied value ("browser" by default).
/// </summary>
public sealed class HarImporter
{
    private readonly IEventRepository _events;

    public HarImporter(IEventRepository events)
    {
        _events = events;
    }

    public async Task<IReadOnlyList<ISecurityEvent>> ImportHarFileAsync(
        string harFilePath, string? processName = "browser", CancellationToken ct = default)
    {
        string json = await File.ReadAllTextAsync(harFilePath, ct);
        using var doc = JsonDocument.Parse(json);

        var results = new List<ISecurityEvent>();

        if (!doc.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries))
            return results;

        foreach (var entry in entries.EnumerateArray())
        {
            var httpEvent = ParseEntry(entry, processName ?? "browser");
            if (httpEvent is not null)
                results.Add(httpEvent);

            // Chrome stores WS frames under _webSocketMessages on 101 entries.
            if (entry.TryGetProperty("_webSocketMessages", out var wsMessages) &&
                wsMessages.ValueKind == JsonValueKind.Array)
            {
                string url = entry.TryGetProperty("request", out var req) &&
                             req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

                foreach (var wsMsg in wsMessages.EnumerateArray())
                    results.Add(ParseWsMessage(wsMsg, url, processName ?? "browser"));
            }
        }

        return results;
    }

    public async Task ImportAndStoreAsync(string harFilePath, string serviceName, CancellationToken ct = default)
    {
        var events = await ImportHarFileAsync(harFilePath, serviceName, ct);
        await _events.InsertBatchAsync(events, ct);
    }

    private static HttpCaptureEvent? ParseEntry(JsonElement entry, string processName)
    {
        if (!entry.TryGetProperty("request", out var request)) return null;

        string method = GetString(request, "method");
        string url = GetString(request, "url");
        if (string.IsNullOrEmpty(url)) return null;

        Uri.TryCreate(url, UriKind.Absolute, out var uri);

        var requestHeaders = ParseHeaders(request);
        string? requestBody = request.TryGetProperty("postData", out var postData)
            ? GetString(postData, "text") : null;
        string? requestContentType = request.TryGetProperty("postData", out var pd)
            ? GetString(pd, "mimeType") : null;

        int? status = null;
        Dictionary<string, string>? responseHeaders = null;
        string? responseBody = null;
        string? responseContentType = null;
        long? responseSize = null;

        if (entry.TryGetProperty("response", out var response))
        {
            if (response.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number)
                status = st.GetInt32();
            responseHeaders = ParseHeaders(response);
            if (response.TryGetProperty("content", out var content))
            {
                responseBody = GetString(content, "text");
                responseContentType = GetString(content, "mimeType");
                if (content.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                    responseSize = sz.GetInt64();
            }
        }

        double? duration = entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble() : null;

        DateTimeOffset timestamp = entry.TryGetProperty("startedDateTime", out var sd) &&
            DateTimeOffset.TryParse(sd.GetString(), out var dt) ? dt : DateTimeOffset.UtcNow;

        return new HttpCaptureEvent
        {
            Timestamp = timestamp,
            ProcessId = 0,
            ProcessName = processName,
            Method = method,
            Url = url,
            Host = uri?.Host ?? "",
            Path = uri?.AbsolutePath ?? "",
            QueryString = uri?.Query.TrimStart('?') ?? "",
            RequestHeaders = requestHeaders,
            RequestBody = requestBody,
            RequestContentType = requestContentType,
            ResponseStatusCode = status,
            ResponseHeaders = responseHeaders,
            ResponseBody = responseBody,
            ResponseContentType = responseContentType,
            ResponseSizeBytes = responseSize,
            DurationMs = duration,
            Source = CaptureSource.DevTools
        };
    }

    private static WebSocketFrameEvent ParseWsMessage(JsonElement wsMsg, string url, string processName)
    {
        // Chrome: { "type": "send"|"receive", "opcode": 1, "data": "...", "time": 123.4 }
        string type = GetString(wsMsg, "type");
        string data = GetString(wsMsg, "data");
        int opcode = wsMsg.TryGetProperty("opcode", out var op) && op.ValueKind == JsonValueKind.Number
            ? op.GetInt32() : 1;

        DateTimeOffset timestamp = wsMsg.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t.GetDouble() * 1000))
            : DateTimeOffset.UtcNow;

        return new WebSocketFrameEvent
        {
            Timestamp = timestamp,
            ProcessId = 0,
            ProcessName = processName,
            ConnectionUrl = url,
            Direction = type.Equals("send", StringComparison.OrdinalIgnoreCase)
                ? WsDirection.Sent : WsDirection.Received,
            Opcode = (WsOpcode)opcode,
            TextPayload = data,
            PayloadLength = data.Length
        };
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement element)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("headers", out var headerArray) &&
            headerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headerArray.EnumerateArray())
            {
                string name = GetString(header, "name");
                string value = GetString(header, "value");
                if (!string.IsNullOrEmpty(name))
                    headers[name] = value;
            }
        }
        return headers;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
