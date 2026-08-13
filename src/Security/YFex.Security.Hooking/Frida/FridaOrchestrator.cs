using System.Collections.Concurrent;
using System.Text.Json;
using YFex.Security.Config;
using YFex.Security.Events;
using YFex.Security.Pipeline;

namespace YFex.Security.Hooking.Frida;

/// <summary>
/// [PERSONAL USE] Spawns and manages Frida CLI sessions for pre-TLS interception.
///
/// Frida runs as <c>frida -p PID -l script.js</c>. The hook script writes JSON lines
/// to stdout; this reads them, deserializes, and emits <see cref="ISecurityEvent"/>
/// records onto the shared pipeline.
///
/// Requires: <c>pip install frida-tools</c>.
/// </summary>
public sealed class FridaOrchestrator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISecurityPipeline _pipeline;
    private readonly SecurityConfig _config;
    private readonly string _scriptsDirectory;
    private readonly ConcurrentDictionary<int, global::System.Diagnostics.Process> _activeSessions = new();

    public FridaOrchestrator(ISecurityPipeline pipeline, SecurityConfig config, string? scriptsDirectory = null)
    {
        _pipeline = pipeline;
        _config = config;
        _scriptsDirectory = scriptsDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "tools", "frida-scripts");
    }

    public IReadOnlyCollection<int> ActiveSessions => _activeSessions.Keys.ToList();

    /// <summary>Attach to a running process by PID using the given (or auto-detected) script.</summary>
    public bool Attach(int pid, HookScript script = HookScript.Auto)
    {
        if (_activeSessions.ContainsKey(pid)) return true;

        string scriptName = ResolveScript(pid, script);
        string scriptPath = Path.Combine(_scriptsDirectory, scriptName);

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Frida hook script not found: {scriptPath}");

        var psi = new global::System.Diagnostics.ProcessStartInfo
        {
            FileName = _config.FridaPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(pid.ToString());
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--runtime=v8");

        var process = new global::System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnStdoutLine(pid, e.Data);
        process.Exited += (_, _) => _activeSessions.TryRemove(pid, out _);

        if (!process.Start())
            return false;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _activeSessions[pid] = process;
        return true;
    }

    public void Detach(int pid)
    {
        if (_activeSessions.TryRemove(pid, out var process))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
        }
    }

    private void OnStdoutLine(int pid, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line[0] != '{') return; // skip Frida banner / non-JSON noise

        try
        {
            var message = JsonSerializer.Deserialize<FridaMessage>(line, JsonOptions);
            if (message is null) return;

            var evt = ConvertToEvent(pid, message);
            if (evt is not null)
                _pipeline.Writer.TryWrite(evt);
        }
        catch (JsonException) { }
    }

    private static ISecurityEvent? ConvertToEvent(int pid, FridaMessage message)
    {
        var data = message.Data;
        int effectivePid = message.Pid != 0 ? message.Pid : pid;

        return message.Type switch
        {
            "http_request" or "http_response" when data is not null => new HttpCaptureEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProcessId = effectivePid,
                Method = data.Method ?? "",
                Url = data.Url ?? "",
                Host = data.Host ?? "",
                Path = data.Path ?? "",
                RequestHeaders = data.Headers ?? new(),
                RequestBody = data.Body,
                ResponseStatusCode = data.Status,
                Source = CaptureSource.Hook
            },
            "ws_frame" when data is not null => new WebSocketFrameEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProcessId = effectivePid,
                ConnectionUrl = data.Url ?? "",
                Direction = string.Equals(data.Direction, "received", StringComparison.OrdinalIgnoreCase)
                    ? WsDirection.Received : WsDirection.Sent,
                Opcode = (WsOpcode)(data.Opcode ?? 1),
                TextPayload = data.Payload,
                PayloadLength = data.Payload?.Length ?? 0
            },
            _ => null
        };
    }

    private string ResolveScript(int pid, HookScript script)
    {
        if (script != HookScript.Auto)
            return ScriptFileName(script);

        // Auto-detect TLS library by scanning loaded modules.
        try
        {
            using var process = global::System.Diagnostics.Process.GetProcessById(pid);
            foreach (global::System.Diagnostics.ProcessModule module in process.Modules)
            {
                string name = module.ModuleName.ToLowerInvariant();
                if (name is "ssleay32.dll" or "libssl-1_1.dll" or "libssl-3.dll" || name.Contains("boringssl"))
                    return ScriptFileName(HookScript.OpenSsl);
                if (name is "sspicli.dll" or "schannel.dll" or "ncrypt.dll" or "bcrypt.dll")
                    return ScriptFileName(HookScript.Schannel);
            }
        }
        catch { }

        return ScriptFileName(HookScript.Schannel);
    }

    private static string ScriptFileName(HookScript script) => script switch
    {
        HookScript.Schannel => "hook-schannel.js",
        HookScript.OpenSsl => "hook-openssl.js",
        HookScript.WebSocket => "hook-websocket.js",
        HookScript.StripPinning => "strip-pinning.js",
        _ => "hook-schannel.js"
    };

    public void Dispose()
    {
        foreach (var pid in _activeSessions.Keys.ToList())
            Detach(pid);
    }
}

public enum HookScript { Auto, Schannel, OpenSsl, WebSocket, StripPinning }
