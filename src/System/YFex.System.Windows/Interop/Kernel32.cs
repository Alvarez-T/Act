using System.Runtime.InteropServices;

namespace YFex.System.Windows.Interop;

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetTickCount();
}
