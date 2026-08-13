using Microsoft.Extensions.Hosting;
using YFex.Security.Config;
using YFex.Security.Events;
using YFex.Security.Pipeline;
using YFex.Security.Storage;

namespace YFex.Security.Analysis;

public sealed class AnomalyDetector : BackgroundService
{
    private readonly ISecurityPipeline _pipeline;
    private readonly IBaselineRepository _baselines;
    private readonly SecurityConfig _config;

    public AnomalyDetector(ISecurityPipeline pipeline, IBaselineRepository baselines, SecurityConfig config)
    {
        _pipeline = pipeline;
        _baselines = baselines;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _pipeline.Reader;

        await foreach (var evt in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var anomalies = await DetectAnomaliesAsync(evt, stoppingToken);
                foreach (var anomaly in anomalies)
                    _pipeline.Writer.TryWrite(anomaly);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task<List<AnomalyEvent>> DetectAnomaliesAsync(ISecurityEvent evt, CancellationToken ct)
    {
        var anomalies = new List<AnomalyEvent>();

        switch (evt)
        {
            case DnsQueryEvent dns:
                await CheckNewDomain(dns, anomalies, ct);
                CheckDnsExfiltration(dns, anomalies);
                break;
            case TcpConnectEvent tcp:
                await CheckUnexpectedNetworkAccess(tcp, anomalies, ct);
                await CheckUnusualHour(tcp, anomalies, ct);
                break;
            case TlsHandshakeEvent tls:
                await CheckJa3Mismatch(tls, anomalies, ct);
                break;
            case ProcessSecurityEvent proc:
                CheckUnsignedModuleLoad(proc, anomalies);
                CheckRemoteThreadInjection(proc, anomalies);
                break;
        }

        return anomalies;
    }

    // Rule 1 — NewDomain
    private async Task CheckNewDomain(DnsQueryEvent dns, List<AnomalyEvent> anomalies, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(dns.ProcessName) || string.IsNullOrEmpty(dns.QueryName)) return;
        if (_config.AllowedDomains.Contains(dns.QueryName)) return;

        bool known = await _baselines.IsKnownAsync("domain_per_process", dns.ProcessName, dns.QueryName, ct);
        if (known) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = dns.Timestamp,
            ProcessId = dns.ProcessId,
            ProcessName = dns.ProcessName,
            AnomalyType = AnomalyType.NewDomain,
            Severity = AnomalySeverity.Medium,
            Description = $"Process '{dns.ProcessName}' contacted new domain: {dns.QueryName}",
            RelatedEventJson = dns.ToJson()
        });
    }

    // Rule 2 — UnexpectedNetworkAccess
    private async Task CheckUnexpectedNetworkAccess(TcpConnectEvent tcp, List<AnomalyEvent> anomalies, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tcp.ProcessName)) return;
        if (_config.KnownNetworkProcesses.Contains(tcp.ProcessName)) return;

        bool known = await _baselines.IsKnownAsync("network_processes", tcp.ProcessName, "active", ct);
        if (known) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = tcp.Timestamp,
            ProcessId = tcp.ProcessId,
            ProcessName = tcp.ProcessName,
            AnomalyType = AnomalyType.UnexpectedNetworkAccess,
            Severity = AnomalySeverity.High,
            Description = $"Unexpected network access by '{tcp.ProcessName}' to {tcp.RemoteAddress}:{tcp.RemotePort}",
            RelatedEventJson = tcp.ToJson()
        });
    }

    // Rule 3 — Ja3Mismatch
    private async Task CheckJa3Mismatch(TlsHandshakeEvent tls, List<AnomalyEvent> anomalies, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tls.ProcessName) || string.IsNullOrEmpty(tls.Ja3Hash)) return;

        var baseline = await _baselines.GetBaselineAsync("ja3_per_process", tls.ProcessName, ct);
        if (baseline.Count == 0) return;

        bool known = baseline.Any(b => b.Key == tls.Ja3Hash);
        if (known) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = tls.Timestamp,
            ProcessId = tls.ProcessId,
            ProcessName = tls.ProcessName,
            AnomalyType = AnomalyType.Ja3Mismatch,
            Severity = AnomalySeverity.High,
            Description = $"JA3 fingerprint change for '{tls.ProcessName}': {tls.Ja3Hash} (SNI: {tls.Sni})",
            RelatedEventJson = tls.ToJson()
        });
    }

    // Rule 4 — UnsignedModuleLoad
    private static void CheckUnsignedModuleLoad(ProcessSecurityEvent proc, List<AnomalyEvent> anomalies)
    {
        if (proc.EventType != SecurityEventType.ModuleLoad) return;
        if (proc.IsSigned is null or true) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = proc.Timestamp,
            ProcessId = proc.ProcessId,
            ProcessName = proc.ProcessName,
            AnomalyType = AnomalyType.UnsignedModuleLoad,
            Severity = AnomalySeverity.Medium,
            Description = $"Unsigned module loaded into '{proc.ProcessName}': {proc.ModulePath}",
            RelatedEventJson = proc.ToJson()
        });
    }

    // Rule 5 — RemoteThreadInjection
    private static void CheckRemoteThreadInjection(ProcessSecurityEvent proc, List<AnomalyEvent> anomalies)
    {
        if (proc.EventType != SecurityEventType.RemoteThreadCreate) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = proc.Timestamp,
            ProcessId = proc.ProcessId,
            ProcessName = proc.ProcessName,
            AnomalyType = AnomalyType.RemoteThreadInjection,
            Severity = AnomalySeverity.Critical,
            Description = $"Remote thread created in '{proc.ProcessName}' (PID {proc.ProcessId})",
            RelatedEventJson = proc.ToJson()
        });
    }

    // Rule 6 — UnusualHour
    private async Task CheckUnusualHour(TcpConnectEvent tcp, List<AnomalyEvent> anomalies, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tcp.ProcessName)) return;

        string hourKey = $"hour_{tcp.Timestamp.Hour}:";
        var baseline = await _baselines.GetBaselineAsync("hourly_volume_per_process", tcp.ProcessName, ct);
        if (baseline.Count == 0) return;

        bool hasActivityThisHour = baseline.Any(b => b.Key.StartsWith(hourKey));
        if (hasActivityThisHour) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = tcp.Timestamp,
            ProcessId = tcp.ProcessId,
            ProcessName = tcp.ProcessName,
            AnomalyType = AnomalyType.UnusualHour,
            Severity = AnomalySeverity.Low,
            Description = $"Process '{tcp.ProcessName}' active at unusual hour {tcp.Timestamp.Hour}:00",
            RelatedEventJson = tcp.ToJson()
        });
    }

    // Rule 8 — DnsExfiltration
    private static void CheckDnsExfiltration(DnsQueryEvent dns, List<AnomalyEvent> anomalies)
    {
        if (string.IsNullOrEmpty(dns.QueryName)) return;

        bool suspicious = false;
        string reason = "";

        if (dns.QueryName.Length > 60)
        {
            suspicious = true;
            reason = $"unusually long DNS query ({dns.QueryName.Length} chars)";
        }

        int subdomainCount = dns.QueryName.Count(c => c == '.');
        if (subdomainCount > 10)
        {
            suspicious = true;
            reason = $"excessive subdomain depth ({subdomainCount} levels)";
        }

        if (!suspicious)
        {
            var parts = dns.QueryName.Split('.');
            foreach (var part in parts)
            {
                if (part.Length > 20 && LooksLikeBase64(part))
                {
                    suspicious = true;
                    reason = "base64-like subdomain segments";
                    break;
                }
            }
        }

        if (!suspicious) return;

        anomalies.Add(new AnomalyEvent
        {
            Timestamp = dns.Timestamp,
            ProcessId = dns.ProcessId,
            ProcessName = dns.ProcessName,
            AnomalyType = AnomalyType.DnsExfiltration,
            Severity = AnomalySeverity.High,
            Description = $"Possible DNS exfiltration by '{dns.ProcessName}': {reason} — {dns.QueryName}",
            RelatedEventJson = dns.ToJson()
        });
    }

    private static bool LooksLikeBase64(string segment)
    {
        if (segment.Length < 8) return false;

        int alphaNum = 0;
        int upper = 0;
        int lower = 0;
        int digit = 0;

        foreach (char c in segment)
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_')
                alphaNum++;
            if (char.IsUpper(c)) upper++;
            if (char.IsLower(c)) lower++;
            if (char.IsDigit(c)) digit++;
        }

        if (alphaNum < segment.Length * 0.9) return false;
        return upper > 0 && lower > 0 && digit > 0;
    }
}
