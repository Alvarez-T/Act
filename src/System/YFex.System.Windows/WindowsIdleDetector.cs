using System.Runtime.InteropServices;
using YFex.System.Data;
using YFex.System.Platform;
using YFex.System.Unions;
using YFex.System.Windows.Interop;

namespace YFex.System.Windows;

public sealed class WindowsIdleDetector : IIdleDetector
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);

    public DataAvailability<IdleState> GetCurrentIdleState()
    {
        var info = new User32.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<User32.LASTINPUTINFO>()
        };

        if (!User32.GetLastInputInfo(ref info))
            throw new InvalidOperationException("GetLastInputInfo failed");

        uint currentTick = unchecked((uint)Environment.TickCount);
        uint idleMs = currentTick - info.dwTime;
        var idle = TimeSpan.FromMilliseconds(idleMs);

        return new Available<IdleState>(
            new IdleState(idle, idle > IdleThreshold),
            DateTimeOffset.UtcNow);
    }
}
