namespace YFex.Windows.Security.Registry;

public interface IRegistryMonitor : IDisposable
{
    IObservable<RegistryChangeEvent> Events { get; }
    void Start();
    void Stop();
}
