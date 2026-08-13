using YFex.System.Data;
using YFex.System.Internal;
using YFex.System.Platform;

namespace YFex.System.Windows;

public sealed class WindowsSessionMonitor : ISessionMonitor
{
    private readonly ObservableSource<SessionEvent> _source = new();
    private bool _started;
    private CancellationTokenRegistration _registration;

    public IObservable<SessionEvent> SessionEvents => _source;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _registration = ct.Register(() =>
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        });

        return Task.CompletedTask;
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        var eventType = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLock => "locked",
            Microsoft.Win32.SessionSwitchReason.SessionUnlock => "unlocked",
            Microsoft.Win32.SessionSwitchReason.RemoteConnect => "remote_connect",
            Microsoft.Win32.SessionSwitchReason.RemoteDisconnect => "remote_disconnect",
            Microsoft.Win32.SessionSwitchReason.SessionLogon => "logon",
            Microsoft.Win32.SessionSwitchReason.SessionLogoff => "logoff",
            _ => "unknown"
        };

        _source.Emit(new SessionEvent(eventType, DateTimeOffset.UtcNow));
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        var eventType = e.Mode switch
        {
            Microsoft.Win32.PowerModes.Suspend => "sleep",
            Microsoft.Win32.PowerModes.Resume => "wake",
            _ => null
        };

        if (eventType is not null)
            _source.Emit(new SessionEvent(eventType, DateTimeOffset.UtcNow));
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        _registration.Dispose();
        _source.Complete();
        return ValueTask.CompletedTask;
    }
}
