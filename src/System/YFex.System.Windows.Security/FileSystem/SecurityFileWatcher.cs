using YFex.System.Internal;

namespace YFex.System.Windows.Security.FileSystem;

public sealed class SecurityFileWatcher : IFileWatcher
{
    private static readonly string[] SuspiciousExtensions =
        ["*.exe", "*.dll", "*.bat", "*.cmd", "*.ps1", "*.vbs", "*.js", "*.msi", "*.scr", "*.pif", "*.com", "*.lnk"];

    private readonly ObservableSource<FileChangeEvent> _events = new();
    private readonly global::System.Collections.Generic.List<global::System.IO.FileSystemWatcher> _watchers = [];

    public global::System.IObservable<FileChangeEvent> Events => _events;

    public void WatchPath(string path)
    {
        if (!global::System.IO.Directory.Exists(path)) return;

        var watcher = new global::System.IO.FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = global::System.IO.NotifyFilters.FileName
                         | global::System.IO.NotifyFilters.LastWrite
                         | global::System.IO.NotifyFilters.CreationTime
        };

        foreach (var ext in SuspiciousExtensions)
            watcher.Filters.Add(ext);

        watcher.Created += (_, e) => _events.Emit(new FileCreated(e.FullPath, global::System.DateTimeOffset.UtcNow));
        watcher.Changed += (_, e) => _events.Emit(new FileModified(e.FullPath, global::System.DateTimeOffset.UtcNow));
        watcher.Deleted += (_, e) => _events.Emit(new FileDeleted(e.FullPath, global::System.DateTimeOffset.UtcNow));
        watcher.Renamed += (_, e) => _events.Emit(new FileRenamed(e.OldFullPath, e.FullPath, global::System.DateTimeOffset.UtcNow));

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    public void WatchSecurityPaths()
    {
        WatchPath(global::System.IO.Path.GetTempPath());

        var appData = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.ApplicationData);
        WatchPath(appData);

        var localAppData = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData);
        WatchPath(localAppData);

        var userStartup = global::System.IO.Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Startup");
        WatchPath(userStartup);

        var commonStartup = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.CommonStartup);
        WatchPath(commonStartup);

        var downloads = global::System.IO.Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile), "Downloads");
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
