using AppDock.Core.Interop;

namespace AppDock.Core.Windows;

/// <summary>
/// Out-of-context WinEvent hooks scoped to one window of another process. Callbacks arrive on the thread that
/// created the tracker (it must pump messages).
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly Native.WinEventProc _proc;
    private readonly List<nint> _hooks = [];

    public WindowTracker(nint hwnd)
    {
        Hwnd = hwnd;
        _proc = OnEvent;
        var pid = WindowOps.GetProcessId(hwnd);
        Hook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, pid);
        Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, pid);
        Hook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_HIDE, pid);
        Hook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, pid);
    }

    public event Action? MoveSizeStarted;

    public event Action? MoveSizeEnded;

    public event Action? MinimizeStarted;

    public event Action? MinimizeEnded;

    public event Action? LocationChanged;

    public event Action? Shown;

    public event Action? Hidden;

    public event Action? Destroyed;

    public nint Hwnd { get; }

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            Native.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void Hook(uint min, uint max, uint pid)
    {
        var hook = Native.SetWinEventHook(min, max, 0, _proc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void OnEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != Hwnd || idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF)
        {
            return;
        }

        try
        {
            switch (evt)
            {
                case Native.EVENT_SYSTEM_MOVESIZESTART: MoveSizeStarted?.Invoke(); break;
                case Native.EVENT_SYSTEM_MOVESIZEEND: MoveSizeEnded?.Invoke(); break;
                case Native.EVENT_SYSTEM_MINIMIZESTART: MinimizeStarted?.Invoke(); break;
                case Native.EVENT_SYSTEM_MINIMIZEEND: MinimizeEnded?.Invoke(); break;
                case Native.EVENT_OBJECT_LOCATIONCHANGE: LocationChanged?.Invoke(); break;
                case Native.EVENT_OBJECT_SHOW: Shown?.Invoke(); break;
                case Native.EVENT_OBJECT_HIDE: Hidden?.Invoke(); break;
                case Native.EVENT_OBJECT_DESTROY: Destroyed?.Invoke(); break;
            }
        }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MessageWindow.ReportException(ex);
        }
    }
}

/// <summary>
/// System-wide watcher for top-level windows appearing or being renamed, used to re-acquire a target right
/// after it starts instead of waiting for the next poll.
/// </summary>
public sealed class WindowAppearanceWatcher : IDisposable
{
    private readonly Native.WinEventProc _proc;
    private readonly List<nint> _hooks = [];

    public WindowAppearanceWatcher()
    {
        _proc = OnEvent;
        Hook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW);
        Hook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE);
    }

    public event Action? TopLevelWindowAppeared;

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            Native.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void Hook(uint min, uint max)
    {
        var hook = Native.SetWinEventHook(min, max, 0, _proc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void OnEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == 0 ||
            WindowOps.GetRoot(hwnd) != hwnd)
        {
            return;
        }

        try
        {
            TopLevelWindowAppeared?.Invoke();
        }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MessageWindow.ReportException(ex);
        }
    }
}
