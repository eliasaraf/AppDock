using System.Runtime.InteropServices;
using AppDock.Core.Interop;

namespace AppDock.Core.Updates;

public static class UserActivity
{
    /// <summary>
    /// True if there has been no keyboard or mouse input for <paramref name="threshold"/> and nothing suggests the PC
    /// is still in use without input (full-screen app, game, presentation).
    /// </summary>
    public static bool IsIdleFor(TimeSpan threshold)
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info))
        {
            return false;
        }

        // Both are 32-bit tick counts, so unsigned subtraction stays correct when the counter wraps after 49 days.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        if (idleMs < threshold.TotalMilliseconds)
        {
            return false;
        }

        return Native.SHQueryUserNotificationState(out var state) == 0 &&
               state is Native.QUNS_NOT_PRESENT or Native.QUNS_ACCEPTS_NOTIFICATIONS or Native.QUNS_QUIET_TIME;
    }
}
