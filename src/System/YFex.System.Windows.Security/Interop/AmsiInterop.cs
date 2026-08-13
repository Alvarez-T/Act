using System.Runtime.InteropServices;

namespace YFex.System.Windows.Security.Interop;

internal static partial class AmsiInterop
{
    internal const int AMSI_RESULT_CLEAN = 0;
    internal const int AMSI_RESULT_NOT_DETECTED = 1;
    internal const int AMSI_RESULT_BLOCKED_BY_ADMIN_START = 0x4000;
    internal const int AMSI_RESULT_BLOCKED_BY_ADMIN_END = 0x4FFF;
    internal const int AMSI_RESULT_DETECTED = 0x8000;

    [DllImport("amsi.dll", CharSet = CharSet.Unicode)]
    internal static extern int AmsiInitialize(string appName, out nint amsiContext);

    [DllImport("amsi.dll")]
    internal static extern int AmsiScanBuffer(
        nint amsiContext,
        nint buffer,
        uint length,
        [MarshalAs(UnmanagedType.LPWStr)] string contentName,
        nint amsiSession,
        out int result);

    [DllImport("amsi.dll")]
    internal static extern void AmsiUninitialize(nint amsiContext);
}
