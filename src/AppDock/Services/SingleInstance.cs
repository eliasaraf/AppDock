using System;
using System.Threading;

namespace AppDock.Services;

/// <summary>
/// One AppDock per user session. A second launch signals the running instance (to open Settings, or to exit
/// when the uninstaller runs <c>AppDock.exe --shutdown</c>) and quits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Prefix = @"Local\AppDock.7b1e2c94";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _shutdown;
    private readonly RegisteredWaitHandle? _activateWait;
    private readonly RegisteredWaitHandle? _shutdownWait;

    private SingleInstance(Mutex mutex, bool isFirst, Action? onActivate, Action? onShutdown)
    {
        _mutex = mutex;
        IsFirst = isFirst;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + ".Activate");
        _shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + ".Shutdown");
        if (isFirst)
        {
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate?.Invoke(), null, -1, false);
            _shutdownWait = ThreadPool.RegisterWaitForSingleObject(_shutdown, (_, _) => onShutdown?.Invoke(), null, -1, false);
        }
    }

    public bool IsFirst { get; }

    /// <summary>Callbacks run on a thread-pool thread; marshal to the UI thread.</summary>
    public static SingleInstance Acquire(Action onActivate, Action onShutdown)
    {
        var mutex = new Mutex(initiallyOwned: true, Prefix + ".Mutex", out var createdNew);
        return new SingleInstance(mutex, createdNew, onActivate, onShutdown);
    }

    public void SignalActivate() => _activate.Set();

    public void SignalShutdown() => _shutdown.Set();

    /// <summary>
    /// Wait until the running instance has exited (it releases the mutex on exit), so an installer can safely
    /// replace files right after <c>--shutdown</c> returns.
    /// </summary>
    public bool WaitForPrimaryExit(TimeSpan timeout)
    {
        try
        {
            if (!_mutex.WaitOne(timeout))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The other instance died without releasing: it is gone either way.
        }

        _mutex.ReleaseMutex();
        return true;
    }

    public void Dispose()
    {
        _activateWait?.Unregister(null);
        _shutdownWait?.Unregister(null);
        _activate.Dispose();
        _shutdown.Dispose();
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from another thread during a crash; the OS frees it on exit anyway.
            }
        }

        _mutex.Dispose();
    }
}
