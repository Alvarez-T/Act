namespace YFex.Security.Storage;

public interface IApiCatalogRepository
{
    Task UpsertEndpointAsync(ApiEndpointRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<ApiEndpointRecord>> GetEndpointsAsync(string serviceName, CancellationToken ct = default);
    Task UpsertWsProtocolAsync(WsProtocolRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<WsProtocolRecord>> GetWsProtocolsAsync(string serviceName, CancellationToken ct = default);
}
