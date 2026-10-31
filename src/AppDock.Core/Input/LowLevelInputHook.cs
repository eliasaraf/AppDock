using System.Runtime.InteropServices;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;
using AppDock.Core.Windows;

namespace AppDock.Core.Input;

public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool IsInjected);

public readonly record struct MouseEvent(uint Message, PointI Point);

/// <summary>
/// Low-level keyboard/mouse hooks on a dedicated thread with its own message loop, so input is never delayed by
/// work on the UI thread (Windows silently removes LL hooks that respond too slowly). Callbacks run on the hook
/// thread, must be quick, and return true to swallow the event.
/// </summary>
public sealed class LowLevelInputHook : IDisposable
{
    private readonly Func<KeyEvent, bool>? _onKey;
    private readonly Func<MouseEvent, bool>? _onMouse;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private Native.HookProc? _keyboardProc;
    private Native.HookProc? _mouseProc;
    private uint _threadId;
    private bool _disposed;

    public LowLevelInputHook(Func<KeyEvent, bool>? onKey, Func<MouseEvent, bool>? onMouse)
    {
        _onKey = onKey;
        _onMouse = onMouse;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "AppDock input hook" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsInstalled { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threadId != 0)
        {
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private void ThreadMain()
    {
        _threadId = Native.GetCurrentThreadId();
        var module = Native.GetModuleHandleW(null);
        nint keyboardHook = 0, mouseHook = 0;
        if (_onKey is not null)
        {
            _keyboardProc = KeyboardProc;
            keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        }

        if (_onMouse is not null)
        {
            _mouseProc = MouseProc;
            mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        }

        IsInstalled = (_onKey is null || keyboardHook != 0) && (_onMouse is null || mouseHook != 0);

        // Make sure the thread has a message queue before signalling, so WM_QUIT can be posted to it.
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        _started.Set();

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        if (keyboardHook != 0)
        {
            Native.UnhookWindowsHookEx(keyboardHook);
        }

        if (mouseHook != 0)
        {
            Native.UnhookWindowsHookEx(mouseHook);
        }
    }

    private nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code == Native.HC_ACTION && _onKey is not null)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var msg = (uint)wParam;
                var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                if (_onKey(new KeyEvent((int)data.vkCode, isDown, (data.flags & Native.LLKHF_INJECTED) != 0)))
                {
                    return 1;
                }
            }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                MessageWindow.ReportException(ex);
            }
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code == Native.HC_ACTION && _onMouse is not null)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (_onMouse(new MouseEvent((uint)wParam, new PointI(data.pt.X, data.pt.Y))))
                {
                    return 1;
                }
            }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                MessageWindow.ReportException(ex);
            }
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }
}
