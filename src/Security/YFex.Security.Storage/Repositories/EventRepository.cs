using System.Text;
using System.Text.Json;
using Dapper;
using YFex.Security.Events;

namespace YFex.Security.Storage.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly SecurityDbConnectionFactory _factory;

    public EventRepository(SecurityDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task InsertEventAsync(ISecurityEvent evt, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);
        await InsertSingleAsync(connection, evt, null);
    }

    public async Task InsertBatchAsync(IReadOnlyList<ISecurityEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        await using var connection = await _factory.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var evt in events)
            await InsertSingleAsync(connection, evt, transaction);

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ISecurityEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);

        var sb = new StringBuilder(
            "SELECT id AS Id, timestamp AS Timestamp, process_id AS ProcessId, process_name AS ProcessName, event_type AS EventType, detail_json AS DetailJson FROM events WHERE 1=1");
        var parameters = new DynamicParameters();

        if (query.After is { } after)
        {
            sb.Append(" AND timestamp >= @After");
            parameters.Add("After", after.ToString("O"));
        }

        if (query.Before is { } before)
        {
            sb.Append(" AND timestamp <= @Before");
            parameters.Add("Before", before.ToString("O"));
        }

        if (query.ProcessName is { } processName)
        {
            sb.Append(" AND process_name = @ProcessName");
            parameters.Add("ProcessName", processName);
        }

        if (query.EventType is { } eventType)
        {
            sb.Append(" AND event_type = @EventType");
            parameters.Add("EventType", eventType.ToString());
        }

        sb.Append(" ORDER BY timestamp DESC LIMIT @Limit OFFSET @Offset");
        parameters.Add("Limit", query.Limit);
        parameters.Add("Offset", query.Offset);

        var rows = await connection.QueryAsync<EventRow>(sb.ToString(), parameters);
        return rows.Select(DeserializeEvent).Where(e => e is not null).Cast<ISecurityEvent>().ToList();
    }

    public async Task PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct);
        string cutoffStr = cutoff.ToString("O");

        string[] tables = ["events", "dns_queries", "connections", "http_captures",
            "ws_frames", "tls_handshakes", "process_events", "anomalies"];

        foreach (var table in tables)
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE timestamp < @Cutoff", new { Cutoff = cutoffStr });
    }

    private static async Task InsertSingleAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection,
        ISecurityEvent evt,
        global::System.Data.Common.DbTransaction? transaction)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO events (timestamp, process_id, process_name, event_type, detail_json)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @EventType, @DetailJson)
            """,
            new
            {
                Timestamp = evt.Timestamp.ToString("O"),
                evt.ProcessId,
                evt.ProcessName,
                EventType = evt.EventType.ToString(),
                DetailJson = evt.ToJson()
            },
            transaction);

        switch (evt)
        {
            case DnsQueryEvent dns:
                await InsertDnsAsync(connection, dns, transaction);
                break;
            case TcpConnectEvent tcp:
                await InsertConnectionAsync(connection, tcp, transaction);
                break;
            case HttpCaptureEvent http:
                await InsertHttpAsync(connection, http, transaction);
                break;
            case WebSocketFrameEvent ws:
                await InsertWsFrameAsync(connection, ws, transaction);
                break;
            case TlsHandshakeEvent tls:
                await InsertTlsAsync(connection, tls, transaction);
                break;
            case ProcessSecurityEvent proc:
                await InsertProcessEventAsync(connection, proc, transaction);
                break;
            case AnomalyEvent anomaly:
                await InsertAnomalyAsync(connection, anomaly, transaction);
                break;
        }
    }

    private static Task InsertDnsAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, DnsQueryEvent dns, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO dns_queries (timestamp, process_id, process_name, query_name, query_type, resolved_ips)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @QueryName, @QueryType, @ResolvedIps)
            """,
            new
            {
                Timestamp = dns.Timestamp.ToString("O"),
                dns.ProcessId,
                dns.ProcessName,
                dns.QueryName,
                dns.QueryType,
                ResolvedIps = string.Join(",", dns.ResolvedAddresses)
            },
            tx);

    private static Task InsertConnectionAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, TcpConnectEvent tcp, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO connections (timestamp, process_id, process_name, local_address, local_port, remote_address, remote_port, protocol)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @LocalAddress, @LocalPort, @RemoteAddress, @RemotePort, @Protocol)
            """,
            new
            {
                Timestamp = tcp.Timestamp.ToString("O"),
                tcp.ProcessId,
                tcp.ProcessName,
                tcp.LocalAddress,
                tcp.LocalPort,
                tcp.RemoteAddress,
                tcp.RemotePort,
                tcp.Protocol
            },
            tx);

    private static Task InsertHttpAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, HttpCaptureEvent http, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO http_captures (timestamp, process_id, process_name, method, url, host, path, query_string,
                request_headers, request_body, request_content_type, response_status, response_headers, response_body,
                response_content_type, response_size_bytes, duration_ms, capture_source)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @Method, @Url, @Host, @Path, @QueryString,
                @RequestHeaders, @RequestBody, @RequestContentType, @ResponseStatus, @ResponseHeaders, @ResponseBody,
                @ResponseContentType, @ResponseSizeBytes, @DurationMs, @CaptureSource)
            """,
            new
            {
                Timestamp = http.Timestamp.ToString("O"),
                http.ProcessId,
                http.ProcessName,
                http.Method,
                http.Url,
                http.Host,
                http.Path,
                http.QueryString,
                RequestHeaders = JsonSerializer.Serialize(http.RequestHeaders),
                http.RequestBody,
                http.RequestContentType,
                ResponseStatus = http.ResponseStatusCode,
                ResponseHeaders = http.ResponseHeaders is not null ? JsonSerializer.Serialize(http.ResponseHeaders) : null,
                http.ResponseBody,
                http.ResponseContentType,
                http.ResponseSizeBytes,
                http.DurationMs,
                CaptureSource = http.Source.ToString()
            },
            tx);

    private static Task InsertWsFrameAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, WebSocketFrameEvent ws, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO ws_frames (timestamp, process_id, process_name, connection_url, direction, opcode, text_payload, binary_payload, payload_length)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @ConnectionUrl, @Direction, @Opcode, @TextPayload, @BinaryPayload, @PayloadLength)
            """,
            new
            {
                Timestamp = ws.Timestamp.ToString("O"),
                ws.ProcessId,
                ws.ProcessName,
                ws.ConnectionUrl,
                Direction = ws.Direction.ToString(),
                Opcode = (int)ws.Opcode,
                ws.TextPayload,
                ws.BinaryPayload,
                ws.PayloadLength
            },
            tx);

    private static Task InsertTlsAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, TlsHandshakeEvent tls, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO tls_handshakes (timestamp, process_id, process_name, sni, cipher_suite, tls_version, ja3_hash, ja3s_hash, cert_subject, cert_issuer, cert_expiry)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @Sni, @CipherSuite, @TlsVersion, @Ja3Hash, @Ja3sHash, @CertSubject, @CertIssuer, @CertExpiry)
            """,
            new
            {
                Timestamp = tls.Timestamp.ToString("O"),
                tls.ProcessId,
                tls.ProcessName,
                tls.Sni,
                tls.CipherSuite,
                tls.TlsVersion,
                tls.Ja3Hash,
                tls.Ja3sHash,
                tls.CertSubject,
                tls.CertIssuer,
                CertExpiry = tls.CertExpiry?.ToString("O")
            },
            tx);

    private static Task InsertProcessEventAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, ProcessSecurityEvent proc, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO process_events (timestamp, process_id, process_name, event_type, parent_process_id, image_path, command_line, module_path, is_signed, signer_name)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @EventType, @ParentProcessId, @ImagePath, @CommandLine, @ModulePath, @IsSigned, @SignerName)
            """,
            new
            {
                Timestamp = proc.Timestamp.ToString("O"),
                proc.ProcessId,
                proc.ProcessName,
                EventType = proc.EventType.ToString(),
                proc.ParentProcessId,
                proc.ImagePath,
                proc.CommandLine,
                proc.ModulePath,
                IsSigned = proc.IsSigned.HasValue ? (proc.IsSigned.Value ? 1 : 0) : (int?)null,
                proc.SignerName
            },
            tx);

    private static Task InsertAnomalyAsync(
        global::Microsoft.Data.Sqlite.SqliteConnection connection, AnomalyEvent anomaly, global::System.Data.Common.DbTransaction? tx) =>
        connection.ExecuteAsync(
            """
            INSERT INTO anomalies (timestamp, process_id, process_name, anomaly_type, description, severity, related_event)
            VALUES (@Timestamp, @ProcessId, @ProcessName, @AnomalyType, @Description, @Severity, @RelatedEvent)
            """,
            new
            {
                Timestamp = anomaly.Timestamp.ToString("O"),
                anomaly.ProcessId,
                anomaly.ProcessName,
                AnomalyType = anomaly.AnomalyType.ToString(),
                anomaly.Description,
                Severity = anomaly.Severity.ToString(),
                RelatedEvent = anomaly.RelatedEventJson
            },
            tx);

    private static ISecurityEvent? DeserializeEvent(EventRow row)
    {
        try
        {
            if (!Enum.TryParse<SecurityEventType>(row.EventType, out var eventType))
                return null;

            return eventType switch
            {
                SecurityEventType.DnsQuery or SecurityEventType.DnsResponse =>
                    JsonSerializer.Deserialize<DnsQueryEvent>(row.DetailJson),
                SecurityEventType.TcpConnect or SecurityEventType.TcpDisconnect =>
                    JsonSerializer.Deserialize<TcpConnectEvent>(row.DetailJson),
                SecurityEventType.TlsHandshake =>
                    JsonSerializer.Deserialize<TlsHandshakeEvent>(row.DetailJson),
                SecurityEventType.HttpRequest or SecurityEventType.HttpResponse =>
                    JsonSerializer.Deserialize<HttpCaptureEvent>(row.DetailJson),
                SecurityEventType.WebSocketFrame =>
                    JsonSerializer.Deserialize<WebSocketFrameEvent>(row.DetailJson),
                SecurityEventType.GrpcCall =>
                    JsonSerializer.Deserialize<GrpcCallEvent>(row.DetailJson),
                SecurityEventType.ProcessStart or SecurityEventType.ProcessStop or
                SecurityEventType.ModuleLoad or SecurityEventType.RemoteThreadCreate =>
                    JsonSerializer.Deserialize<ProcessSecurityEvent>(row.DetailJson),
                SecurityEventType.AnomalyDetected or SecurityEventType.BaselineDeviation =>
                    JsonSerializer.Deserialize<AnomalyEvent>(row.DetailJson),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class EventRow
    {
        public long Id { get; init; }
        public string Timestamp { get; init; } = "";
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = "";
        public string EventType { get; init; } = "";
        public string DetailJson { get; init; } = "";
    }
}
