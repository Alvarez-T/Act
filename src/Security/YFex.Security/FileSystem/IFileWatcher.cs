namespace YFex.Security.FileSystem;

public interface IFileWatcher : IDisposable
{
    IObservable<FileChangeEvent> Events { get; }
    void WatchPath(string path);
    void WatchSecurityPaths();
    void Stop();
}
