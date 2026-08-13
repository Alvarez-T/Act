using System.Diagnostics;
using YFex.System.Consent;
using YFex.System.Platform;
using YFex.System.Telemetry;
using YFex.System.Unions;
using YFex.System.Windows.Interop;

namespace YFex.System.Windows;

public sealed class WindowsActiveWindowDetector : IActiveWindowDetector
{
    private readonly ITelemetryConsent _consent;

    public WindowsActiveWindowDetector(ITelemetryConsent consent)
    {
        _consent = consent;
    }

    public DataAvailability<ActiveWindowInfo> GetActiveWindow()
    {
        if (!_consent.IsAllowed(ConsentLevel.Full))
            return new Denied("Requires full consent", ConsentLevel.Full);

        var hWnd = User32.GetForegroundWindow();
        if (hWnd == nint.Zero)
        {
            return new Available<ActiveWindowInfo>(
                new ActiveWindowInfo("None", 0, new Unsupported("Windows", "No foreground window")),
                DateTimeOffset.UtcNow);
        }

        User32.GetWindowThreadProcessId(hWnd, out uint processId);

        string processName;
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            processName = proc.ProcessName;
        }
        catch
        {
            processName = "Unknown";
        }

        DataAvailability<string> title = new Available<string>(
            User32.GetWindowText(hWnd),
            DateTimeOffset.UtcNow);

        return new Available<ActiveWindowInfo>(
            new ActiveWindowInfo(processName, (int)processId, title),
            DateTimeOffset.UtcNow);
    }
}
