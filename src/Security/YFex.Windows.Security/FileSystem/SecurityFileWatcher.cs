using YFex.Security.FileSystem;
using YFex.System.Internal;

namespace YFex.Windows.Security.FileSystem;

public sealed class SecurityFileWatcher : IFileWatcher
{
    private static readonly string[] SuspiciousExtensions =
        ["*.exe", "*.dll", "*.bat", "*.cmd", "*.ps1", "*.vbs", "*.js", "*.msi", "*.scr", "*.pif", "*.com", "*.lnk"];

    private readonly ObservableSource<FileChangeEvent> _events = new();
    private readonly List<global::System.IO.FileSystemWatcher> _watchers = [];

    public IObservable<FileChangeEvent> Events => _events;

    public void WatchPath(string path)
    {
        if (!Directory.Exists(path)) return;

        var watcher = new global::System.IO.FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.CreationTime
        };

        foreach (var ext in SuspiciousExtensions)
            watcher.Filters.Add(ext);

        watcher.Created += (_, e) => _events.Emit(new FileCreated(e.FullPath, DateTimeOffset.UtcNow));
        watcher.Changed += (_, e) => _events.Emit(new FileModified(e.FullPath, DateTimeOffset.UtcNow));
        watcher.Deleted += (_, e) => _events.Emit(new FileDeleted(e.FullPath, DateTimeOffset.UtcNow));
        watcher.Renamed += (_, e) => _events.Emit(new FileRenamed(e.OldFullPath, e.FullPath, DateTimeOffset.UtcNow));

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    public void WatchSecurityPaths()
    {
        WatchPath(Path.GetTempPath());

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        WatchPath(appData);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        WatchPath(localAppData);

        var userStartup = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Startup");
        WatchPath(userStartup);

        var commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        WatchPath(commonStartup);

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        WatchPath(downloads);
    }

    public void Stop()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    public void Dispose() => Stop();
}
