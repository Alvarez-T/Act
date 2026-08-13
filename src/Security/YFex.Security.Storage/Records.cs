namespace YFex.Security.Storage;

public sealed record ApiEndpointRecord
{
    public long Id { get; init; }
    public string ServiceName { get; init; } = "";
    public string EndpointUrl { get; init; } = "";
    public string Method { get; init; } = "";
    public string Host { get; init; } = "";
    public string PathTemplate { get; init; } = "";
    public string? RequiredHeaders { get; init; }
    public string? AuthType { get; init; }
    public string? AuthDetails { get; init; }
    public string? RequestBodySchema { get; init; }
    public string? ResponseBodySchema { get; init; }
    public string? Notes { get; init; }
    public string DiscoveredAt { get; init; } = "";
    public string LastSeenAt { get; init; } = "";
    public int CallCount { get; init; } = 1;
}

public sealed record WsProtocolRecord
{
    public long Id { get; init; }
    public string ServiceName { get; init; } = "";
    public string ConnectionUrl { get; init; } = "";
    public string? Subprotocol { get; init; }
    public string FrameType { get; init; } = "";
    public string Direction { get; init; } = "";
    public string? ExamplePayload { get; init; }
    public string? PayloadSchema { get; init; }
    public string? Notes { get; init; }
    public string DiscoveredAt { get; init; } = "";
}

public sealed record BaselineRecord
{
    public long Id { get; init; }
    public string BaselineType { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string Key { get; init; } = "";
    public string FirstSeen { get; init; } = "";
    public string LastSeen { get; init; } = "";
    public int Count { get; init; }
}
