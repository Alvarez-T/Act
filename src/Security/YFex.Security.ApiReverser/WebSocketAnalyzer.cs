using System.Text.Json;
using YFex.Security.Events;
using YFex.Security.Storage;

namespace YFex.Security.ApiReverser;

/// <summary>
/// [PERSONAL USE] Analyzes captured <see cref="WebSocketFrameEvent"/>s into protocol
/// definitions: detects framing (Socket.IO / MessagePack / protobuf / JSON), clusters
/// frame types, flags likely auth and keepalive frames, and catalogs them.
/// </summary>
public sealed class WebSocketAnalyzer
{
    private readonly IApiCatalogRepository _catalog;

    public WebSocketAnalyzer(IApiCatalogRepository catalog)
    {
        _catalog = catalog;
    }

    public async Task AnalyzeAndCatalogAsync(
        string serviceName,
        IReadOnlyList<WebSocketFrameEvent> frames,
        CancellationToken ct = default)
    {
        var byConnection = frames
            .Where(f => !string.IsNullOrEmpty(f.ConnectionUrl))
            .GroupBy(f => f.ConnectionUrl);

        foreach (var connection in byConnection)
        {
            var ordered = connection.OrderBy(f => f.Timestamp).ToList();
            string subprotocol = DetectSubprotocol(ordered);

            // Cluster by (direction, top-level shape key).
            var clusters = new Dictionary<(WsDirection, string), List<WebSocketFrameEvent>>();
            foreach (var frame in ordered)
            {
                string shapeKey = ClassifyFrame(frame, ordered, out _);
                var key = (frame.Direction, shapeKey);
                if (!clusters.TryGetValue(key, out var list))
                    clusters[key] = list = [];
                list.Add(frame);
            }

            string now = DateTimeOffset.UtcNow.ToString("O");

            foreach (var ((direction, shapeKey), clusterFrames) in clusters)
            {
                var example = clusterFrames[0];
                string? schema = BuildSchema(clusterFrames);

                await _catalog.UpsertWsProtocolAsync(new WsProtocolRecord
                {
                    ServiceName = serviceName,
                    ConnectionUrl = connection.Key,
                    Subprotocol = subprotocol,
                    FrameType = shapeKey,
                    Direction = direction.ToString(),
                    ExamplePayload = Truncate(example.TextPayload, 1024),
                    PayloadSchema = schema,
                    Notes = clusterFrames.Count > 1 ? $"{clusterFrames.Count} samples" : null,
                    DiscoveredAt = now
                }, ct);
            }
        }
    }

    private static string DetectSubprotocol(List<WebSocketFrameEvent> frames)
    {
        foreach (var frame in frames)
        {
            string? payload = frame.TextPayload;
            if (string.IsNullOrEmpty(payload)) continue;

            // Socket.IO engine.io framing: "0"/"40" handshake, "42[...]" events.
            if (payload.StartsWith("0{") || payload.StartsWith("40") || payload.StartsWith("42["))
                return "socket.io";

            if (payload.StartsWith('{') || payload.StartsWith('['))
                return "json";
        }

        // Binary heuristics.
        foreach (var frame in frames)
        {
            if (frame.BinaryPayload is { Length: > 0 } bin)
            {
                byte first = bin[0];
                if (first is >= 0x80 and <= 0x9f) return "messagepack";
                return "binary";
            }
        }

        return "json";
    }

    /// <summary>Classify a frame into a stable type key based on its content shape.</summary>
    private static string ClassifyFrame(WebSocketFrameEvent frame, List<WebSocketFrameEvent> all, out bool isAuth)
    {
        isAuth = false;
        string? payload = frame.TextPayload;

        if (string.IsNullOrEmpty(payload))
            return frame.Opcode == WsOpcode.Binary ? "binary" : "empty";

        // First sent frame containing auth-ish keys → "auth".
        if (frame.Direction == WsDirection.Sent &&
            (payload.Contains("token", StringComparison.OrdinalIgnoreCase) ||
             payload.Contains("\"auth\"", StringComparison.OrdinalIgnoreCase)) &&
            all.Where(f => f.Direction == WsDirection.Sent).OrderBy(f => f.Timestamp).FirstOrDefault() == frame)
        {
            isAuth = true;
            return "auth";
        }

        // Keepalive: short, constant ping/pong-ish content.
        string trimmed = payload.Trim();
        if (trimmed is "2" or "3" or "ping" or "pong" || trimmed.Length <= 2)
            return "keepalive";

        // JSON: key by top-level "type"/"event"/"op" discriminator, else sorted key set.
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var disc in new[] { "type", "event", "op", "action", "method" })
                {
                    if (doc.RootElement.TryGetProperty(disc, out var v) && v.ValueKind == JsonValueKind.String)
                        return $"{disc}:{v.GetString()}";
                }

                var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k);
                return "obj:" + string.Join(",", keys);
            }
        }
        catch (JsonException) { }

        // Socket.IO event packet "42[\"name\",...]".
        if (payload.StartsWith("42["))
        {
            int q1 = payload.IndexOf('"');
            int q2 = q1 >= 0 ? payload.IndexOf('"', q1 + 1) : -1;
            if (q1 >= 0 && q2 > q1)
                return "socketio:" + payload.Substring(q1 + 1, q2 - q1 - 1);
        }

        return "text";
    }

    private static string? BuildSchema(List<WebSocketFrameEvent> frames)
    {
        var bodies = frames.Select(f => f.TextPayload);
        return HttpFlowAnalyzer.MergeJsonSchema(bodies);
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
