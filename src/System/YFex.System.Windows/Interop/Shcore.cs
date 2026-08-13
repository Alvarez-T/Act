using System.Runtime.InteropServices;

namespace YFex.System.Windows.Interop;

internal static class Shcore
{
    internal const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
