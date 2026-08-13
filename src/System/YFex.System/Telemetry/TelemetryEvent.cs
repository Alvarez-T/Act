using YFex.System.Consent;

namespace YFex.System.Telemetry;

public record TelemetryEvent(
    string EventName,
    string SessionId,
    string DeviceIdHash,
    DateTimeOffset Timestamp,
    ConsentState ConsentAtCollection,
    Dictionary<string, object?> Properties
);
