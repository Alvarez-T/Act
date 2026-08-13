using MsReg = Microsoft.Win32;
using YFex.System.Internal;

namespace YFex.System.Windows.Security.Registry;

public sealed class RegistryMonitor : IRegistryMonitor
{
    private static readonly (MsReg.RegistryKey Root, string Path)[] MonitoredKeys =
    [
        (MsReg.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (MsReg.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (MsReg.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"),
    ];

    private readonly ObservableSource<RegistryChangeEvent> _events = new();
    private readonly global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.Dictionary<string, string?>> _baseline = new();
    private global::System.Threading.CancellationTokenSource? _cts;
    private global::System.Threading.Tasks.Task? _pollTask;
    private readonly global::System.TimeSpan _pollInterval;

    public global::System.IObservable<RegistryChangeEvent> Events => _events;

    public RegistryMonitor(global::System.TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? global::System.TimeSpan.FromSeconds(30);
    }

    public void Start()
    {
        TakeBaseline();
        _cts = new global::System.Threading.CancellationTokenSource();
        _pollTask = PollAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private void TakeBaseline()
    {
        _baseline.Clear();
        foreach (var (root, path) in MonitoredKeys)
        {
            string fullPath = FormatKeyPath(root, path);
            _baseline[fullPath] = ReadKeyValues(root, path);
        }
    }

    private async global::System.Threading.Tasks.Task PollAsync(global::System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await global::System.Threading.Tasks.Task.Delay(_pollInterval, ct); }
            catch (global::System.OperationCanceledException) { break; }

            foreach (var (root, path) in MonitoredKeys)
            {
                string fullPath = FormatKeyPath(root, path);
                var current = ReadKeyValues(root, path);

                if (!_baseline.TryGetValue(fullPath, out var previous))
                {
                    _baseline[fullPath] = current;
                    if (current.Count > 0)
                        _events.Emit(new KeyCreated(fullPath, global::System.DateTimeOffset.UtcNow));
                    continue;
                }

                foreach (var (name, value) in current)
                {
                    if (!previous.TryGetValue(name, out var oldValue))
                    {
                        _events.Emit(new ValueChanged(fullPath, name, null, value, global::System.DateTimeOffset.UtcNow));
                    }
                    else if (oldValue != value)
                    {
                        _events.Emit(new ValueChanged(fullPath, name, oldValue, value, global::System.DateTimeOffset.UtcNow));
                    }
                }

                foreach (var name in previous.Keys)
                {
                    if (!current.ContainsKey(name))
                        _events.Emit(new ValueChanged(fullPath, name, previous[name], null, global::System.DateTimeOffset.UtcNow));
                }

                _baseline[fullPath] = current;
            }
        }
    }

    private static global::System.Collections.Generic.Dictionary<string, string?> ReadKeyValues(
        MsReg.RegistryKey root, string path)
    {
        var values = new global::System.Collections.Generic.Dictionary<string, string?>();
        try
        {
            using var key = root.OpenSubKey(path, false);
            if (key is null) return values;

            foreach (var name in key.GetValueNames())
            {
                values[name] = key.GetValue(name)?.ToString();
            }
        }
        catch { }
        return values;
    }

    private static string FormatKeyPath(MsReg.RegistryKey root, string path)
    {
        string rootName = root.Name;
        return $"{rootName}\\{path}";
    }

    public void Dispose() => Stop();
}
