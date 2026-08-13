using YFex.System.Internal;
using SysDiag = global::System.Diagnostics;

namespace YFex.System.Windows.Security.Process;

public sealed class ProcessWatcher : IProcessWatcher
{
    private readonly ObservableSource<ProcessEvent> _events = new();
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private volatile bool _running;

    public ProcessWatcher(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public IObservable<ProcessEvent> Events => _events;
    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _pollTask = PollAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _cts?.Cancel();

        try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch { }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var known = new Dictionary<int, string>();
        foreach (var p in SysDiag.Process.GetProcesses())
        {
            try { known[p.Id] = p.ProcessName; }
            catch { }
            finally { p.Dispose(); }
        }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { break; }

            var current = new Dictionary<int, string>();
            foreach (var p in SysDiag.Process.GetProcesses())
            {
                try
                {
                    current[p.Id] = p.ProcessName;
                    if (!known.ContainsKey(p.Id))
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; }
                        catch { }

                        ProcessEvent evt = new ProcessCreated(p.Id, p.ProcessName, path, DateTimeOffset.UtcNow);
                        _events.Emit(evt);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            foreach (var (pid, name) in known)
            {
                if (!current.ContainsKey(pid))
                {
                    ProcessEvent evt = new ProcessTerminated(pid, DateTimeOffset.UtcNow);
                    _events.Emit(evt);
                }
            }

            known = current;
        }
    }

    public void Dispose()
    {
        Stop();
        _events.Complete();
    }
}
