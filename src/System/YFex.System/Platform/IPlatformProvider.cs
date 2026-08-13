using YFex.System.Data;

namespace YFex.System.Platform;

public interface IPlatformProvider
{
    IIdleDetector IdleDetector { get; }
    ISessionMonitor SessionMonitor { get; }
    IInstalledSoftwareReader InstalledSoftware { get; }
    IBrowserDataReader BrowserData { get; }
    IHardwareFingerprintProvider Fingerprint { get; }
    IActiveWindowDetector ActiveWindow { get; }
    IDisplayInfoProvider Display { get; }

    SystemSnapshot CaptureSystemSnapshot();
    NetworkSnapshot CaptureNetworkSnapshot();
    LocaleSnapshot CaptureLocaleSnapshot();
}
