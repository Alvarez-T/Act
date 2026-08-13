namespace YFex.Windows.Security.Input;

public interface ISystemInputMonitor : IDisposable
{
    IObservable<InputEvent> Events { get; }
    bool IsRunning { get; }
    void Start();
    void Stop();
}
