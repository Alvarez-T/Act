namespace YFex.System.Windows.Security.FileSystem;

public interface IFileWatcher : global::System.IDisposable
{
    global::System.IObservable<FileChangeEvent> Events { get; }
    void WatchPath(string path);
    void WatchSecurityPaths();
    void Stop();
}
