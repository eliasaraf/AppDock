using AppDock.Core.Geometry;
using AppDock.Core.Interop;
using AppDock.Core.Windows;

namespace AppDock.Core.Input;

/// <summary>
/// "Click the window you want to dock": while active, reports the top-level window under the cursor, swallows the
/// next left click and returns that window. Esc or right click cancels.
/// </summary>
public sealed class WindowPicker : IDisposable
{
    private readonly MessageWindow _ui;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private LowLevelInputHook? _hook;
    private nint _lastHover;
    private TaskCompletionSource<nint?>? _tcs;

    public WindowPicker(MessageWindow ui) => _ui = ui;

    /// <summary>Hovered window changed (0 when none). Raised on the UI thread.</summary>
    public event Action<nint>? HoverChanged;

    public bool IsActive => _hook is not null;

    public Task<nint?> PickAsync()
    {
        Cancel();
        _tcs = new TaskCompletionSource<nint?>();
        _lastHover = 0;
        _hook = new LowLevelInputHook(OnKey, OnMouse);
        return _tcs.Task;
    }

    public void Cancel() => Finish(null);

    public void Dispose() => Cancel();

    private void Finish(nint? result)
    {
        var hook = _hook;
        _hook = null;
        hook?.Dispose();
        _tcs?.TrySetResult(result);
        _tcs = null;
        HoverChanged?.Invoke(0);
    }

    private nint RootAt(PointI p)
    {
        var hwnd = WindowOps.WindowFromPoint(p);
        if (hwnd == 0)
        {
            return 0;
        }

        var root = WindowOps.GetRoot(hwnd);
        return root == 0 || WindowOps.GetProcessId(root) == _ownProcessId ? 0 : root;
    }

    // Hook thread.
    private bool OnMouse(MouseEvent e)
    {
        switch (e.Message)
        {
            case Native.WM_MOUSEMOVE:
                var hover = RootAt(e.Point);
                if (hover != _lastHover)
                {
                    _lastHover = hover;
                    _ui.Post(() => HoverChanged?.Invoke(hover));
                }

                return false;

            case Native.WM_LBUTTONDOWN:
                var picked = RootAt(e.Point);
                _ui.Post(() => Finish(picked == 0 ? null : picked));
                return true;

            case Native.WM_LBUTTONUP:
                return true; // swallow the matching release too

            case Native.WM_RBUTTONDOWN:
                _ui.Post(Cancel);
                return true;

            case Native.WM_RBUTTONUP:
                return true;
        }

        return false;
    }

    private bool OnKey(KeyEvent e)
    {
        if (e.VirtualKey == Native.VK_ESCAPE && e.IsDown)
        {
            _ui.Post(Cancel);
            return true;
        }

        return false;
    }
}
