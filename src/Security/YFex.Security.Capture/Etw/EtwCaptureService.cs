using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using YFex.Security.Capture.Correlation;
using YFex.Security.Events;
using YFex.Security.Pipeline;

namespace YFex.Security.Capture.Etw;

public sealed class EtwCaptureService : BackgroundService
{
    private const string SessionName = "YFex-Security-ETW";

    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private static readonly Guid TcpIpProvider = new("2F07E2EE-15DB-40F1-90EF-9D7BA282188A");

    private readonly ISecurityPipeline _pipeline;
    private readonly ProcessSocketCorrelator _correlator;
    private TraceEventSession? _session;

    public EtwCaptureService(ISecurityPipeline pipeline, ProcessSocketCorrelator correlator)
    {
        _pipeline = pipeline;
        _correlator = correlator;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() => RunSession(stoppingToken), stoppingToken);
    }

    private void RunSession(CancellationToken ct)
    {
        _session = new TraceEventSession(SessionName);

        ct.Register(() =>
        {
            try { _session?.Stop(); } catch { }
        });

        _session.EnableProvider(DnsClientProvider, TraceEventLevel.Informational);
        _session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational);
        _session.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational);
        _session.EnableProvider(TcpIpProvider, TraceEventLevel.Informational);

        _session.Source.Dynamic.All += OnEvent;

        _session.Source.Process();
    }

    private void OnEvent(TraceEvent data)
    {
        try
        {
            ISecurityEvent? evt = data.ProviderGuid switch
            {
                var g when g == DnsClientProvider => HandleDnsEvent(data),
                var g when g == KernelNetworkProvider => HandleNetworkEvent(data),
                var g when g == KernelProcessProvider => HandleProcessEvent(data),
                var g when g == TcpIpProvider => HandleTcpIpEvent(data),
                _ => null
            };

            if (evt is not null)
                _pipeline.Writer.TryWrite(evt);
        }
        catch { }
    }

    private ISecurityEvent? HandleDnsEvent(TraceEvent data)
    {
        if (data.EventName is not ("EventID(3006)" or "QueryCompleted"))
            return null;

        string queryName = data.PayloadStringByName("QueryName") ?? "";
        if (string.IsNullOrEmpty(queryName)) return null;

        string queryType = data.PayloadStringByName("QueryType") ?? "A";
        string? results = data.PayloadStringByName("QueryResults");
        string[] resolved = results?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];

        return new DnsQueryEvent
        {
            Timestamp = data.TimeStamp,
            ProcessId = data.ProcessID,
            ProcessName = _correlator.GetProcessName(data.ProcessID) ?? "",
            QueryName = queryName.TrimEnd('.'),
            QueryType = queryType,
            ResolvedAddresses = resolved
        };
    }

    private ISecurityEvent? HandleNetworkEvent(TraceEvent data)
    {
        if (!data.EventName.Contains("Connect", StringComparison.OrdinalIgnoreCase) &&
            !data.EventName.Contains("Accept", StringComparison.OrdinalIgnoreCase))
            return null;

        string localAddr = data.PayloadStringByName("LocalAddress") ?? "";
        string remoteAddr = data.PayloadStringByName("RemoteAddress") ?? "";

        if (string.IsNullOrEmpty(remoteAddr)) return null;

        int localPort = (int)(data.PayloadByName("LocalPort") ?? 0);
        int remotePort = (int)(data.PayloadByName("RemotePort") ?? 0);

        return new TcpConnectEvent
        {
            Timestamp = data.TimeStamp,
            ProcessId = data.ProcessID,
            ProcessName = _correlator.GetProcessName(data.ProcessID) ?? "",
            LocalAddress = localAddr,
            LocalPort = localPort,
            RemoteAddress = remoteAddr,
            RemotePort = remotePort
        };
    }

    private ISecurityEvent? HandleProcessEvent(TraceEvent data)
    {
        SecurityEventType? eventType = data.Opcode switch
        {
            TraceEventOpcode.Start => SecurityEventType.ProcessStart,
            TraceEventOpcode.Stop => SecurityEventType.ProcessStop,
            _ when data.EventName.Contains("ImageLoad") => SecurityEventType.ModuleLoad,
            _ => null
        };

        if (eventType is null) return null;

        string imagePath = data.PayloadStringByName("ImageFileName")
                           ?? data.PayloadStringByName("ImageName") ?? "";
        string commandLine = data.PayloadStringByName("CommandLine") ?? "";
        int parentPid = (int)(data.PayloadByName("ParentId")
                              ?? data.PayloadByName("ParentProcessID") ?? 0);

        return new ProcessSecurityEvent
        {
            Timestamp = data.TimeStamp,
            ProcessId = data.ProcessID,
            ProcessName = _correlator.GetProcessName(data.ProcessID) ?? "",
            EventType = eventType.Value,
            ParentProcessId = parentPid,
            ImagePath = imagePath,
            CommandLine = commandLine
        };
    }

    private ISecurityEvent? HandleTcpIpEvent(TraceEvent data)
    {
        if (!data.EventName.Contains("Send", StringComparison.OrdinalIgnoreCase) &&
            !data.EventName.Contains("Recv", StringComparison.OrdinalIgnoreCase))
            return null;

        string localAddr = data.PayloadStringByName("saddr") ?? "";
        string remoteAddr = data.PayloadStringByName("daddr") ?? "";
        int localPort = (int)(data.PayloadByName("sport") ?? 0);
        int remotePort = (int)(data.PayloadByName("dport") ?? 0);

        if (string.IsNullOrEmpty(remoteAddr)) return null;

        return new TcpConnectEvent
        {
            Timestamp = data.TimeStamp,
            ProcessId = data.ProcessID,
            ProcessName = _correlator.GetProcessName(data.ProcessID) ?? "",
            LocalAddress = localAddr,
            LocalPort = localPort,
            RemoteAddress = remoteAddr,
            RemotePort = remotePort
        };
    }

    public override void Dispose()
    {
        _session?.Dispose();
        base.Dispose();
    }
}
