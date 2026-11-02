using System.Diagnostics;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;
using AppDock.Core.Windows;

namespace AppDock.Core.Animation;

/// <summary>
/// Tweens a window between two visible rects, one step per display frame (paced by DwmFlush), on a background
/// thread so the UI thread never stalls. Uses asynchronous SetWindowPos so a slow target cannot block us.
/// A new animation cancels the running one and starts from wherever the window currently is.
/// </summary>
public sealed class WindowAnimator
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task _current = Task.CompletedTask;
    private volatile bool _fastForward;

    public bool Enabled { get; set; } = true;

    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(200);

    public bool IsAnimating => !_current.IsCompleted;

    /// <summary>Tick count (ms) when the last frame was applied; used to ignore our own location events.</summary>
    public long LastFrameTick { get; private set; }

    /// <summary>Jump the running animation to its end (used when a new command arrives mid-animation).</summary>
    public void FastForward() => _fastForward = true;

    /// <param name="margins">The window's invisible frame borders; measured now when omitted. Pass them when the window
    /// is off-screen or hidden, where DWM reports unreliable frame bounds.</param>
    public async Task AnimateAsync(nint hwnd, Rect from, Rect to, Margins? margins = null)
    {
        Task previous;
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            previous = _current;
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        var m = margins ?? WindowOps.GetFrameMargins(hwnd);
        var animate = Enabled && Duration > TimeSpan.Zero && from != to && !from.IsEmpty &&
                      WindowOps.SystemAnimationsEnabled();
        if (!animate)
        {
            Apply(hwnd, to, m, async: false, keepSize: false);
            return;
        }

        _fastForward = false;
        var run = Task.Run(() => Run(hwnd, from, to, m, cts.Token), CancellationToken.None);
        lock (_lock)
        {
            _current = run;
        }

        await run.ConfigureAwait(true);
    }

    /// <summary>Set a rect immediately, cancelling any running animation.</summary>
    public void Set(nint hwnd, Rect to, Margins? margins = null)
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }

        Apply(hwnd, to, margins ?? WindowOps.GetFrameMargins(hwnd), async: false, keepSize: false);
    }

    private void Run(nint hwnd, Rect from, Rect to, Margins margins, CancellationToken token)
    {
        var keepSize = from.Size == to.Size;
        var clock = Stopwatch.StartNew();
        var duration = Duration.TotalMilliseconds;
        while (!token.IsCancellationRequested)
        {
            var t = _fastForward ? 1.0 : Math.Min(1.0, clock.Elapsed.TotalMilliseconds / duration);
            if (t >= 1.0)
            {
                // Final frame is synchronous so the window is exactly in place when the task completes.
                Apply(hwnd, to, margins, async: false, keepSize: false);
                return;
            }

            Apply(hwnd, DockGeometry.Lerp(from, to, Easing.OutCubic(t)), margins, async: true, keepSize);
            if (Native.DwmFlush() != 0)
            {
                Thread.Sleep(8);
            }
        }
    }

    private void Apply(nint hwnd, Rect r, Margins margins, bool async, bool keepSize)
    {
        WindowOps.SetVisibleRect(hwnd, r, margins, async, keepSize);
        LastFrameTick = Environment.TickCount64;
    }
}
