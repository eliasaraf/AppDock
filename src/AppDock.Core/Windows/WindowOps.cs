using System.Runtime.InteropServices;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;

namespace AppDock.Core.Windows;

/// <summary>
/// Typed operations on top-level windows of any process. "Visible rect" means the frame the user sees, excluding
/// the invisible DWM resize borders that GetWindowRect includes on Windows 10/11.
/// </summary>
public static class WindowOps
{
    public static bool Exists(nint hwnd) => hwnd != 0 && Native.IsWindow(hwnd);

    public static bool IsVisible(nint hwnd) => Native.IsWindowVisible(hwnd);

    public static bool IsMinimized(nint hwnd) => Native.IsIconic(hwnd);

    public static bool IsMaximized(nint hwnd) => Native.IsZoomed(hwnd);

    public static Rect GetWindowRect(nint hwnd) =>
        Native.GetWindowRect(hwnd, out var rc) ? rc.ToRect() : default;

    public static Rect GetVisibleRect(nint hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) == 0)
        {
            var r = ext.ToRect();
            if (!r.IsEmpty)
            {
                return r;
            }
        }

        return GetWindowRect(hwnd);
    }

    /// <summary>The invisible border between GetWindowRect and the visible frame.</summary>
    public static Margins GetFrameMargins(nint hwnd)
    {
        var window = GetWindowRect(hwnd);
        var visible = GetVisibleRect(hwnd);
        if (window.IsEmpty || visible.IsEmpty)
        {
            return Margins.Zero;
        }

        var m = new Margins(
            visible.Left - window.Left,
            visible.Top - window.Top,
            window.Right - visible.Right,
            window.Bottom - visible.Bottom);

        // Guard against nonsense values while a window is minimized or transitioning.
        return m.Left is < 0 or > 64 || m.Top is < 0 or > 64 || m.Right is < 0 or > 64 || m.Bottom is < 0 or > 64
            ? Margins.Zero
            : m;
    }

    /// <summary>Position the window so its visible frame is exactly <paramref name="visible"/>.</summary>
    public static bool SetVisibleRect(nint hwnd, Rect visible, Margins margins, bool async = false, bool keepSize = false)
    {
        var r = visible.Expand(margins);
        var flags = Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER;
        if (async)
        {
            flags |= Native.SWP_ASYNCWINDOWPOS;
        }

        if (keepSize)
        {
            flags |= Native.SWP_NOSIZE;
        }

        return Native.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Width, r.Height, flags);
    }

    public static bool SetVisibleRect(nint hwnd, Rect visible) =>
        SetVisibleRect(hwnd, visible, GetFrameMargins(hwnd));

    public static bool IsTopmost(nint hwnd) =>
        (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & (nint)Native.WS_EX_TOPMOST) != 0;

    public static void SetTopmost(nint hwnd, bool topmost) =>
        Native.SetWindowPos(hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

    public static void Show(nint hwnd, bool activate) =>
        Native.ShowWindow(hwnd, activate ? Native.SW_SHOW : Native.SW_SHOWNA);

    public static void Hide(nint hwnd) => Native.ShowWindow(hwnd, Native.SW_HIDE);

    public static void Restore(nint hwnd) => Native.ShowWindow(hwnd, Native.SW_RESTORE);

    /// <summary>
    /// Bring a window of another process to the foreground. AppDock is not the foreground process when the hotkey
    /// fires, so attach to the foreground thread's input queue to be allowed to change focus.
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var self = Native.GetCurrentThreadId();
        var fgThread = foreground != 0 ? Native.GetWindowThreadProcessId(foreground, out _) : 0;
        var attached = fgThread != 0 && fgThread != self && Native.AttachThreadInput(self, fgThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(self, fgThread, false);
            }
        }
    }

    public static uint GetProcessId(nint hwnd)
    {
        _ = Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static unsafe string GetTitle(nint hwnd)
    {
        var buffer = stackalloc char[512];
        var len = Native.GetWindowTextW(hwnd, buffer, 512);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    public static unsafe string GetClassName(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var len = Native.GetClassNameW(hwnd, buffer, 256);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    public static nint GetForeground() => Native.GetForegroundWindow();

    public static nint GetOwner(nint hwnd) => Native.GetWindow(hwnd, Native.GW_OWNER);

    public static nint GetRoot(nint hwnd) => Native.GetAncestor(hwnd, Native.GA_ROOT);

    public static bool IsToolWindow(nint hwnd) =>
        (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & (nint)Native.WS_EX_TOOLWINDOW) != 0;

    public static bool IsCloaked(nint hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static nint WindowFromPoint(PointI p) => Native.WindowFromPoint(new POINT { X = p.X, Y = p.Y });

    public static PointI GetCursorPos() =>
        Native.GetCursorPos(out var p) ? new PointI(p.X, p.Y) : default;

    /// <summary>True when Windows' "Animation effects" setting is on.</summary>
    public static bool SystemAnimationsEnabled() =>
        !Native.SystemParametersInfoW(Native.SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) || enabled != 0;

    /// <summary>Whether Windows Snap ("window arranging") is on.</summary>
    public static bool IsWindowsSnapEnabled() =>
        !Native.SystemParametersInfoW(Native.SPI_GETWINARRANGING, 0, out var enabled, 0) || enabled != 0;

    /// <summary>
    /// Turn Windows Snap on/off for the session without persisting it to the user profile (fWinIni = 0), so a
    /// crash can never leave the user's saved setting changed.
    /// </summary>
    public static void SetWindowsSnapEnabled(bool enabled) =>
        Native.SystemParametersInfoSet(Native.SPI_SETWINARRANGING, enabled ? 1u : 0u, 0, 0);

    /// <summary>Enumerate all top-level windows.</summary>
    public static IReadOnlyList<nint> EnumerateTopLevel()
    {
        var list = new List<nint>(256);
        Native.EnumWindows((h, _) =>
        {
            list.Add(h);
            return true;
        }, 0);
        return list;
    }

    /// <summary>Apply Windows 11 rounded corners to one of our own windows (no-op on Windows 10).</summary>
    public static void SetRoundedCorners(nint hwnd)
    {
        var pref = Native.DWMWCP_ROUND;
        _ = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>Make one of our own windows click-through, non-activating and absent from Alt+Tab.</summary>
    public static void MakeOverlay(nint hwnd)
    {
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        ex |= (nint)(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex);
    }

    /// <summary>Place one of our own windows without activating it and keep it above everything.</summary>
    public static void PlaceTopmost(nint hwnd, Rect r) =>
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
}
