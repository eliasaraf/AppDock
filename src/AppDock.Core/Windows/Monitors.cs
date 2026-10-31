using System.Runtime.InteropServices;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;

namespace AppDock.Core.Windows;

/// <summary>A display monitor. All coordinates are physical pixels (the process is per-monitor DPI aware).</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, Rect Bounds, Rect WorkArea, bool IsPrimary, uint Dpi)
{
    public double Scale => Dpi / 96.0;

    public int ToPixels(int dip) => (int)Math.Round(dip * Scale);

    public int ToDip(int pixels) => (int)Math.Round(pixels / Scale);
}

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var handles = new List<nint>();
        Native.EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            handles.Add(h);
            return true;
        }, 0);
        return handles.Select(FromHandle).OfType<MonitorInfo>().ToList();
    }

    public static MonitorInfo Primary() =>
        FromHandle(Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY))
        ?? throw new InvalidOperationException("No primary monitor.");

    public static MonitorInfo FromPoint(PointI p) =>
        FromHandle(Native.MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST)) ?? Primary();

    public static MonitorInfo FromWindow(nint hwnd) =>
        FromHandle(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)) ?? Primary();

    public static MonitorInfo FromRect(Rect r)
    {
        var rc = new RECT(r);
        return FromHandle(Native.MonitorFromRect(ref rc, Native.MONITOR_DEFAULTTONEAREST)) ?? Primary();
    }

    /// <summary>Find a monitor by device name (e.g. <c>\\.\DISPLAY2</c>); null if it is no longer connected.</summary>
    public static MonitorInfo? ByDeviceName(string? deviceName) =>
        string.IsNullOrEmpty(deviceName)
            ? null
            : All().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    public static MonitorInfo? FromHandle(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (!Native.GetMonitorInfoW(handle, ref info))
        {
            return null;
        }

        var dpi = Native.GetDpiForMonitor(handle, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 96u;
        return new MonitorInfo(
            handle,
            info.szDevice,
            info.rcMonitor.ToRect(),
            info.rcWork.ToRect(),
            (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
            dpi);
    }
}
