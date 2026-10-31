using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AppDock.Core.Interop;

namespace AppDock.Core.Windows;

/// <summary>Handler for raw window messages. Return true and set <paramref name="result"/> to consume.</summary>
public delegate bool MessageHandler(uint msg, nint wParam, nint lParam, out nint result);

/// <summary>
/// A hidden, top-level Win32 window owned by the calling thread. It is the per-thread "dispatcher" for Core:
/// receives shell/app-bar callbacks and broadcasts (TaskbarCreated, display changes), runs timers and marshals
/// work from background threads. Top-level (not HWND_MESSAGE) so it receives broadcast messages.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "AppDock.MessageWindow";
    private const uint WmInvoke = Native.WM_APP + 1;

    private static readonly Native.WndProc s_wndProc = StaticWndProc;
    private static readonly ConcurrentDictionary<nint, MessageWindow> s_windows = new();
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<nuint, (Action Callback, bool Repeat)> _timers = [];
    private readonly uint _threadId;
    private nuint _nextTimerId = 1;
    private bool _disposed;

    public MessageWindow(string title)
    {
        EnsureClass();
        _threadId = Native.GetCurrentThreadId();
        Handle = Native.CreateWindowExW(
            (uint)Native.WS_EX_TOOLWINDOW, ClassName, title, Native.WS_POPUP,
            0, 0, 0, 0, 0, 0, Native.GetModuleHandleW(null), 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        s_windows[Handle] = this;
    }

    /// <summary>Raised for every exception thrown by a callback running on this window's thread.</summary>
    public static event Action<Exception>? CallbackException;

    public event MessageHandler? Message;

    public nint Handle { get; }

    public bool CheckAccess() => Native.GetCurrentThreadId() == _threadId;

    /// <summary>Queue <paramref name="action"/> to run on the window's thread. Safe from any thread.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(action);
        Native.PostMessageW(Handle, WmInvoke, 0, 0);
    }

    /// <summary>Run <paramref name="callback"/> after <paramref name="due"/>. Dispose the result to cancel.</summary>
    public IDisposable Delay(TimeSpan due, Action callback) => StartTimer(due, callback, repeat: false);

    /// <summary>Run <paramref name="callback"/> every <paramref name="interval"/> until disposed.</summary>
    public IDisposable Every(TimeSpan interval, Action callback) => StartTimer(interval, callback, repeat: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _timers.Keys.ToArray())
        {
            Native.KillTimer(Handle, id);
        }

        _timers.Clear();
        s_windows.TryRemove(Handle, out _);
        Native.DestroyWindow(Handle);
    }

    internal static void ReportException(Exception ex) => CallbackException?.Invoke(ex);

    private TimerHandle StartTimer(TimeSpan interval, Action callback, bool repeat)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var id = _nextTimerId++;
        _timers[id] = (callback, repeat);
        var ms = (uint)Math.Clamp(interval.TotalMilliseconds, 10, int.MaxValue);
        Native.SetTimer(Handle, id, ms, 0);
        return new TimerHandle(this, id);
    }

    private void StopTimer(nuint id)
    {
        if (_timers.Remove(id))
        {
            Native.KillTimer(Handle, id);
        }
    }

    private static void EnsureClass()
    {
        lock (s_classLock)
        {
            if (s_classRegistered)
            {
                return;
            }

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassExW(ref wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }

            s_classRegistered = true;
        }
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!s_windows.TryGetValue(hwnd, out var window))
        {
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        try
        {
            switch (msg)
            {
                case WmInvoke:
                    while (window._queue.TryDequeue(out var action))
                    {
                        Run(action);
                    }

                    return 0;

                case Native.WM_TIMER when window._timers.TryGetValue((nuint)wParam, out var timer):
                    if (!timer.Repeat)
                    {
                        window.StopTimer((nuint)wParam);
                    }

                    Run(timer.Callback);
                    return 0;
            }

            var handlers = window.Message;
            if (handlers is not null)
            {
                foreach (var handler in handlers.GetInvocationList().Cast<MessageHandler>())
                {
                    if (handler(msg, wParam, lParam, out var result))
                    {
                        return result;
                    }
                }
            }
        }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportException(ex);
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Exceptions must never cross back into native code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportException(ex);
        }
    }

    private sealed class TimerHandle(MessageWindow owner, nuint id) : IDisposable
    {
        public void Dispose()
        {
            if (owner.CheckAccess())
            {
                owner.StopTimer(id);
            }
            else
            {
                owner.Post(() => owner.StopTimer(id));
            }
        }
    }
}

/// <summary>Synchronization context backed by a <see cref="MessageWindow"/> (used where no UI framework exists).</summary>
public sealed class MessageWindowSynchronizationContext(MessageWindow window) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => window.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (window.CheckAccess())
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        window.Post(() =>
        {
            try
            {
                d(state);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>Minimal Win32 message pump, for hosts without a UI framework (tests, tools).</summary>
public static class MessagePump
{
    /// <summary>Pump messages until <paramref name="until"/> returns true or the timeout elapses.</summary>
    public static bool PumpUntil(Func<bool> until, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(until);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            while (Native.PeekMessageW(out var msg, 0, 0, 0, 1))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }

            if (until())
            {
                return true;
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(5);
        }
    }

    public static void PumpFor(TimeSpan duration) => PumpUntil(() => false, duration);
}
