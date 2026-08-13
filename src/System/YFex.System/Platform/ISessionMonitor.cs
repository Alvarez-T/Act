using YFex.System.Data;

namespace YFex.System.Platform;

public interface ISessionMonitor : IAsyncDisposable
{
    IObservable<SessionEvent> SessionEvents { get; }
    Task StartAsync(CancellationToken ct);
}
