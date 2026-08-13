using YFex.Security.Events;
using YFex.Security.Storage;

namespace YFex.Security.Analysis;

public sealed class BaselineBuilder
{
    private readonly IEventRepository _events;
    private readonly IBaselineRepository _baselines;
    private readonly int _windowDays;

    public BaselineBuilder(IEventRepository events, IBaselineRepository baselines, int windowDays = 14)
    {
        _events = events;
        _baselines = baselines;
        _windowDays = windowDays;
    }

    public async Task RebuildAllBaselinesAsync(CancellationToken ct = default)
    {
        await RebuildBaselineAsync("domain_per_process", ct);
        await RebuildBaselineAsync("ja3_per_process", ct);
        await RebuildBaselineAsync("hourly_volume_per_process", ct);
        await RebuildBaselineAsync("listening_ports", ct);
        await RebuildBaselineAsync("network_processes", ct);
    }

    public async Task RebuildBaselineAsync(string baselineType, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_windowDays);

        switch (baselineType)
        {
            case "domain_per_process":
                await BuildDomainPerProcessAsync(cutoff, ct);
                break;
            case "ja3_per_process":
                await BuildJa3PerProcessAsync(cutoff, ct);
                break;
            case "hourly_volume_per_process":
                await BuildHourlyVolumeAsync(cutoff, ct);
                break;
            case "listening_ports":
                await BuildListeningPortsAsync(cutoff, ct);
                break;
            case "network_processes":
                await BuildNetworkProcessesAsync(cutoff, ct);
                break;
        }
    }

    private async Task BuildDomainPerProcessAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var dnsEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = cutoff,
            EventType = SecurityEventType.DnsQuery,
            Limit = 100_000
        }, ct);

        var pairs = new HashSet<(string Process, string Domain)>();

        foreach (var evt in dnsEvents)
        {
            if (evt is DnsQueryEvent dns && !string.IsNullOrEmpty(dns.ProcessName))
                pairs.Add((dns.ProcessName, dns.QueryName));
        }

        foreach (var (process, domain) in pairs)
            await _baselines.UpsertBaselineEntryAsync("domain_per_process", process, domain, ct);
    }

    private async Task BuildJa3PerProcessAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var tlsEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = cutoff,
            EventType = SecurityEventType.TlsHandshake,
            Limit = 100_000
        }, ct);

        var pairs = new HashSet<(string Process, string Ja3)>();

        foreach (var evt in tlsEvents)
        {
            if (evt is TlsHandshakeEvent tls && !string.IsNullOrEmpty(tls.ProcessName) && !string.IsNullOrEmpty(tls.Ja3Hash))
                pairs.Add((tls.ProcessName, tls.Ja3Hash));
        }

        foreach (var (process, ja3) in pairs)
            await _baselines.UpsertBaselineEntryAsync("ja3_per_process", process, ja3, ct);
    }

    private async Task BuildHourlyVolumeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var connEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = cutoff,
            EventType = SecurityEventType.TcpConnect,
            Limit = 100_000
        }, ct);

        var hourlyGroups = new Dictionary<(string Process, int Hour), int>();

        foreach (var evt in connEvents)
        {
            if (string.IsNullOrEmpty(evt.ProcessName)) continue;
            var key = (evt.ProcessName, evt.Timestamp.Hour);
            hourlyGroups[key] = hourlyGroups.GetValueOrDefault(key) + 1;
        }

        foreach (var ((process, hour), count) in hourlyGroups)
            await _baselines.UpsertBaselineEntryAsync("hourly_volume_per_process", process, $"hour_{hour}:{count}", ct);
    }

    private async Task BuildListeningPortsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var connEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = cutoff,
            EventType = SecurityEventType.TcpConnect,
            Limit = 100_000
        }, ct);

        var ports = new HashSet<(string Process, string Port)>();

        foreach (var evt in connEvents)
        {
            if (evt is TcpConnectEvent tcp && !string.IsNullOrEmpty(tcp.ProcessName))
                ports.Add((tcp.ProcessName, tcp.LocalPort.ToString()));
        }

        foreach (var (process, port) in ports)
            await _baselines.UpsertBaselineEntryAsync("listening_ports", process, port, ct);
    }

    private async Task BuildNetworkProcessesAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var connEvents = await _events.QueryEventsAsync(new EventQuery
        {
            After = cutoff,
            EventType = SecurityEventType.TcpConnect,
            Limit = 100_000
        }, ct);

        var processes = new HashSet<string>();

        foreach (var evt in connEvents)
        {
            if (!string.IsNullOrEmpty(evt.ProcessName))
                processes.Add(evt.ProcessName);
        }

        foreach (var process in processes)
            await _baselines.UpsertBaselineEntryAsync("network_processes", process, "active", ct);
    }
}
