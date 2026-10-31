using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AppDock.Core.Docking;
using AppDock.Core.Geometry;
using AppDock.Core.Interop;
using AppDock.Core.Settings;
using AppDock.Core.Windows;
using Serilog;

namespace AppDock.IntegrationTests;

/// <summary>
/// End-to-end tests against a real window (tools/AppDock.TestTarget) and the real shell: app bars are registered,
/// the desktop work area really shrinks, windows really move. They take over part of the screen for a few
/// seconds (and the drag test moves the mouse), so they only run when APPDOCK_INTEGRATION=1.
/// </summary>
public sealed class DockSessionIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static Dictionary<string, Rect>? s_baselineWorkAreas;

    private static bool Enabled => Environment.GetEnvironmentVariable("APPDOCK_INTEGRATION") == "1";

    [Fact]
    public void Dock_hide_show_minimize_move_and_dispose_manage_the_work_area()
    {
        if (!Enabled)
        {
            return;
        }

        RunOnUiThread(async () =>
        {
            using var target = await TestTarget.LaunchAsync();
            var monitor = Monitors.FromWindow(target.Hwnd);
            var freeWorkArea = monitor.WorkArea;
            using var harness = new Harness(target, DockEdge.Right);

            // Docks on start and reserves space.
            await WaitUntil(() => harness.Session.State.ReservesSpace, "docked on start");
            await WaitUntil(() => WorkArea(monitor).Right < freeWorkArea.Right - 100, "work area shrinks");
            await WaitUntil(() => VisibleRect(target).Right >= freeWorkArea.Right - 2 &&
                                  Math.Abs(VisibleRect(target).Left - WorkArea(monitor).Right) <= 2, "window fills the strip");
            Assert.True(WindowOps.IsTopmost(target.Hwnd));

            // Hide frees the space; show brings both back.
            harness.Session.ToggleVisibility();
            await WaitUntil(() => !WindowOps.IsVisible(target.Hwnd), "hidden");
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "space released when hidden");
            harness.Session.ToggleVisibility();
            await WaitUntil(() => WindowOps.IsVisible(target.Hwnd) && harness.Session.State.ReservesSpace, "shown");
            await WaitUntil(() => WorkArea(monitor).Right < freeWorkArea.Right - 100, "space reserved again");

            // Minimize frees the space; restore re-docks.
            Native.ShowWindow(target.Hwnd, 6 /* SW_MINIMIZE */);
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "space released when minimized");
            Native.ShowWindow(target.Hwnd, Native.SW_RESTORE);
            await WaitUntil(() => harness.Session.State.ReservesSpace && WorkArea(monitor).Right < freeWorkArea.Right - 100, "re-docked after restore");

            // Another edge.
            harness.Session.DockTo(DockEdge.Left);
            await WaitUntil(() => WorkArea(monitor).Left > freeWorkArea.Left + 100 && WorkArea(monitor).Right == freeWorkArea.Right, "docked left");
            await WaitUntil(() => Math.Abs(VisibleRect(target).Right - WorkArea(monitor).Left) <= 2, "window on the left strip");

            // Something else moves the docked window: AppDock puts it back.
            WindowOps.SetVisibleRect(target.Hwnd, Rect.FromXywh(600, 200, 500, 400));
            await WaitUntil(() => Math.Abs(VisibleRect(target).Right - WorkArea(monitor).Left) <= 2, "enforced back into place");

            // Undock.
            harness.Session.Undock();
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "space released on undock");
            Assert.Equal(DockMode.Floating, harness.Session.State.Mode);
            await WaitUntil(() => !WindowOps.IsTopmost(target.Hwnd), "topmost removed on undock");

            // Dock again, then dispose: space must be released and window left visible.
            harness.Session.DockTo(DockEdge.Right);
            await WaitUntil(() => WorkArea(monitor).Right < freeWorkArea.Right - 100, "docked again");
            harness.Dispose();
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "released on dispose");
            Assert.True(WindowOps.IsVisible(target.Hwnd));
        });
    }

    [Fact]
    public void Target_closing_releases_space_and_relaunch_redocks()
    {
        if (!Enabled)
        {
            return;
        }

        RunOnUiThread(async () =>
        {
            var title = TestTarget.UniqueTitle();
            var first = await TestTarget.LaunchAsync(title);
            var monitor = Monitors.FromWindow(first.Hwnd);
            var freeWorkArea = monitor.WorkArea;
            using var harness = new Harness(first, DockEdge.Bottom);
            await WaitUntil(() => WorkArea(monitor).Bottom < freeWorkArea.Bottom - 100, "docked bottom");

            first.Dispose(); // kills the process
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "released when target closes");
            await WaitUntil(() => !harness.Session.HasTarget, "target lost");

            using var second = await TestTarget.LaunchAsync(title);
            await WaitUntil(() => harness.Session.HasTarget && harness.Session.State.ReservesSpace, "re-acquired");
            await WaitUntil(() => WorkArea(monitor).Bottom < freeWorkArea.Bottom - 100, "re-docked bottom");
            Assert.Equal(second.Hwnd, harness.Session.TargetHandle);
        });
    }

    [Fact]
    public void Window_left_hidden_by_a_crashed_run_is_recovered()
    {
        if (!Enabled)
        {
            return;
        }

        RunOnUiThread(async () =>
        {
            using var target = await TestTarget.LaunchAsync();
            WindowOps.Hide(target.Hwnd);
            await WaitUntil(() => !WindowOps.IsVisible(target.Hwnd), "hidden by 'previous run'");

            var profile = target.Profile();
            profile.Runtime = new RuntimeState { HiddenByAppDock = true, LastHwnd = target.Hwnd, LastProcessId = (uint)target.Process.Id };
            using var harness = new Harness(target, DockEdge.Right, profile);
            await WaitUntil(() => WindowOps.IsVisible(target.Hwnd), "shown again");
            await WaitUntil(() => harness.Session.State.ReservesSpace, "docked");
            Assert.False(profile.Runtime.HiddenByAppDock);
        });
    }

    [Fact]
    public void Dragging_to_an_edge_docks_and_dragging_away_undocks()
    {
        if (!Enabled)
        {
            return;
        }

        RunOnUiThread(async () =>
        {
            using var target = await TestTarget.LaunchAsync();
            var monitor = Monitors.FromWindow(target.Hwnd);
            var freeWorkArea = monitor.WorkArea;
            var profile = target.Profile();
            profile.Edge = null; // start floating
            using var harness = new Harness(target, null, profile);
            await WaitUntil(() => harness.Session.HasTarget, "attached");
            Rect? lastPreview = null;
            harness.Session.SnapPreviewChanged += r => lastPreview ??= r;

            // Drag by the title bar to the left edge of the monitor.
            var r = VisibleRect(target);
            var grab = new PointI(r.Left + 200, r.Top + 12);
            await Mouse.DragAsync(grab, new PointI(monitor.Bounds.Left + 2, monitor.Bounds.Top + (monitor.Bounds.Height / 2)));
            await WaitUntil(() => harness.Session.State.ReservesSpace && harness.Session.State.Edge == DockEdge.Left, "docked by drag");
            await WaitUntil(() => WorkArea(monitor).Left > freeWorkArea.Left + 100, "space reserved on the left");
            Assert.NotNull(lastPreview);

            // Windows Snap must not have resized the window when it was dropped at the edge (AppDock owns the edges).
            var dragEnd = TestLog.Instance.Snapshot().Split(Environment.NewLine).First(l => l.Contains("Move/size ended", StringComparison.Ordinal));
            Assert.Contains($"{r.Width}x{r.Height}", dragEnd, StringComparison.Ordinal);

            // Drag it back to the middle (after a human-scale pause; nobody re-grabs a window within 0.3 s).
            await Task.Delay(500);
            r = VisibleRect(target);
            grab = new PointI(r.Left + (r.Width / 2), r.Top + 12);
            var under = WindowOps.GetRoot(WindowOps.WindowFromPoint(grab));
            Assert.True(under == target.Hwnd,
                $"Grab point {grab} on {r} is covered by '{WindowOps.GetClassName(under)}' (topmost {WindowOps.IsTopmost(under)}); " +
                $"target topmost {WindowOps.IsTopmost(target.Hwnd)}{Environment.NewLine}{TestLog.Instance.Snapshot()}");
            await Mouse.DragAsync(grab, monitor.WorkArea.Center);
            await WaitUntil(() => harness.Session.State.Mode == DockMode.Floating, "undocked by drag");
            await WaitUntil(() => WorkArea(monitor) == freeWorkArea, "space released");
        });
    }

    [Fact]
    public void Resizing_the_inner_edge_changes_the_reserved_thickness()
    {
        if (!Enabled)
        {
            return;
        }

        RunOnUiThread(async () =>
        {
            using var target = await TestTarget.LaunchAsync();
            var monitor = Monitors.FromWindow(target.Hwnd);
            var profile = target.Profile();
            using var harness = new Harness(target, DockEdge.Right, profile);
            await WaitUntil(() => harness.Session.State.ReservesSpace, "docked");
            await Task.Delay(400);
            var before = VisibleRect(target);

            // The resize border sits just outside the visible frame (invisible DWM border).
            var grab = new PointI(before.Left - 3, before.Top + (before.Height / 2));
            await Mouse.DragAsync(grab, new PointI(grab.X - 150, grab.Y));
            await WaitUntil(() => Math.Abs(VisibleRect(target).Width - (before.Width + 150)) <= 4, "window wider");
            await WaitUntil(() => Math.Abs(WorkArea(monitor).Right - VisibleRect(target).Left) <= 2,
                () => $"reservation follows the new width (before {before}, window {VisibleRect(target)}, work area {WorkArea(monitor)})");
            Assert.Equal(VisibleRect(target).Right, before.Right);
            Assert.InRange(profile.ThicknessDip[DockEdge.Right], monitor.ToDip(before.Width + 140), monitor.ToDip(before.Width + 160));
        });
    }

    // ---- helpers ----
    private static Rect WorkArea(MonitorInfo m) => Monitors.FromHandle(m.Handle)!.WorkArea;

    private static Rect VisibleRect(TestTarget t) => WindowOps.GetVisibleRect(t.Hwnd);

    private static Task WaitUntil(Func<bool> condition, string what) => WaitUntil(condition, () => what);

    private static async Task WaitUntil(Func<bool> condition, Func<string> what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Timeout)
            {
                throw new TimeoutException($"Timed out waiting for: {what()}");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Run an async test body on a dedicated thread with a Win32 message loop, like the app's UI thread.</summary>
    private static void RunOnUiThread(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var window = new MessageWindow("AppDock.Tests");
            Exception? callbackError = null;
            MessageWindow.CallbackException += OnError;
            SynchronizationContext.SetSynchronizationContext(new MessageWindowSynchronizationContext(window));
            try
            {
                // Every test must start from (and leave) an untouched desktop: no AppDock strip may survive a test.
                var current = Monitors.All().ToDictionary(m => m.DeviceName, m => m.WorkArea);
                s_baselineWorkAreas ??= current;
                if (!MessagePump.PumpUntil(
                        () => Monitors.All().All(m => s_baselineWorkAreas.TryGetValue(m.DeviceName, out var b) && b == m.WorkArea),
                        TimeSpan.FromSeconds(3)))
                {
                    failure = new InvalidOperationException(
                        $"A previous test left screen space reserved: {string.Join(", ", Monitors.All().Select(m => $"{m.DeviceName} {m.WorkArea}"))}");
                    return;
                }

                var task = body();
                if (!MessagePump.PumpUntil(() => task.IsCompleted, TimeSpan.FromMinutes(2)))
                {
                    failure = new TimeoutException("Test body did not complete.");
                }
                else if (task.IsFaulted)
                {
                    failure = task.Exception!.InnerException;
                }

                failure ??= callbackError;
            }
            finally
            {
                MessageWindow.CallbackException -= OnError;
            }

            void OnError(Exception ex) => callbackError ??= ex;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(
                failure.Message + Environment.NewLine + "Session log:" + Environment.NewLine + TestLog.Instance.Drain(), failure);
        }

        TestLog.Instance.Drain();
    }

    private sealed class TestLog : Serilog.Core.ILogEventSink
    {
        public static readonly TestLog Instance = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public void Emit(Serilog.Events.LogEvent logEvent) =>
            _lines.Enqueue($"{logEvent.Timestamp:HH:mm:ss.fff} {logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)}");

        public string Snapshot() => string.Join(Environment.NewLine, _lines);

        public string Drain()
        {
            var text = string.Join(Environment.NewLine, _lines);
            _lines.Clear();
            return text;
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "AppDockIT", Guid.NewGuid().ToString("N"));
        private bool _disposed;

        public Harness(TestTarget target, DockEdge? edge, TargetProfile? profile = null)
        {
            Directory.CreateDirectory(_dir);
            var store = new SettingsStore(Path.Combine(_dir, "settings.json"), new LoggerConfiguration().CreateLogger());
            profile ??= target.Profile();
            profile.Edge = edge;

            store.Settings.Profiles = [profile];
            store.Settings.ActiveProfileId = profile.Id;
            store.Settings.Animation.DurationMs = 120;
            Session = new DockSession(profile, store,
                new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(TestLog.Instance).CreateLogger());
            Session.Start();
        }

        public DockSession Session { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Session.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class TestTarget : IDisposable
    {
        private TestTarget(Process process, nint hwnd, string title)
        {
            Process = process;
            Hwnd = hwnd;
            Title = title;
        }

        public Process Process { get; }

        public nint Hwnd { get; }

        public string Title { get; }

        public static string UniqueTitle() => $"AppDock IT {Guid.NewGuid():N}";

        public static async Task<TestTarget> LaunchAsync(string? title = null)
        {
            title ??= UniqueTitle();
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "AppDock.TestTarget", "bin",
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                "net10.0-windows", "AppDock.TestTarget.exe"));
            var process = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--title", title } })
                ?? throw new InvalidOperationException("Could not start test target.");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < Timeout)
            {
                process.Refresh();
                if (process.MainWindowHandle != 0 && WindowOps.IsVisible(process.MainWindowHandle))
                {
                    return new TestTarget(process, process.MainWindowHandle, title);
                }

                await Task.Delay(50);
            }

            process.Kill();
            throw new TimeoutException("Test target window did not appear.");
        }

        public TargetProfile Profile() => new()
        {
            Name = "Integration target",
            ProcessName = "AppDock.TestTarget",
            TitlePattern = "^" + Regex.Escape(Title) + "$",
        };

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }

            Process.Dispose();
        }
    }

    private static class Mouse
    {
        private const uint InputMouse = 0, MoveAbsolute = 0x8001 | 0x4000, LeftDown = 0x0002, LeftUp = 0x0004;

        public static async Task DragAsync(PointI from, PointI to)
        {
            Move(from);
            await Task.Delay(80);
            Send(LeftDown);
            await Task.Delay(120);
            const int steps = 30;
            for (var i = 1; i <= steps; i++)
            {
                Move(new PointI(from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps)));
                await Task.Delay(15);
            }

            await Task.Delay(150);
            Send(LeftUp);
            await Task.Delay(100);
        }

        private static void Move(PointI p)
        {
            // Absolute coordinates are normalised to the virtual desktop (0..65535).
            var vx = GetSystemMetrics(76);
            var vy = GetSystemMetrics(77);
            var vw = GetSystemMetrics(78);
            var vh = GetSystemMetrics(79);
            var input = new INPUT
            {
                type = InputMouse,
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1)),
                    dy = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1)),
                    dwFlags = MoveAbsolute,
                },
            };
            _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        }

        private static void Send(uint flags)
        {
            var input = new INPUT { type = InputMouse, mi = new MOUSEINPUT { dwFlags = flags } };
            _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }
    }
}
