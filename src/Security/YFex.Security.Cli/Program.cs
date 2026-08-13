using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YFex.Security.Analysis;
using YFex.Security.Cli;
using YFex.Security.Config;
using YFex.Security.Events;
using YFex.Security.Service;
using YFex.Security.Storage;

if (args.Length == 0)
{
    CliHelp.Print();
    return 0;
}

var config = SecurityConfigLoader.Load();
string command = args[0].ToLowerInvariant();

try
{
    return command switch
    {
        "start" => await RunStart(config, args),
        "status" => await RunStatus(config),
        "query" => await RunQuery(config, args),
        "report" => await RunReport(config, args),
        "baseline" => await RunBaseline(config, args),
        "anomalies" => await RunAnomalies(config, args),
        "allowlist" => RunAllowlist(config, args),
        "purge" => await RunPurge(config, args),
        "help" or "--help" or "-h" => Help(),
        _ => Unknown(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task<IHost> BuildMigratedHostAsync(SecurityConfig config)
{
    var host = SecurityHostBuilder.Create(config).Build();
    await new MigrationHostedService(config).StartAsync(CancellationToken.None);
    return host;
}

static int Help() { CliHelp.Print(); return 0; }

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    CliHelp.Print();
    return 1;
}

static async Task<int> RunStart(SecurityConfig config, string[] args)
{
    bool enablePcap = args.Contains("--pcap", StringComparer.OrdinalIgnoreCase);
    Console.WriteLine("Starting YFex Security service (Ctrl+C to stop)...");
    var host = SecurityHostBuilder.Create(config, enablePcap).Build();
    await host.RunAsync();
    return 0;
}

static async Task<int> RunStatus(SecurityConfig config)
{
    using var host = await BuildMigratedHostAsync(config);
    var repo = host.Services.GetRequiredService<IEventRepository>();

    var today = DateTimeOffset.UtcNow.Date;
    var todayStart = new DateTimeOffset(today, TimeSpan.Zero);

    var events = await repo.QueryEventsAsync(new EventQuery { After = todayStart, Limit = 100_000 });
    var anomalies = events.OfType<AnomalyEvent>().ToList();

    Console.WriteLine($"Database:       {Environment.ExpandEnvironmentVariables(config.DatabasePath)}");
    Console.WriteLine($"Events today:   {events.Count}");
    Console.WriteLine($"Anomalies today: {anomalies.Count}");
    if (anomalies.Count > 0)
    {
        var bySeverity = anomalies.GroupBy(a => a.Severity)
            .OrderByDescending(g => g.Key);
        foreach (var group in bySeverity)
            Console.WriteLine($"  {group.Key}: {group.Count()}");
    }
    return 0;
}

static async Task<int> RunQuery(SecurityConfig config, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: yfex-security query <events|connections|dns> [filters]");
        return 1;
    }

    string subject = args[1].ToLowerInvariant();
    var a = ArgMap.Parse(args, 2);

    using var host = await BuildMigratedHostAsync(config);
    var repo = host.Services.GetRequiredService<IEventRepository>();

    var query = new EventQuery
    {
        ProcessName = a.Get("process"),
        After = a.GetDate("after"),
        Before = a.GetDate("before"),
        RemoteAddress = a.Get("remote"),
        Domain = a.Get("domain"),
        Limit = a.GetInt("limit") ?? 100,
        EventType = subject switch
        {
            "connections" => SecurityEventType.TcpConnect,
            "dns" => SecurityEventType.DnsQuery,
            _ => a.Get("type") is { } t && Enum.TryParse<SecurityEventType>(t, true, out var et) ? et : null
        }
    };

    var results = await repo.QueryEventsAsync(query);
    Console.WriteLine($"{results.Count} result(s):");
    foreach (var evt in results)
        Console.WriteLine($"[{evt.Timestamp:u}] {evt.EventType,-16} {evt.ProcessName,-24} {evt.ToJson()}");

    return 0;
}

static async Task<int> RunReport(SecurityConfig config, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: yfex-security report <daily|process|api|security> [args]");
        return 1;
    }

    string kind = args[1].ToLowerInvariant();
    var a = ArgMap.Parse(args, 2);

    using var host = await BuildMigratedHostAsync(config);
    var generator = host.Services.GetRequiredService<ReportGenerator>();

    string report = kind switch
    {
        "daily" => await generator.GenerateDailySummaryAsync(
            a.GetDate("date") is { } d ? DateOnly.FromDateTime(d.Date) : DateOnly.FromDateTime(DateTime.UtcNow)),
        "process" => await generator.GenerateProcessProfileAsync(
            a.Positional(0) ?? a.Get("name") ?? throw new ArgumentException("process name required")),
        "api" => await generator.GenerateServiceApiReportAsync(
            a.Positional(0) ?? a.Get("service") ?? throw new ArgumentException("service name required")),
        "security" => await generator.GenerateSecurityAuditAsync(
            a.GetDate("from") ?? DateTimeOffset.UtcNow.AddDays(-7),
            a.GetDate("to") ?? DateTimeOffset.UtcNow),
        _ => throw new ArgumentException($"Unknown report type: {kind}")
    };

    string? outPath = a.Get("output");
    if (outPath is not null)
    {
        await File.WriteAllTextAsync(outPath, report);
        Console.WriteLine($"Report written to {outPath}");
    }
    else
    {
        Console.WriteLine(report);
    }

    return 0;
}

