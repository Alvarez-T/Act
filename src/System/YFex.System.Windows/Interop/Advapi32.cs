using System.Runtime.InteropServices;

namespace YFex.System.Windows.Interop;

internal static partial class Advapi32
{
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint ProcessHandle,
        uint DesiredAccess,
        out nint TokenHandle);

    internal const uint TOKEN_QUERY = 0x0008;
}
