using System.Text;
using YFex.Security.Events;
using YFex.Security.Storage;

namespace YFex.Security.Analysis;

public sealed class ReportGenerator
{
    private readonly IEventRepository _events;
    private readonly IBaselineRepository _baselines;
    private readonly IApiCatalogRepository _apiCatalog;

    public ReportGenerator(IEventRepository events, IBaselineRepository baselines, IApiCatalogRepository apiCatalog)
    {
        _events = events;
        _baselines = baselines;
        _apiCatalog = apiCatalog;
    }

    public async Task<string> GenerateDailySummaryAsync(DateOnly date, CancellationToken ct = default)
    {
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);

        var allEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = dayStart,
            Before = dayEnd,
            Limit = 100_000
        }, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Daily Security Summary — {date:yyyy-MM-dd}");
        sb.AppendLine();

        // Events by type
        var byType = allEvents.GroupBy(e => e.EventType)
            .OrderByDescending(g => g.Count())
            .ToList();

        sb.AppendLine("## Events by Type");
        sb.AppendLine();
        sb.AppendLine("| Type | Count |");
        sb.AppendLine("|------|-------|");
        foreach (var group in byType)
            sb.AppendLine($"| {group.Key} | {group.Count()} |");
        sb.AppendLine();

        // Top processes by event count
        var topProcesses = allEvents
            .Where(e => !string.IsNullOrEmpty(e.ProcessName))
            .GroupBy(e => e.ProcessName)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        sb.AppendLine("## Top 10 Processes");
        sb.AppendLine();
        sb.AppendLine("| Process | Events |");
        sb.AppendLine("|---------|--------|");
        foreach (var group in topProcesses)
            sb.AppendLine($"| {group.Key} | {group.Count()} |");
        sb.AppendLine();

        // Top domains
        var dnsEvents = allEvents.OfType<DnsQueryEvent>().ToList();
        if (dnsEvents.Count > 0)
        {
            var topDomains = dnsEvents
                .GroupBy(d => d.QueryName)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();

            sb.AppendLine("## Top 10 Domains");
            sb.AppendLine();
            sb.AppendLine("| Domain | Queries |");
            sb.AppendLine("|--------|---------|");
            foreach (var group in topDomains)
                sb.AppendLine($"| {group.Key} | {group.Count()} |");
            sb.AppendLine();
        }

        // Anomalies
        var anomalies = allEvents.OfType<AnomalyEvent>().ToList();
        if (anomalies.Count > 0)
        {
            sb.AppendLine("## Anomalies");
            sb.AppendLine();

            var bySeverity = anomalies.GroupBy(a => a.Severity)
                .OrderByDescending(g => g.Key)
                .ToList();

            foreach (var group in bySeverity)
            {
                sb.AppendLine($"### {group.Key} ({group.Count()})");
                sb.AppendLine();
                foreach (var a in group.Take(20))
                    sb.AppendLine($"- [{a.Timestamp:HH:mm:ss}] {a.Description}");
                sb.AppendLine();
            }
        }

        sb.AppendLine($"---");
        sb.AppendLine($"*Total events: {allEvents.Count} | Generated: {DateTimeOffset.UtcNow:u}*");

        return sb.ToString();
    }

    public async Task<string> GenerateProcessProfileAsync(string processName, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Process Profile: {processName}");
        sb.AppendLine();

        // Contacted domains
        var domainBaseline = await _baselines.GetBaselineAsync("domain_per_process", processName, ct);
        if (domainBaseline.Count > 0)
        {
            sb.AppendLine("## Known Domains");
            sb.AppendLine();
            sb.AppendLine("| Domain | First Seen | Last Seen | Count |");
            sb.AppendLine("|--------|-----------|-----------|-------|");
            foreach (var entry in domainBaseline.OrderByDescending(b => b.Count).Take(50))
                sb.AppendLine($"| {entry.Key} | {entry.FirstSeen} | {entry.LastSeen} | {entry.Count} |");
            sb.AppendLine();
        }

        // JA3 history
        var ja3Baseline = await _baselines.GetBaselineAsync("ja3_per_process", processName, ct);
        if (ja3Baseline.Count > 0)
        {
            sb.AppendLine("## TLS Fingerprints (JA3)");
            sb.AppendLine();
            sb.AppendLine("| JA3 Hash | First Seen | Last Seen |");
            sb.AppendLine("|----------|-----------|-----------|");
            foreach (var entry in ja3Baseline)
                sb.AppendLine($"| `{entry.Key}` | {entry.FirstSeen} | {entry.LastSeen} |");
            sb.AppendLine();
        }

        // Hourly activity
        var hourlyBaseline = await _baselines.GetBaselineAsync("hourly_volume_per_process", processName, ct);
        if (hourlyBaseline.Count > 0)
        {
            sb.AppendLine("## Hourly Activity");
            sb.AppendLine();
            sb.AppendLine("| Hour | Volume |");
            sb.AppendLine("|------|--------|");
            foreach (var entry in hourlyBaseline.OrderBy(b => b.Key))
                sb.AppendLine($"| {entry.Key} | {entry.Count} |");
            sb.AppendLine();
        }

        // Recent anomalies
        var recentAnomalies = await _events.QueryEventsAsync(new EventQuery
        {
            ProcessName = processName,
            EventType = SecurityEventType.AnomalyDetected,
            Limit = 20
        }, ct);

        if (recentAnomalies.Count > 0)
        {
            sb.AppendLine("## Recent Anomalies");
            sb.AppendLine();
            foreach (var evt in recentAnomalies.OfType<AnomalyEvent>())
                sb.AppendLine($"- **[{evt.Severity}]** [{evt.Timestamp:u}] {evt.Description}");
            sb.AppendLine();
        }

        sb.AppendLine($"---");
        sb.AppendLine($"*Generated: {DateTimeOffset.UtcNow:u}*");

        return sb.ToString();
    }

    public async Task<string> GenerateServiceApiReportAsync(string serviceName, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# API Report: {serviceName}");
        sb.AppendLine();

        var endpoints = await _apiCatalog.GetEndpointsAsync(serviceName, ct);
        if (endpoints.Count > 0)
        {
            sb.AppendLine("## HTTP Endpoints");
            sb.AppendLine();

            foreach (var ep in endpoints)
            {
                sb.AppendLine($"### `{ep.Method} {ep.PathTemplate}`");
                sb.AppendLine();
                sb.AppendLine($"- **Host:** {ep.Host}");
                sb.AppendLine($"- **URL:** {ep.EndpointUrl}");
                sb.AppendLine($"- **Calls:** {ep.CallCount}");
                sb.AppendLine($"- **Discovered:** {ep.DiscoveredAt} | **Last seen:** {ep.LastSeenAt}");
                if (ep.AuthType is not null)
                    sb.AppendLine($"- **Auth:** {ep.AuthType} — {ep.AuthDetails}");
                if (ep.RequiredHeaders is not null)
                    sb.AppendLine($"- **Required headers:** {ep.RequiredHeaders}");
                if (ep.RequestBodySchema is not null)
                {
                    sb.AppendLine($"- **Request schema:**");
                    sb.AppendLine($"  ```json");
                    sb.AppendLine($"  {ep.RequestBodySchema}");
                    sb.AppendLine($"  ```");
                }
                if (ep.ResponseBodySchema is not null)
                {
                    sb.AppendLine($"- **Response schema:**");
                    sb.AppendLine($"  ```json");
                    sb.AppendLine($"  {ep.ResponseBodySchema}");
                    sb.AppendLine($"  ```");
                }
                sb.AppendLine();
            }
        }

        var wsProtocols = await _apiCatalog.GetWsProtocolsAsync(serviceName, ct);
        if (wsProtocols.Count > 0)
        {
            sb.AppendLine("## WebSocket Protocols");
            sb.AppendLine();

            foreach (var ws in wsProtocols)
            {
                sb.AppendLine($"### `{ws.Direction}` — {ws.FrameType}");
                sb.AppendLine();
                sb.AppendLine($"- **URL:** {ws.ConnectionUrl}");
                if (ws.Subprotocol is not null)
                    sb.AppendLine($"- **Subprotocol:** {ws.Subprotocol}");
                if (ws.ExamplePayload is not null)
                {
                    sb.AppendLine($"- **Example:**");
                    sb.AppendLine($"  ```");
                    sb.AppendLine($"  {ws.ExamplePayload}");
                    sb.AppendLine($"  ```");
                }
                if (ws.PayloadSchema is not null)
                {
                    sb.AppendLine($"- **Schema:**");
                    sb.AppendLine($"  ```json");
                    sb.AppendLine($"  {ws.PayloadSchema}");
                    sb.AppendLine($"  ```");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine($"---");
        sb.AppendLine($"*Generated: {DateTimeOffset.UtcNow:u}*");

        return sb.ToString();
    }

    public async Task<string> GenerateSecurityAuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var allAnomalies = await _events.QueryEventsAsync(new EventQuery
        {
            After = from,
            Before = to,
            EventType = SecurityEventType.AnomalyDetected,
            Limit = 100_000
        }, ct);

        var anomalies = allAnomalies.OfType<AnomalyEvent>().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Security Audit Report");
        sb.AppendLine($"**Period:** {from:u} — {to:u}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Total anomalies:** {anomalies.Count}");

        var bySeverity = anomalies.GroupBy(a => a.Severity).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine($"- Critical: {bySeverity.GetValueOrDefault(AnomalySeverity.Critical)}");
        sb.AppendLine($"- High: {bySeverity.GetValueOrDefault(AnomalySeverity.High)}");
        sb.AppendLine($"- Medium: {bySeverity.GetValueOrDefault(AnomalySeverity.Medium)}");
        sb.AppendLine($"- Low: {bySeverity.GetValueOrDefault(AnomalySeverity.Low)}");
        sb.AppendLine();

        // By type
        var byType = anomalies.GroupBy(a => a.AnomalyType)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (byType.Count > 0)
        {
            sb.AppendLine("## Anomalies by Type");
            sb.AppendLine();
            sb.AppendLine("| Type | Count | Highest Severity |");
            sb.AppendLine("|------|-------|-----------------|");
            foreach (var group in byType)
            {
                var maxSeverity = group.Max(a => a.Severity);
                sb.AppendLine($"| {group.Key} | {group.Count()} | {maxSeverity} |");
            }
            sb.AppendLine();
        }

        // Top processes by anomaly count
        var topProcesses = anomalies
            .Where(a => !string.IsNullOrEmpty(a.ProcessName))
            .GroupBy(a => a.ProcessName)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        if (topProcesses.Count > 0)
        {
            sb.AppendLine("## Top Processes by Anomaly Count");
            sb.AppendLine();
            sb.AppendLine("| Process | Anomalies | Highest Severity |");
            sb.AppendLine("|---------|-----------|-----------------|");
            foreach (var group in topProcesses)
            {
                var maxSeverity = group.Max(a => a.Severity);
                sb.AppendLine($"| {group.Key} | {group.Count()} | {maxSeverity} |");
            }
            sb.AppendLine();
        }

        // Critical and high severity details
        var critical = anomalies.Where(a => a.Severity >= AnomalySeverity.High)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.Timestamp)
            .Take(50)
            .ToList();

        if (critical.Count > 0)
        {
            sb.AppendLine("## High & Critical Anomalies (Detail)");
            sb.AppendLine();
            foreach (var a in critical)
                sb.AppendLine($"- **[{a.Severity}]** [{a.Timestamp:u}] `{a.ProcessName}` — {a.Description}");
            sb.AppendLine();
        }

        sb.AppendLine($"---");
        sb.AppendLine($"*Generated: {DateTimeOffset.UtcNow:u}*");

        return sb.ToString();
    }
}