static async Task<int> RunBaseline(SecurityConfig config, string[] args)
{
    var a = ArgMap.Parse(args, 1);
    string action = a.Positional(0) ?? "rebuild";

    if (action != "rebuild")
    {
        Console.Error.WriteLine("Usage: yfex-security baseline rebuild [--type <type>|all]");
        return 1;
    }

    using var host = await BuildMigratedHostAsync(config);
    var builder = host.Services.GetRequiredService<BaselineBuilder>();

    string type = a.Get("type", "all");
    if (type == "all")
    {
        Console.WriteLine("Rebuilding all baselines...");
        await builder.RebuildAllBaselinesAsync();
    }
    else
    {
        Console.WriteLine($"Rebuilding baseline: {type}");
        await builder.RebuildBaselineAsync(type);
    }
    Console.WriteLine("Done.");
    return 0;
}

static async Task<int> RunAnomalies(SecurityConfig config, string[] args)
{
    var a = ArgMap.Parse(args, 1);

    using var host = await BuildMigratedHostAsync(config);
    var repo = host.Services.GetRequiredService<IEventRepository>();

    var events = await repo.QueryEventsAsync(new EventQuery
    {
        EventType = SecurityEventType.AnomalyDetected,
        Limit = a.GetInt("limit") ?? 100
    });

    var anomalies = events.OfType<AnomalyEvent>().AsEnumerable();

    if (a.Get("severity") is { } sev && Enum.TryParse<AnomalySeverity>(sev, true, out var severity))
        anomalies = anomalies.Where(x => x.Severity == severity);

    var list = anomalies.ToList();
    Console.WriteLine($"{list.Count} anomalies:");
    foreach (var x in list)
        Console.WriteLine($"[{x.Severity,-8}] [{x.Timestamp:u}] {x.ProcessName,-24} {x.Description}");

    return 0;
}

static int RunAllowlist(SecurityConfig config, string[] args)
{
    var a = ArgMap.Parse(args, 1);
    string subject = a.Positional(0) ?? "";
    string action = a.Positional(1) ?? "list";
    string? value = a.Positional(2);

    switch (subject, action)
    {
        case ("domain", "add") when value is not null:
            config.AllowedDomains.Add(value);
            SecurityConfigLoader.Save(config);
            Console.WriteLine($"Added domain to allowlist: {value}");
            break;
        case ("domain", "list"):
            foreach (var d in config.AllowedDomains) Console.WriteLine(d);
            break;
        case ("process", "add") when value is not null:
            config.KnownNetworkProcesses.Add(value);
            SecurityConfigLoader.Save(config);
            Console.WriteLine($"Added process to allowlist: {value}");
            break;
        case ("process", "list"):
            foreach (var p in config.KnownNetworkProcesses) Console.WriteLine(p);
            break;
        default:
            Console.Error.WriteLine("Usage: yfex-security allowlist <domain|process> <add|list> [value]");
            return 1;
    }

    return 0;
}

static async Task<int> RunPurge(SecurityConfig config, string[] args)
{
    var a = ArgMap.Parse(args, 1);
    string olderThan = a.Get("older-than", $"{config.RetentionDays}d");

    int days = ParseDays(olderThan) ?? config.RetentionDays;
    var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

    using var host = await BuildMigratedHostAsync(config);
    var repo = host.Services.GetRequiredService<IEventRepository>();

    await repo.PurgeOlderThanAsync(cutoff);
    Console.WriteLine($"Purged events older than {days} days (before {cutoff:u}).");
    return 0;
}

static int? ParseDays(string value)
{
    value = value.Trim().ToLowerInvariant();
    if (value.EndsWith('d') && int.TryParse(value[..^1], out var d)) return d;
    if (int.TryParse(value, out var n)) return n;
    return null;
}
