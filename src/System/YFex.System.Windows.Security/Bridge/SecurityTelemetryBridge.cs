using YFex.System.Telemetry;
using YFex.System.Windows.Security.FileSystem;
using YFex.System.Windows.Security.Process;
using YFex.System.Windows.Security.Registry;

namespace YFex.System.Windows.Security.Bridge;

public sealed class SecurityTelemetryBridge : global::System.IDisposable
{
    private readonly ITelemetryCollector _collector;
    private readonly global::System.Collections.Generic.List<global::System.IDisposable> _subscriptions = [];

    public SecurityTelemetryBridge(ITelemetryCollector collector)
    {
        _collector = collector;
    }

    public void SubscribeProcessEvents(IProcessWatcher watcher)
    {
        var sub = watcher.Events.Subscribe(new ProcessObserver(_collector));
        _subscriptions.Add(sub);
    }

    public void SubscribeFileEvents(IFileWatcher fileWatcher)
    {
        var sub = fileWatcher.Events.Subscribe(new FileObserver(_collector));
        _subscriptions.Add(sub);
    }

    public void SubscribeRegistryEvents(IRegistryMonitor registryMonitor)
    {
        var sub = registryMonitor.Events.Subscribe(new RegistryObserver(_collector));
        _subscriptions.Add(sub);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
    }

    private sealed class ProcessObserver(ITelemetryCollector collector) : global::System.IObserver<ProcessEvent>
    {
        public void OnNext(ProcessEvent value)
        {
            switch (value)
            {
                case ProcessCreated e:
                    collector.Track("security.process.created", new global::System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["pid"] = e.Pid,
                        ["name"] = e.Name,
                        ["filePath"] = e.FilePath
                    });
                    break;
                case ProcessTerminated e:
                    collector.Track("security.process.terminated", new global::System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["pid"] = e.Pid
                    });
                    break;
            }
        }

        public void OnError(global::System.Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class FileObserver(ITelemetryCollector collector) : global::System.IObserver<FileChangeEvent>
    {
        public void OnNext(FileChangeEvent value)
        {
            var (eventName, props) = value switch
            {
                FileCreated e => ("security.file.created", new Dictionary<string, object?> { ["path"] = e.Path }),
                FileModified e => ("security.file.modified", new Dictionary<string, object?> { ["path"] = e.Path }),
                FileDeleted e => ("security.file.deleted", new Dictionary<string, object?> { ["path"] = e.Path }),
                FileRenamed e => ("security.file.renamed", new Dictionary<string, object?> { ["oldPath"] = e.OldPath, ["newPath"] = e.NewPath }),
                _ => ((string?)null, null)
            };

            if (eventName is not null && props is not null)
                collector.Track(eventName, props);
        }

        public void OnError(global::System.Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class RegistryObserver(ITelemetryCollector collector) : global::System.IObserver<RegistryChangeEvent>
    {
        public void OnNext(RegistryChangeEvent value)
        {
            switch (value)
            {
                case ValueChanged e:
                    collector.Track("security.registry.valueChanged", new Dictionary<string, object?>
                    {
                        ["keyPath"] = e.KeyPath,
                        ["valueName"] = e.ValueName,
                        ["oldValue"] = e.OldValue,
                        ["newValue"] = e.NewValue
                    });
                    break;
                case KeyCreated e:
                    collector.Track("security.registry.keyCreated", new Dictionary<string, object?>
                    {
                        ["keyPath"] = e.KeyPath
                    });
                    break;
                case KeyDeleted e:
                    collector.Track("security.registry.keyDeleted", new Dictionary<string, object?>
                    {
                        ["keyPath"] = e.KeyPath
                    });
                    break;
            }
        }

        public void OnError(global::System.Exception error) { }
        public void OnCompleted() { }
    }
}
