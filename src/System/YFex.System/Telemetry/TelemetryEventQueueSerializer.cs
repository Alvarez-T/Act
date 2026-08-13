using System.Text.Json;
using System.Text.Json.Nodes;
using YFex.Persistence;
using YFex.System.Consent;

namespace YFex.System.Telemetry;

/// <summary>
/// Serializes <see cref="TelemetryEvent"/> to and from the JSON form stored by a durable
/// offline queue. Domain-owned format that keeps the generic
/// <see cref="Persistence.IDurableQueue{T}"/> backing store free of telemetry types.
/// </summary>
public sealed class TelemetryEventQueueSerializer : IQueueItemSerializer<TelemetryEvent>
{
    public string Serialize(TelemetryEvent evt)
    {
        var obj = new JsonObject
        {
            ["eventName"] = evt.EventName,
            ["sessionId"] = evt.SessionId,
            ["deviceIdHash"] = evt.DeviceIdHash,
            ["timestamp"] = evt.Timestamp.ToString("O"),
            ["consentType"] = evt.ConsentAtCollection switch
            {
                NotAsked => "not_asked",
                Declined => "declined",
                FunctionalOnly => "functional",
                AnalyticsConsent => "analytics",
                FullConsent => "full",
                _ => "unknown"
            }
        };

        var props = new JsonObject();
        foreach (var (key, value) in evt.Properties)
        {
            props[key] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                float f => JsonValue.Create(f),
                bool b => JsonValue.Create(b),
                _ => JsonValue.Create(value.ToString())
            };
        }
        obj["properties"] = props;

        return obj.ToJsonString();
    }

    public TelemetryEvent? Deserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            ConsentState consent = root.GetProperty("consentType").GetString() switch
            {
                "declined" => new Declined(DateTimeOffset.UtcNow),
                "functional" => new FunctionalOnly(DateTimeOffset.UtcNow, "restored"),
                "analytics" => new AnalyticsConsent(DateTimeOffset.UtcNow, "restored"),
                "full" => new FullConsent(DateTimeOffset.UtcNow, "restored"),
                _ => new NotAsked()
            };

            var properties = new Dictionary<string, object?>();
            if (root.TryGetProperty("properties", out var props))
            {
                foreach (var prop in props.EnumerateObject())
                {
                    properties[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.ToString()
                    };
                }
            }

            return new TelemetryEvent(
                root.GetProperty("eventName").GetString()!,
                root.GetProperty("sessionId").GetString()!,
                root.GetProperty("deviceIdHash").GetString()!,
                DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
                consent,
                properties);
        }
        catch { return null; }
    }
}
