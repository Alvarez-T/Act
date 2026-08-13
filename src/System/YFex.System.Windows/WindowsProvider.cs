using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using YFex.System.Data;
using YFex.System.Platform;

namespace YFex.System.Windows;

public sealed class WindowsProvider : IPlatformProvider
{
    public WindowsProvider(
        IIdleDetector idleDetector,
        ISessionMonitor sessionMonitor,
        IInstalledSoftwareReader installedSoftware,
        IBrowserDataReader browserData,
        IHardwareFingerprintProvider fingerprint,
        IActiveWindowDetector activeWindow,
        IDisplayInfoProvider display)
    {
        IdleDetector = idleDetector;
        SessionMonitor = sessionMonitor;
        InstalledSoftware = installedSoftware;
        BrowserData = browserData;
        Fingerprint = fingerprint;
        ActiveWindow = activeWindow;
        Display = display;
    }

    public IIdleDetector IdleDetector { get; }
    public ISessionMonitor SessionMonitor { get; }
    public IInstalledSoftwareReader InstalledSoftware { get; }
    public IBrowserDataReader BrowserData { get; }
    public IHardwareFingerprintProvider Fingerprint { get; }
    public IActiveWindowDetector ActiveWindow { get; }
    public IDisplayInfoProvider Display { get; }

    public SystemSnapshot CaptureSystemSnapshot()
    {
        var assembly = global::System.Reflection.Assembly.GetEntryAssembly();
        using var process = Process.GetCurrentProcess();

        return new SystemSnapshot
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            Is64BitOs = Environment.Is64BitOperatingSystem,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = process.WorkingSet64,
            GcTotalMemory = GC.GetTotalMemory(false),
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            UserDomainName = Environment.UserDomainName,
            IsElevated = IsRunningAsAdmin(),
            AppVersion = assembly?.GetName().Version?.ToString() ?? "0.0.0",
            AppPath = Environment.ProcessPath ?? "",
            AppUptimeMs = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds
        };
    }

    public NetworkSnapshot CaptureNetworkSnapshot()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(a =>
            {
                var mac = a.GetPhysicalAddress().ToString();
                var hashedMac = mac.Length > 0
                    ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mac))).ToLowerInvariant()
                    : "";

                string[] maskedIps;
                try
                {
                    maskedIps = a.GetIPProperties().UnicastAddresses
                        .Select(u => MaskIpAddress(u.Address.ToString()))
                        .ToArray();
                }
                catch
                {
                    maskedIps = [];
                }

                return new AdapterInfo(
                    a.Name,
                    hashedMac,
                    maskedIps,
                    a.Speed,
                    a.OperationalStatus == OperationalStatus.Up);
            })
            .ToArray();

        return new NetworkSnapshot(
            adapters,
            NetworkInterface.GetIsNetworkAvailable());
    }

    public LocaleSnapshot CaptureLocaleSnapshot()
    {
        var culture = global::System.Globalization.CultureInfo.CurrentCulture;
        var uiCulture = global::System.Globalization.CultureInfo.CurrentUICulture;
        var tz = TimeZoneInfo.Local;

        return new LocaleSnapshot(
            culture.Name,
            uiCulture.Name,
            tz.Id,
            (int)tz.BaseUtcOffset.TotalMinutes,
            global::System.Globalization.CultureInfo.InstalledUICulture.KeyboardLayoutId.ToString());
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string MaskIpAddress(string ip)
    {
        if (ip.Contains('.'))
        {
            var parts = ip.Split('.');
            if (parts.Length == 4)
                return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";
        }
        if (ip.Contains(':'))
        {
            var colonIdx = ip.LastIndexOf(':');
            if (colonIdx > 0)
                return ip[..colonIdx] + ":xxxx";
        }
        return "xxx";
    }
}
