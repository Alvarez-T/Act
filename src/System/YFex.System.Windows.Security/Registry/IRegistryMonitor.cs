namespace YFex.System.Windows.Security.Registry;

public interface IRegistryMonitor : global::System.IDisposable
{
    global::System.IObservable<RegistryChangeEvent> Events { get; }
    void Start();
    void Stop();
}
