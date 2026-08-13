using System.Collections.Concurrent;
using YFex.Security.Network;

namespace YFex.Security.Capture.Correlation;

public sealed class ProcessSocketCorrelator
{
    private readonly INetworkInspector _inspector;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new();
    private readonly TimeSpan _ttl;

    public ProcessSocketCorrelator(INetworkInspector inspector, TimeSpan? cacheTtl = null)
    {
        _inspector = inspector;
        _ttl = cacheTtl ?? TimeSpan.FromSeconds(2);
    }

    public int? GetOwnerPid(string localAddr, int localPort, string remoteAddr, int remotePort)
    {
        string key = $"{localAddr}:{localPort}-{remoteAddr}:{remotePort}";

        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Pid;

        RefreshCache();

        return _cache.TryGetValue(key, out var updated) ? updated.Pid : null;
    }

    public string? GetProcessName(int pid)
    {
        try
        {
            using var proc = global::System.Diagnostics.Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshCache()
    {
        var expiry = DateTimeOffset.UtcNow + _ttl;

        try
        {
            var connections = _inspector.GetTcpConnections();
            foreach (var conn in connections)
            {
                string key = $"{conn.LocalAddress}:{conn.LocalPort}-{conn.RemoteAddress}:{conn.RemotePort}";
                _cache[key] = new CachedEntry(conn.OwningPid, expiry);

                string reverseKey = $"{conn.RemoteAddress}:{conn.RemotePort}-{conn.LocalAddress}:{conn.LocalPort}";
                _cache[reverseKey] = new CachedEntry(conn.OwningPid, expiry);
            }
        }
        catch { }

        try
        {
            var endpoints = _inspector.GetUdpEndpoints();
            foreach (var ep in endpoints)
            {
                string prefix = $"{ep.LocalAddress}:{ep.LocalPort}-";
                _cache[prefix] = new CachedEntry(ep.OwningPid, expiry);
            }
        }
        catch { }
    }

    private readonly record struct CachedEntry(int Pid, DateTimeOffset ExpiresAt);
}
