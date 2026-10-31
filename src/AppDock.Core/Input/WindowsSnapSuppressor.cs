using AppDock.Core.Interop;
using AppDock.Core.Windows;

namespace AppDock.Core.Input;

/// <summary>
/// Keeps Windows Snap from fighting AppDock while the user drags the target window. Windows reads the Snap
/// setting when a drag begins, so it is switched off at mouse-down on the target (in a low-level hook, before the
/// target even receives the click) and back on shortly after the button is released. The change is not persisted
/// to the user profile (fWinIni = 0) and is restored on dispose, so the user's setting is never lost.
/// </summary>
public sealed class WindowsSnapSuppressor : IDisposable
{
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(250);
    private readonly MessageWindow _ui;
    private readonly LowLevelInputHook _hook;
    private IDisposable? _restoreTimer;
    private volatile int _suppressed;
    private nint _target;
    private bool _disposed;

    public WindowsSnapSuppressor(MessageWindow ui, nint target)
    {
        _ui = ui;
        _target = target;
        _hook = new LowLevelInputHook(null, OnMouse);
    }

    public void RestoreNow()
    {
        _restoreTimer?.Dispose();
        _restoreTimer = null;
        if (Interlocked.Exchange(ref _suppressed, 0) == 1)
        {
            WindowOps.SetWindowsSnapEnabled(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _target = 0;
        _hook.Dispose();
        RestoreNow();
    }

    // Hook thread.
    private bool OnMouse(MouseEvent e)
    {
        var target = _target;
        if (target == 0)
        {
            return false;
        }

        if (e.Message == Native.WM_LBUTTONDOWN && _suppressed == 0 &&
            WindowOps.GetRoot(WindowOps.WindowFromPoint(e.Point)) == target && WindowOps.IsWindowsSnapEnabled())
        {
            WindowOps.SetWindowsSnapEnabled(false);
            _suppressed = 1;
        }
        else if (e.Message == Native.WM_LBUTTONUP && _suppressed == 1)
        {
            _ui.Post(() =>
            {
                _restoreTimer?.Dispose();
                _restoreTimer = _ui.Delay(RestoreDelay, RestoreNow);
            });
        }

        return false;
    }
}
