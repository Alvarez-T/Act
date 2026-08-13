using Dapper;

namespace YFex.Security.Storage.Repositories;

public sealed class ApiCatalogRepository : IApiCatalogRepository
{
    private readonly SecurityDbConnectionFactory _factory;

    public ApiCatalogRepository(SecurityDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task UpsertEndpointAsync(ApiEndpointRecord record, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO api_endpoints (service_name, endpoint_url, method, host, path_template,
                required_headers, auth_type, auth_details, request_body_schema, response_body_schema,
                notes, discovered_at, last_seen_at, call_count)
            VALUES (@ServiceName, @EndpointUrl, @Method, @Host, @PathTemplate,
                @RequiredHeaders, @AuthType, @AuthDetails, @RequestBodySchema, @ResponseBodySchema,
                @Notes, @DiscoveredAt, @LastSeenAt, @CallCount)
            ON CONFLICT(service_name, method, path_template)
            DO UPDATE SET
                last_seen_at = @LastSeenAt,
                call_count = call_count + 1,
                endpoint_url = @EndpointUrl,
                required_headers = COALESCE(@RequiredHeaders, required_headers),
                request_body_schema = COALESCE(@RequestBodySchema, request_body_schema),
                response_body_schema = COALESCE(@ResponseBodySchema, response_body_schema)
            """,
            new
            {
                record.ServiceName,
                record.EndpointUrl,
                record.Method,
                record.Host,
                record.PathTemplate,
                record.RequiredHeaders,
                record.AuthType,
                record.AuthDetails,
                record.RequestBodySchema,
                record.ResponseBodySchema,
                record.Notes,
                record.DiscoveredAt,
                record.LastSeenAt,
                record.CallCount
            });
    }

    public async Task<IReadOnlyList<ApiEndpointRecord>> GetEndpointsAsync(string serviceName, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        var rows = await connection.QueryAsync<ApiEndpointRecord>(
            """
            SELECT id as Id, service_name as ServiceName, endpoint_url as EndpointUrl, method as Method,
                   host as Host, path_template as PathTemplate, required_headers as RequiredHeaders,
                   auth_type as AuthType, auth_details as AuthDetails,
                   request_body_schema as RequestBodySchema, response_body_schema as ResponseBodySchema,
                   notes as Notes, discovered_at as DiscoveredAt, last_seen_at as LastSeenAt, call_count as CallCount
            FROM api_endpoints
            WHERE service_name = @ServiceName
            ORDER BY path_template
            """,
            new { ServiceName = serviceName });

        return rows.ToList();
    }

    public async Task UpsertWsProtocolAsync(WsProtocolRecord record, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO ws_protocols (service_name, connection_url, subprotocol, frame_type, direction,
                example_payload, payload_schema, notes, discovered_at)
            VALUES (@ServiceName, @ConnectionUrl, @Subprotocol, @FrameType, @Direction,
                @ExamplePayload, @PayloadSchema, @Notes, @DiscoveredAt)
            ON CONFLICT(service_name, frame_type, direction)
            DO UPDATE SET
                example_payload = @ExamplePayload,
                payload_schema = COALESCE(@PayloadSchema, payload_schema)
            """,
            new
            {
                record.ServiceName,
                record.ConnectionUrl,
                record.Subprotocol,
                record.FrameType,
                record.Direction,
                record.ExamplePayload,
                record.PayloadSchema,
                record.Notes,
                record.DiscoveredAt
            });
    }

    public async Task<IReadOnlyList<WsProtocolRecord>> GetWsProtocolsAsync(string serviceName, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        var rows = await connection.QueryAsync<WsProtocolRecord>(
            """
            SELECT id as Id, service_name as ServiceName, connection_url as ConnectionUrl,
                   subprotocol as Subprotocol, frame_type as FrameType, direction as Direction,
                   example_payload as ExamplePayload, payload_schema as PayloadSchema,
                   notes as Notes, discovered_at as DiscoveredAt
            FROM ws_protocols
            WHERE service_name = @ServiceName
            ORDER BY frame_type, direction
            """,
            new { ServiceName = serviceName });

        return rows.ToList();
    }
}
