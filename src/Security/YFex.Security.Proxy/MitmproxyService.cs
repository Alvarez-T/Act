using System.Text.Json;
using Microsoft.Extensions.Hosting;
using YFex.Security.Config;
using YFex.Security.Events;
using YFex.Security.Pipeline;

namespace YFex.Security.Proxy;

/// <summary>
/// [PERSONAL USE] Manages a <c>mitmdump</c> subprocess as a TLS-terminating proxy.
/// The bundled Python addon emits JSON lines per HTTP flow / WebSocket message;
/// this reads them and pushes <see cref="ISecurityEvent"/> records onto the pipeline.
///
/// On first run mitmproxy generates a CA cert under its confdir; trust it (or hook
/// <c>strip-pinning.js</c>) for cert-pinned targets. Only captures apps that use the
/// system proxy. Requires <c>pip install mitmproxy</c>.
/// </summary>
public sealed class MitmproxyService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ISecurityPipeline _pipeline;
    private readonly SecurityConfig _config;
    private readonly string _addonPath;
    private global::System.Diagnostics.Process? _process;

    public MitmproxyService(ISecurityPipeline pipeline, SecurityConfig config, string? addonPath = null)
    {
        _pipeline = pipeline;
        _config = config;
        _addonPath = addonPath
            ?? Path.Combine(AppContext.BaseDirectory, "tools", "mitmproxy-addons", "capture_addon.py");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var psi = new global::System.Diagnostics.ProcessStartInfo
        {
            FileName = _config.MitmdumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(_config.ProxyPort.ToString());
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(_addonPath);
        psi.ArgumentList.Add("--set");
        psi.ArgumentList.Add("flow_detail=0");
        psi.ArgumentList.Add("-q"); // quiet — addon owns stdout

        _process = new global::System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnStdoutLine(e.Data);

        stoppingToken.Register(() =>
        {
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch { }
        });

        if (!_process.Start())
            return Task.CompletedTask;

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        return _process.WaitForExitAsync(stoppingToken);
    }

    private void OnStdoutLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return;

        try
        {
            var message = JsonSerializer.Deserialize<ProxyMessage>(line, JsonOptions);
            if (message is null) return;

            var evt = ConvertToEvent(message);
            if (evt is not null)
                _pipeline.Writer.TryWrite(evt);
        }
        catch (JsonException) { }
    }

    private static ISecurityEvent? ConvertToEvent(ProxyMessage message) => message.Type switch
    {
        "http" => new HttpCaptureEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Method = message.Method ?? "",
            Url = message.Url ?? "",
            Host = message.Host ?? "",
            Path = message.Path ?? "",
            QueryString = message.QueryString ?? "",
            RequestHeaders = message.RequestHeaders ?? new(),
            RequestBody = message.RequestBody,
            RequestContentType = message.RequestContentType,
            ResponseStatusCode = message.ResponseStatus,
            ResponseHeaders = message.ResponseHeaders,
            ResponseBody = message.ResponseBody,
            ResponseContentType = message.ResponseContentType,
            DurationMs = message.DurationMs,
            Source = CaptureSource.Proxy
        },
        "websocket" => new WebSocketFrameEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            ConnectionUrl = message.Url ?? "",
            Direction = string.Equals(message.Direction, "received", StringComparison.OrdinalIgnoreCase)
                ? WsDirection.Received : WsDirection.Sent,
            Opcode = (WsOpcode)(message.Opcode ?? 1),
            TextPayload = message.Payload,
            PayloadLength = message.Payload?.Length ?? 0
        },
        _ => null
    };

    public override void Dispose()
    {
        _process?.Dispose();
        base.Dispose();
    }
}
