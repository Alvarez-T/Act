namespace YFex.System.Windows.Security.Process;

public interface IProcessWatcher : IDisposable
{
    IObservable<ProcessEvent> Events { get; }
    bool IsRunning { get; }
    void Start();
    void Stop();
}
