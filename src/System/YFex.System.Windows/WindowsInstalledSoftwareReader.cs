using Microsoft.Win32;
using YFex.System.Consent;
using YFex.System.Data;
using YFex.System.Platform;
using YFex.System.Telemetry;
using YFex.System.Unions;

namespace YFex.System.Windows;

public sealed class WindowsInstalledSoftwareReader : IInstalledSoftwareReader
{
    private readonly ITelemetryConsent _consent;

    public WindowsInstalledSoftwareReader(ITelemetryConsent consent)
    {
        _consent = consent;
    }

    public DataAvailability<InstalledAppList> GetInstalledSoftware()
    {
        if (!_consent.IsAllowed(ConsentLevel.Full))
            return new Denied("Requires full consent", ConsentLevel.Full);

        var apps = new List<InstalledApp>();

        ReadUninstallKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstallKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstallKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);

        return new Available<InstalledAppList>(
            new InstalledAppList(apps),
            DateTimeOffset.UtcNow);
    }

    private static void ReadUninstallKey(RegistryKey root, string path, List<InstalledApp> apps)
    {
        using var key = root.OpenSubKey(path);
        if (key is null) return;

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            try
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                var name = subKey.GetValue("DisplayName") as string;
                if (string.IsNullOrEmpty(name)) continue;

                apps.Add(new InstalledApp(
                    name,
                    subKey.GetValue("DisplayVersion") as string ?? "",
                    subKey.GetValue("Publisher") as string ?? "",
                    subKey.GetValue("InstallDate") as string ?? ""));
            }
            catch { }
        }
    }
}
