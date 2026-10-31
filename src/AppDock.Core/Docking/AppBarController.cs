using System.Runtime.InteropServices;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;
using AppDock.Core.Windows;

namespace AppDock.Core.Docking;

/// <summary>
/// Registers a shell AppBar (like the taskbar) so Windows keeps a strip of the screen free and maximized windows
/// resize around it. The bar itself is an invisible window; the target app is positioned into the strip.
/// One controller per docked target, because the shell identifies bars by window handle.
/// </summary>
public sealed class AppBarController : IDisposable
{
    private static readonly uint s_taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    private readonly uint _callbackMessage;
    private bool _disposed;

    public AppBarController(string name)
    {
        Window = new MessageWindow($"AppDock.AppBar.{name}");
        _callbackMessage = Native.RegisterWindowMessageW("AppDock.AppBarNotify");
        Window.Message += OnMessage;
        Native.WTSRegisterSessionNotification(Window.Handle, Native.NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>Another bar (e.g. the taskbar) changed size or position; re-reserve.</summary>
    public event Action? PositionChanged;

    /// <summary>A full-screen app opened (true) or closed (false) on some monitor.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>Displays, DPI, Explorer, session unlock or resume changed: everything should be re-applied.</summary>
    public event Action? EnvironmentChanged;

    /// <summary>The window used as the bar's identity, also this session's dispatcher.</summary>
    public MessageWindow Window { get; }

    public bool IsRegistered { get; private set; }

    public bool IsReserved { get; private set; }

    public Rect ReservedRect { get; private set; }

    /// <summary>Ask the shell where a bar on <paramref name="edge"/> would go, without reserving it.</summary>
    public Rect Query(DockEdge edge, Rect monitor, int thickness)
    {
        EnsureRegistered();
        var abd = NewData();
        abd.uEdge = (uint)edge;
        abd.rc = new RECT(DockGeometry.BarRect(monitor, edge, thickness));
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        var queried = abd.rc.ToRect();

        // While reserved, the shell can count our own current strip as an obstacle (seen when resizing): the
        // proposed bar then ends exactly where our strip begins. Keep our own outer side in that case.
        if (IsReserved && ReservedEdge == edge && DockGeometry.OuterSide(queried, edge) == DockGeometry.InnerSide(ReservedRect, edge))
        {
            queried = DockGeometry.WithOuterSide(queried, edge, DockGeometry.OuterSide(ReservedRect, edge));
        }

        var rect = DockGeometry.AdjustAfterQuery(queried, edge, thickness);
        return rect.IsEmpty ? DockGeometry.BarRect(monitor, edge, thickness) : rect;
    }

    public DockEdge? ReservedEdge { get; private set; }

    /// <summary>Reserve the strip. Returns the final rect granted by the shell.</summary>
    public Rect Reserve(DockEdge edge, Rect monitor, int thickness)
    {
        var proposed = Query(edge, monitor, thickness);
        var abd = NewData();
        abd.uEdge = (uint)edge;
        abd.rc = new RECT(proposed);
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
        var granted = abd.rc.ToRect();

        // The reply can describe the strip as if our own new reservation were in the way (it ends exactly where
        // the proposed strip begins) while the shell actually reserved what we proposed. Ignore that echo.
        if (granted.IsEmpty || DockGeometry.OuterSide(granted, edge) == DockGeometry.InnerSide(proposed, edge))
        {
            granted = proposed;
        }

        ReservedRect = granted;
        ReservedEdge = edge;
        IsReserved = true;
        return ReservedRect;
    }

    /// <summary>Give the strip back to the desktop. Idempotent.</summary>
    public void Release()
    {
        if (!IsRegistered)
        {
            return;
        }

        var abd = NewData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        IsRegistered = false;
        IsReserved = false;
        ReservedRect = default;
        ReservedEdge = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        Native.WTSUnRegisterSessionNotification(Window.Handle);
        Window.Message -= OnMessage;
        Window.Dispose();
    }

    private void EnsureRegistered()
    {
        if (IsRegistered)
        {
            return;
        }

        var abd = NewData();
        abd.uCallbackMessage = _callbackMessage;
        IsRegistered = Native.SHAppBarMessage(Native.ABM_NEW, ref abd) != 0;
        if (!IsRegistered)
        {
            throw new InvalidOperationException("The shell refused to register the app bar.");
        }
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = Window.Handle,
    };

    private bool OnMessage(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == _callbackMessage)
        {
            switch ((int)wParam)
            {
                case Native.ABN_POSCHANGED:
                    if (IsReserved)
                    {
                        PositionChanged?.Invoke();
                    }

                    break;
                case Native.ABN_FULLSCREENAPP:
                    FullScreenChanged?.Invoke(lParam != 0);
                    break;
            }

            return true;
        }

        if (msg == s_taskbarCreated)
        {
            // Explorer restarted and forgot every bar.
            IsRegistered = false;
            IsReserved = false;
            EnvironmentChanged?.Invoke();
            return false;
        }

        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                EnvironmentChanged?.Invoke();
                break;
            case Native.WM_POWERBROADCAST when (int)wParam is Native.PBT_APMRESUMEAUTOMATIC or Native.PBT_APMRESUMESUSPEND:
            case Native.WM_WTSSESSION_CHANGE when (int)wParam == Native.WTS_SESSION_UNLOCK:
                EnvironmentChanged?.Invoke();
                break;
        }

        return false;
    }
}
