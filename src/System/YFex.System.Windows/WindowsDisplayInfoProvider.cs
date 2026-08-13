using System.Runtime.InteropServices;
using YFex.System.Data;
using YFex.System.Platform;
using YFex.System.Unions;
using YFex.System.Windows.Interop;

namespace YFex.System.Windows;

public sealed class WindowsDisplayInfoProvider : IDisplayInfoProvider
{
    public DataAvailability<DisplaySnapshot> Capture()
    {
        var handles = new List<nint>();
        User32.MonitorEnumDelegate callback = (hMonitor, hdcMonitor, ref lprcMonitor, dwData) =>
        {
            handles.Add(hMonitor);
            return true;
        };
        User32.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);

        var monitors = new MonitorInfo[handles.Count == 0 ? 1 : handles.Count];

        if (handles.Count == 0)
        {
            int w = User32.GetSystemMetrics(User32.SM_CXSCREEN);
            int h = User32.GetSystemMetrics(User32.SM_CYSCREEN);
            monitors[0] = new MonitorInfo("Primary", w, h, 1.0, 96.0, true);
        }
        else
        {
            for (int i = 0; i < handles.Count; i++)
            {
                var mi = new User32.MONITORINFOEXW
                {
                    cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEXW>()
                };

                string name = $"Monitor {i + 1}";
                int width = 0, height = 0;
                bool isPrimary = false;
                uint dpiX = 96;

                if (User32.GetMonitorInfoW(handles[i], ref mi))
                {
                    width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    isPrimary = (mi.dwFlags & User32.MONITORINFOF_PRIMARY) != 0;
                    name = mi.szDevice?.TrimEnd('\0') ?? name;
                }

                try { Shcore.GetDpiForMonitor(handles[i], Shcore.MDT_EFFECTIVE_DPI, out dpiX, out _); }
                catch { /* shcore.dll unavailable on older Windows */ }

                double scaling = dpiX / 96.0;
                monitors[i] = new MonitorInfo(name, width, height, scaling, dpiX, isPrimary);
            }
        }

        string theme = DetectThemeVariant();
        string? accent = DetectAccentColor();
        bool highContrast = DetectHighContrast();

        return new Available<DisplaySnapshot>(
            new DisplaySnapshot(monitors, theme, accent, highContrast),
            DateTimeOffset.UtcNow);
    }

    private static string DetectThemeVariant()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0 ? "Dark" : "Light";
        }
        catch { return "Unknown"; }
    }

    private static string? DetectAccentColor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\DWM");
            var value = key?.GetValue("AccentColor");
            if (value is int color)
            {
                byte r = (byte)(color >> 0);
                byte g = (byte)(color >> 8);
                byte b = (byte)(color >> 16);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { }
        return null;
    }

    private static bool DetectHighContrast()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("HighContrastOn") is int i && i == 1;
        }
        catch { return false; }
    }
}
