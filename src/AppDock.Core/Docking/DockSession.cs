using AppDock.Core.Animation;
using AppDock.Core.Geometry;
using AppDock.Core.Input;
using AppDock.Core.Settings;
using AppDock.Core.Targeting;
using AppDock.Core.Windows;
using Serilog;
using static AppDock.Core.Docking.DockEffect;
using static AppDock.Core.Docking.DockEvent;

namespace AppDock.Core.Docking;

public enum NotificationKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Docks one target application. Owns its AppBar, follows the target window through WinEvents, turns user
/// gestures into <see cref="DockEvent"/>s, runs them through <see cref="DockReducer"/> one at a time and executes
/// the resulting effects. Must be created and used on a thread with a message loop and a SynchronizationContext
/// (the WPF UI thread in the app).
/// </summary>
public sealed class DockSession : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EnforceDelay = TimeSpan.FromMilliseconds(160);
    private const int OwnMoveGraceMs = 250;
    private const int MaxEnforceAttempts = 3;

    // After a reservation changes the work area, Windows pushes visible windows that overlap the new strip back
    // into the work area, with its own (DWM-animated) move. Undoing that push at once, before DWM renders a frame,
    // keeps it invisible; undoing it later (Enforce) shows it as a slide followed by a jump.
    private const int ShellNudgeGuardMs = 1500;
    private const int MaxNudgeCorrections = 4;

    private readonly TargetProfile _profile;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ILogger _log;
    private readonly AppBarController _bar;
    private readonly WindowAnimator _animator = new();
    private readonly Queue<DockEvent> _pending = new();

    private DockState _state;
    private WindowTracker? _tracker;
    private WindowAppearanceWatcher? _watcher;
    private IDisposable? _pollTimer;
    private IDisposable? _acquireDebounce;
    private IDisposable? _enforceTimer;
    private IDisposable? _envDebounce;
    private IDisposable? _posDebounce;
    private IDisposable? _verifyTimer;
    private nint _hwnd;
    private bool _processing;
    private bool _disposed;
    private bool _originalTopmost;
    private bool _yieldedToFullScreen;
    private WindowsSnapSuppressor? _snapSuppressor;

    // Drag tracking.
    private bool _inMoveSize;
    private MoveSizeKind _gesture;
    private Rect _moveSizeStartRect;
    private DockEdge? _previewEdge;
    private string? _previewMonitor;

    // Enforcement (keep the docked window where it belongs).
    private Rect _expectedRect;
    private int _enforceAttempts;
    private long _enforceWindowStart;
    private long _ownMoveTick;

    // Shell nudge guard (see ShellNudgeGuardMs).
    private Rect _settledWindowRect;
    private long _nudgeGuardUntil;
    private int _nudgeCorrections;

    // Frame borders measured while the window was last on screen (DWM reports nonsense for off-screen windows).
    private Margins? _parkMargins;

    public DockSession(TargetProfile profile, SettingsStore store, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(store);
        _profile = profile;
        _store = store;
        _settings = store.Settings;
        _log = log.ForContext<DockSession>().ForContext("Target", profile.Name);
        _bar = new AppBarController(profile.Id);
        _bar.PositionChanged += OnBarPositionChanged;
        _bar.EnvironmentChanged += () => DebounceEnvironment(TimeSpan.FromMilliseconds(400));
        _bar.FullScreenChanged += OnFullScreenChanged;
        _state = DockState.Initial(profile.RestoreDockOnLaunch ? profile.Edge : null, profile.Monitor);
        ApplyAnimationSettings();
    }

    /// <summary>State or target changed (for tray text, settings UI).</summary>
    public event Action? Changed;

    /// <summary>Snap preview rect while dragging near an edge; null hides it.</summary>
    public event Action<Rect?>? SnapPreviewChanged;

    public event Action<string, NotificationKind>? Notification;

    public TargetProfile Profile => _profile;

    public DockState State => _state;

    public bool HasTarget => _hwnd != 0;

    public nint TargetHandle => _hwnd;

    public string Describe()
    {
        if (!_state.TargetPresent)
        {
            return $"Waiting for {_profile.Name}";
        }

        var where = _state.IsDocked ? $"docked {_state.Edge.ToString().ToLowerInvariant()}" : "floating";
        return _state.Visibility switch
        {
            WindowVisibility.Hidden => $"{_profile.Name} - hidden ({where})",
            WindowVisibility.Minimized => $"{_profile.Name} - minimized",
            _ => $"{_profile.Name} - {where}",
        };
    }

    public void Start()
    {
        _log.Information("Session started for {Profile}", _profile.Name);
        TryRecoverRememberedWindow();
        if (_hwnd == 0)
        {
            TryAcquire();
        }

        if (_hwnd == 0)
        {
            StartWaitingForTarget();
        }
    }

    // ---- Public commands (UI thread) ----
    public void DockTo(DockEdge edge, string? monitor = null) =>
        Enqueue(new DockRequested(edge, monitor ?? (_hwnd != 0 ? Monitors.FromWindow(_hwnd).DeviceName : null)));

    public void Undock() => Enqueue(new UndockRequested());

    public void ToggleVisibility() => Enqueue(new ToggleVisibility());

    public void Show() => Enqueue(new ShowRequested());

    public void Hide() => Enqueue(new HideRequested());

    /// <summary>Re-apply after settings such as thickness, monitor or always-on-top changed.</summary>
    public void ApplySettings()
    {
        ApplyAnimationSettings();
        if (_hwnd != 0 && _settings.SuspendWindowsSnapWhileDragging != (_snapSuppressor is not null))
        {
            _snapSuppressor?.Dispose();
            _snapSuppressor = _settings.SuspendWindowsSnapWhileDragging ? new WindowsSnapSuppressor(_bar.Window, _hwnd) : null;
        }

        if (_hwnd != 0 && _state.ReservesSpace)
        {
            WindowOps.SetTopmost(_hwnd, _profile.AlwaysOnTop || _originalTopmost);
        }

        Enqueue(new Reapply());
    }

    public void SetThickness(DockEdge edge, int dip)
    {
        _profile.ThicknessDip[edge] = Math.Max(DockGeometry.MinThicknessDip, dip);
        _store.MarkDirty();
        if (_state.ReservesSpace && _state.Edge == edge)
        {
            Enqueue(new Reapply());
        }
    }

    public void SetMonitor(string? deviceName)
    {
        _profile.Monitor = deviceName;
        _state = _state with { Monitor = deviceName };
        _store.MarkDirty();
        Enqueue(new Reapply());
    }

    /// <summary>Restore everything synchronously; safe to call from crash handlers on the UI thread.</summary>
    public void EmergencyRestore()
    {
        try
        {
            RestoreWindowsSnap();
            _bar.Release();
            if (_hwnd != 0 && WindowOps.Exists(_hwnd))
            {
                if (!WindowOps.IsVisible(_hwnd))
                {
                    WindowOps.Show(_hwnd, activate: false);
                }

                if (!_originalTopmost)
                {
                    WindowOps.SetTopmost(_hwnd, false);
                }
            }

            _profile.Runtime.HiddenByAppDock = false;
        }
#pragma warning disable CA1031 // Best effort during shutdown/crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Emergency restore failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SnapPreviewChanged?.Invoke(null);
        EmergencyRestore();
        _store.MarkDirty();
        DetachTarget();
        StopWaitingForTarget();
        _enforceTimer?.Dispose();
        _envDebounce?.Dispose();
        _posDebounce?.Dispose();
        _verifyTimer?.Dispose();
        _bar.Dispose();
        _log.Information("Session stopped");
    }

    // ---- Event pipeline ----
    private void Enqueue(DockEvent e)
    {
        if (_disposed)
        {
            return;
        }

        _pending.Enqueue(e);
        _animator.FastForward();
        if (!_processing)
        {
            _ = ProcessAsync();
        }
    }

    private async Task ProcessAsync()
    {
        _processing = true;
        try
        {
            while (!_disposed && _pending.TryDequeue(out var e))
            {
                var previous = _state;
                var (next, effects) = DockReducer.Reduce(previous, e);
                _state = next;
                _log.Debug("{Event}: {Prev} -> {Next}; effects {Effects}", e.GetType().Name, Summarize(previous),
                    Summarize(next), string.Join(",", effects.Select(f => f.GetType().Name)));
                PersistState(e);

                foreach (var effect in effects)
                {
                    if (_disposed || (_hwnd == 0 && effect is not Release))
                    {
                        break;
                    }

                    try
                    {
                        await ExecuteAsync(effect, e).ConfigureAwait(true);
                    }
#pragma warning disable CA1031 // One failed effect must not kill the session.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        _log.Error(ex, "Effect {Effect} failed", effect.GetType().Name);
                    }
                }

                Changed?.Invoke();
            }
        }
        finally
        {
            _processing = false;
        }
    }

    private void PersistState(DockEvent e)
    {
        var edge = _state.IsDocked ? _state.Edge : (DockEdge?)null;
        var changed = _profile.Edge != edge || _profile.Monitor != _state.Monitor;

        // Remember the layout the user chose, but do not forget it just because the target closed.
        if (changed && e is not TargetLost)
        {
            _profile.Edge = edge;
            _profile.Monitor = _state.Monitor;
        }

        var hidden = _state.TargetPresent && _state.Visibility == WindowVisibility.Hidden;
        if (changed || _profile.Runtime.HiddenByAppDock != hidden)
        {
            _profile.Runtime.HiddenByAppDock = hidden;
            _store.MarkDirty();
        }
    }

    private async Task ExecuteAsync(DockEffect effect, DockEvent cause)
    {
        switch (effect)
        {
            case MoveToDock m:
                await MoveToDockAsync(m.Animate).ConfigureAwait(true);
                break;

            case Reserve:
                ReserveSpace();
                break;

            case Release:
                ReleaseSpace();
                break;

            case MoveToFloat f:
                await MoveToFloatAsync(f.DropCursor, f.Animate).ConfigureAwait(true);
                break;

            case SlideOut:
                await SlideOutAsync().ConfigureAwait(true);
                break;

            case SlideIn:
                await SlideInAsync().ConfigureAwait(true);
                break;

            case DockEffect.Hide:
                MarkOwnMove();
                WindowOps.Hide(_hwnd);
                break;

            case DockEffect.Show:
                MarkOwnMove();
                WindowOps.Show(_hwnd, activate: cause is DockEvent.ToggleVisibility or ShowRequested);
                break;

            case Focus:
                WindowOps.ForceForeground(_hwnd);
                break;

            case Restore:
                WindowOps.Restore(_hwnd);
                break;

            case DockEffect.SaveFloatingBounds:
                SaveFloatingBounds();
                break;
        }
    }

    // ---- Effect implementations ----
    private MonitorInfo ResolveMonitor()
    {
        var byName = Monitors.ByDeviceName(_state.Monitor);
        if (byName is not null)
        {
            return byName;
        }

        if (_state.Monitor is not null)
        {
            _log.Information("Monitor {Monitor} is gone; using the primary monitor", _state.Monitor);
            return Monitors.Primary();
        }

        return _hwnd != 0 ? Monitors.FromWindow(_hwnd) : Monitors.Primary();
    }

    private int ThicknessPixels(MonitorInfo monitor, DockEdge edge) =>
        DockGeometry.ClampThickness(monitor.ToPixels(_profile.GetThicknessDip(edge)), monitor.Bounds, edge, monitor.Scale);

    private Rect DockRect(out MonitorInfo monitor)
    {
        monitor = ResolveMonitor();
        return _bar.Query(_state.Edge, monitor.Bounds, ThicknessPixels(monitor, _state.Edge));
    }

    private void PrepareForPositioning()
    {
        if (WindowOps.IsMaximized(_hwnd) || WindowOps.IsMinimized(_hwnd))
        {
            MarkOwnMove();
            WindowOps.Restore(_hwnd);
        }
    }

    private async Task MoveToDockAsync(bool animate)
    {
        var target = DockRect(out _);
        _log.Debug("Dock rect from shell: {Rect} (window at {Window})", target, WindowOps.GetVisibleRect(_hwnd));
        PrepareForPositioning();
        if (_profile.AlwaysOnTop && !_yieldedToFullScreen)
        {
            WindowOps.SetTopmost(_hwnd, true);
        }

        _expectedRect = target;
        await MoveAsync(target, animate).ConfigureAwait(true);
    }

    private void ReserveSpace()
    {
        var monitor = ResolveMonitor();

        // Armed before reserving: the shell can push the window (and we can hear of it) before ABM_SETPOS returns.
        GuardAgainstShellNudge();
        var granted = _bar.Reserve(_state.Edge, monitor.Bounds, ThicknessPixels(monitor, _state.Edge));
        _log.Debug("Reserved {Granted} (expected {Expected})", granted, _expectedRect);
        if (!granted.NearlyEquals(_expectedRect))
        {
            _expectedRect = granted;
            MarkOwnMove();
            _animator.Set(_hwnd, granted);
            GuardAgainstShellNudge();
        }

        _enforceAttempts = 0;

        // Windows keeps ordinary windows inside the work area, so right after the strip is reserved it may nudge
        // our window out of it. That nudge arrives while our own moves are still being ignored; verify explicitly.
        _verifyTimer?.Dispose();
        _verifyTimer = _bar.Window.Delay(TimeSpan.FromMilliseconds(OwnMoveGraceMs + 100), () =>
        {
            Enforce();
            _verifyTimer = _bar.Window.Delay(TimeSpan.FromMilliseconds(500), Enforce);
        });
    }

    /// <summary>Remember where the window is now and, for a short while, put it straight back if the shell moves it.</summary>
    private void GuardAgainstShellNudge()
    {
        _settledWindowRect = WindowOps.GetWindowRect(_hwnd);
        _nudgeCorrections = 0;
        _nudgeGuardUntil = _settledWindowRect.IsEmpty ? 0 : Environment.TickCount64 + ShellNudgeGuardMs;
    }

    private bool TryUndoShellNudge()
    {
        if (Environment.TickCount64 > _nudgeGuardUntil || !_state.ReservesSpace || _animator.IsAnimating ||
            _nudgeCorrections >= MaxNudgeCorrections || WindowOps.IsMinimized(_hwnd) || WindowOps.IsMaximized(_hwnd) ||
            !WindowOps.IsVisible(_hwnd))
        {
            return false;
        }

        var actual = WindowOps.GetWindowRect(_hwnd);
        if (actual == _settledWindowRect || actual.IsEmpty)
        {
            return false;
        }

        _nudgeCorrections++;
        _log.Debug("Undoing shell nudge: window at {Actual}, back to {Settled}", actual, _settledWindowRect);
        MarkOwnMove();
        WindowOps.SetVisibleRect(_hwnd, _settledWindowRect, Margins.Zero);
        return true;
    }

    private void ReleaseSpace()
    {
        _nudgeGuardUntil = 0;
        _bar.Release();
        _expectedRect = default;
        if (_hwnd != 0 && !_originalTopmost && WindowOps.Exists(_hwnd))
        {
            WindowOps.SetTopmost(_hwnd, false);
        }
    }

    private async Task MoveToFloatAsync(PointI? dropCursor, bool animate)
    {
        PrepareForPositioning();
        var current = WindowOps.GetVisibleRect(_hwnd);
        var monitor = dropCursor is { } c ? Monitors.FromPoint(c) : Monitors.FromRect(current);
        var saved = _profile.FloatingBounds?.ToRect();
        var size = saved is { IsEmpty: false } s
            ? s.Size
            : DockGeometry.DefaultFloatingRect(monitor.WorkArea, monitor.Scale).Size;

        Rect target;
        if (dropCursor is { } cursor)
        {
            target = DockGeometry.UndockRect(current, cursor, size, monitor.WorkArea);
        }
        else if (saved is { IsEmpty: false } savedRect && Monitors.All().Any(m => m.WorkArea.IntersectsWith(savedRect)))
        {
            target = DockGeometry.ClampInto(savedRect, Monitors.FromRect(savedRect).WorkArea);
        }
        else
        {
            target = DockGeometry.DefaultFloatingRect(monitor.WorkArea, monitor.Scale);
        }

        _log.Debug("Floating: from {Current} to {Target} (drop {Drop}, work area {Work})", current, target, dropCursor, monitor.WorkArea);
        await MoveAsync(target, animate).ConfigureAwait(true);
    }

    private async Task SlideOutAsync()
    {
        var current = WindowOps.GetVisibleRect(_hwnd);
        var margins = WindowOps.GetFrameMargins(_hwnd);
        _parkMargins = margins;
        var monitor = ResolveMonitor();
        var offscreen = DockGeometry.Offscreen(current, _state.Edge, monitor.Bounds);
        if (DockGeometry.CanSlide(offscreen, Monitors.All().Select(m => m.Bounds)) && !WindowOps.IsMinimized(_hwnd))
        {
            MarkOwnMove();
            await _animator.AnimateAsync(_hwnd, current, offscreen, margins).ConfigureAwait(true);
            MarkOwnMove();
            WindowOps.Hide(_hwnd);

            // Park it back in place while hidden, so anything that shows it (its own tray icon, a crash of
            // AppDock) brings it back on screen rather than off-screen.
            WindowOps.SetVisibleRect(_hwnd, current, margins);
        }
        else
        {
            // Neighbouring monitor in the way: let DWM play its native hide transition instead.
            MarkOwnMove();
            WindowOps.Hide(_hwnd);
        }
    }

    private async Task SlideInAsync()
    {
        PrepareForPositioning();
        var target = DockRect(out var monitor);
        _expectedRect = target;
        var offscreen = DockGeometry.Offscreen(target, _state.Edge, monitor.Bounds);
        if (_profile.AlwaysOnTop && !_yieldedToFullScreen)
        {
            WindowOps.SetTopmost(_hwnd, true);
        }

        var visible = WindowOps.IsVisible(_hwnd);
        if (!visible && DockGeometry.CanSlide(offscreen, Monitors.All().Select(m => m.Bounds)))
        {
            // Show it parked just past the edge and reserve the strip while it is there. Windows pushes visible
            // windows that overlap a newly reserved strip into the work area (hidden ones when they are shown),
            // but leaves a window that is fully off-screen alone. Reserving after sliding in instead would make
            // Windows visibly drag it out of the strip again.
            var margins = _parkMargins ?? WindowOps.GetFrameMargins(_hwnd);
            MarkOwnMove();
            _animator.Set(_hwnd, offscreen, margins);
            WindowOps.Show(_hwnd, activate: false);
            target = _bar.Reserve(_state.Edge, monitor.Bounds, ThicknessPixels(monitor, _state.Edge));
            _expectedRect = target;
            MarkOwnMove();
            if (_animator.Enabled && WindowOps.SystemAnimationsEnabled())
            {
                await _animator.AnimateAsync(_hwnd, offscreen, target, margins).ConfigureAwait(true);
            }
            else
            {
                _animator.Set(_hwnd, target, margins);
            }

            MarkOwnMove();
        }
        else
        {
            MarkOwnMove();
            _animator.Set(_hwnd, target);
            WindowOps.Show(_hwnd, activate: false);
        }
    }

    private async Task MoveAsync(Rect target, bool animate)
    {
        MarkOwnMove();
        if (animate)
        {
            await _animator.AnimateAsync(_hwnd, WindowOps.GetVisibleRect(_hwnd), target).ConfigureAwait(true);
        }
        else
        {
            _animator.Set(_hwnd, target);
        }

        MarkOwnMove();
    }

    private void SaveFloatingBounds()
    {
        if (_hwnd == 0 || _state.IsDocked || WindowOps.IsMinimized(_hwnd) || WindowOps.IsMaximized(_hwnd))
        {
            return;
        }

        var r = WindowOps.GetVisibleRect(_hwnd);
        if (!r.IsEmpty)
        {
            _profile.FloatingBounds = RectDto.From(r);
            _store.MarkDirty();
        }
    }

    private void MarkOwnMove() => _ownMoveTick = Environment.TickCount64;

    private bool IsOwnMove() =>
        _animator.IsAnimating ||
        Environment.TickCount64 - Math.Max(_ownMoveTick, _animator.LastFrameTick) < OwnMoveGraceMs;

    // ---- Target acquisition ----
    private void TryRecoverRememberedWindow()
    {
        var rt = _profile.Runtime;
        if (!rt.HiddenByAppDock || rt.LastHwnd == 0)
        {
            return;
        }

        var info = TargetFinder.FromRememberedHandle(_profile, (nint)rt.LastHwnd, rt.LastProcessId);
        if (info is not null)
        {
            _log.Warning("Recovering window {Hwnd} left hidden by a previous run", info.Handle);
            Attach(info);
        }
        else
        {
            rt.HiddenByAppDock = false;
            _store.MarkDirty();
        }
    }

    private void TryAcquire()
    {
        if (_disposed || _hwnd != 0)
        {
            return;
        }

        var info = TargetFinder.Find(_profile);
        if (info is not null)
        {
            Attach(info);
        }
    }

    private void Attach(WindowInfo info)
    {
        if (!ProcessInfo.IsCurrentProcessElevated && ProcessInfo.IsElevated(info.ProcessId) != false)
        {
            // Windows blocks moving windows of elevated apps from a normal process (UIPI).
            _log.Warning("{Target} runs as administrator; cannot control it", info.DisplayName);
            Notification?.Invoke(
                $"{_profile.Name} is running as administrator. Run AppDock as administrator to dock it.",
                NotificationKind.Warning);
            StopWaitingForTarget();
            _pollTimer = _bar.Window.Every(TimeSpan.FromSeconds(15), TryAcquire);
            return;
        }

        StopWaitingForTarget();
        _hwnd = info.Handle;
        _originalTopmost = WindowOps.IsTopmost(_hwnd);
        _profile.Runtime.LastHwnd = _hwnd;
        _profile.Runtime.LastProcessId = info.ProcessId;
        _store.MarkDirty();

        _tracker = new WindowTracker(_hwnd);
        _tracker.MoveSizeStarted += OnMoveSizeStarted;
        _tracker.MoveSizeEnded += OnMoveSizeEnded;
        _tracker.MinimizeStarted += () => Enqueue(new Minimized());
        _tracker.MinimizeEnded += () => Enqueue(new Restored());
        _tracker.LocationChanged += OnLocationChanged;
        _tracker.Shown += OnVisibilityEvent;
        _tracker.Hidden += OnVisibilityEvent;
        _tracker.Destroyed += OnTargetDestroyed;
        if (_settings.SuspendWindowsSnapWhileDragging)
        {
            _snapSuppressor = new WindowsSnapSuppressor(_bar.Window, _hwnd);
        }

        _log.Information("Attached to {Title} (hwnd {Hwnd}, pid {Pid}, class {Class})",
            info.Title, info.Handle, info.ProcessId, info.ClassName);
        Enqueue(new TargetFound(WindowOps.IsVisible(_hwnd), WindowOps.IsMinimized(_hwnd)));

        // Many apps restore their own saved bounds shortly after the window appears; check again then.
        _bar.Window.Delay(TimeSpan.FromMilliseconds(700), ScheduleEnforce);
        _bar.Window.Delay(TimeSpan.FromMilliseconds(1800), ScheduleEnforce);
    }

    private void DetachTarget()
    {
        _snapSuppressor?.Dispose();
        _snapSuppressor = null;
        _tracker?.Dispose();
        _tracker = null;
        _hwnd = 0;
    }

    private void OnTargetDestroyed()
    {
        _log.Information("Target window closed");
        SnapPreviewChanged?.Invoke(null);
        _inMoveSize = false;
        RestoreWindowsSnap();
        _bar.Release();
        DetachTarget();
        Enqueue(new TargetLost());
        StartWaitingForTarget();
    }

    private void StartWaitingForTarget()
    {
        if (_disposed)
        {
            return;
        }

        StopWaitingForTarget();
        _pollTimer = _bar.Window.Every(PollInterval, TryAcquire);
        _watcher = new WindowAppearanceWatcher();
        _watcher.TopLevelWindowAppeared += () =>
        {
            _acquireDebounce?.Dispose();
            _acquireDebounce = _bar.Window.Delay(TimeSpan.FromMilliseconds(150), TryAcquire);
        };
        Changed?.Invoke();
    }

    private void StopWaitingForTarget()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _acquireDebounce?.Dispose();
        _acquireDebounce = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---- Window events ----
    private void OnMoveSizeStarted()
    {
        _inMoveSize = true;
        _nudgeGuardUntil = 0; // the user owns the window now
        _gesture = MoveSizeKind.Unknown;
        _moveSizeStartRect = WindowOps.GetVisibleRect(_hwnd);
        _previewEdge = null;
        _previewMonitor = null;
    }

    private void RestoreWindowsSnap() => _snapSuppressor?.RestoreNow();

    private void OnMoveSizeEnded()
    {
        if (!_inMoveSize)
        {
            return;
        }

        _inMoveSize = false;
        RestoreWindowsSnap();
        SnapPreviewChanged?.Invoke(null);
        var gesture = _gesture == MoveSizeKind.Unknown
            ? DockGeometry.ClassifyMoveSize(_moveSizeStartRect, WindowOps.GetVisibleRect(_hwnd))
            : _gesture;

        // Still ambiguous (a pixel or two): for a docked window that is a tiny resize; re-applying the dock with the
        // measured thickness is always right. Never let it count as dragging the window away.
        if (gesture == MoveSizeKind.Unknown && _state.ReservesSpace)
        {
            gesture = MoveSizeKind.Resize;
        }

        var cursor = WindowOps.GetCursorPos();
        _log.Debug("Move/size ended: {Gesture} at {Cursor}, window {Rect}", gesture, cursor, WindowOps.GetVisibleRect(_hwnd));

        // Let Windows finish its own end-of-drag work (Snap) first, then take over.
        _bar.Window.Delay(TimeSpan.FromMilliseconds(30), () =>
        {
            if (_hwnd == 0)
            {
                return;
            }

            switch (gesture)
            {
                case MoveSizeKind.Move:
                    var monitor = Monitors.FromPoint(cursor);
                    var edge = DockGeometry.HitTestEdge(cursor, monitor.Bounds, monitor.ToPixels(_settings.SnapThresholdDip));
                    Enqueue(new DragEnded(edge, monitor.DeviceName, cursor));
                    break;

                case MoveSizeKind.Resize:
                    if (_state.ReservesSpace)
                    {
                        var r = WindowOps.GetVisibleRect(_hwnd);
                        var m = ResolveMonitor();
                        var px = DockGeometry.ClampThickness(DockGeometry.ThicknessOf(r, _state.Edge), m.Bounds, _state.Edge, m.Scale);
                        _profile.ThicknessDip[_state.Edge] = m.ToDip(px);
                        _store.MarkDirty();
                        _log.Information("Thickness for {Edge} set to {Dip} DIP", _state.Edge, m.ToDip(px));
                    }

                    Enqueue(new ResizeEnded());
                    break;

                default:
                    if (!_state.ReservesSpace)
                    {
                        _bar.Release(); // drop a query-only registration
                    }

                    break;
            }
        });
    }

    private void OnLocationChanged()
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (_inMoveSize)
        {
            TrackDrag();
            return;
        }

        if (TryUndoShellNudge())
        {
            return;
        }

        if (!IsOwnMove())
        {
            ScheduleEnforce();
        }
    }

    private void TrackDrag()
    {
        if (_gesture == MoveSizeKind.Unknown)
        {
            // Decide only once the change is unambiguous: the first pixel of a resize looks like a move.
            _gesture = DockGeometry.ClassifyMoveSize(_moveSizeStartRect, WindowOps.GetVisibleRect(_hwnd));
        }

        if (_gesture != MoveSizeKind.Move)
        {
            return;
        }

        var cursor = WindowOps.GetCursorPos();
        var monitor = Monitors.FromPoint(cursor);
        var edge = DockGeometry.HitTestEdge(cursor, monitor.Bounds, monitor.ToPixels(_settings.SnapThresholdDip));
        if (edge == _previewEdge && monitor.DeviceName == _previewMonitor)
        {
            return;
        }

        _previewEdge = edge;
        _previewMonitor = monitor.DeviceName;
        if (edge is not { } e)
        {
            SnapPreviewChanged?.Invoke(null);
            return;
        }

        var preview = _state.ReservesSpace && e == _state.Edge && monitor.DeviceName == ResolveMonitor().DeviceName
            ? _bar.ReservedRect
            : _bar.Query(e, monitor.Bounds, ThicknessPixels(monitor, e));
        SnapPreviewChanged?.Invoke(preview);
    }

    private void OnVisibilityEvent() => ReconcileVisibility(attempt: 0);

    /// <summary>
    /// Compare what the window does with what we believe, once our own work has settled. A mismatch means the
    /// app showed or hid itself (e.g. its own tray icon).
    /// </summary>
    private void ReconcileVisibility(int attempt)
    {
        _bar.Window.Delay(TimeSpan.FromMilliseconds(60), () =>
        {
            if (_hwnd == 0)
            {
                return;
            }

            if (_processing || IsOwnMove())
            {
                if (attempt < 20)
                {
                    ReconcileVisibility(attempt + 1);
                }

                return;
            }

            var visible = WindowOps.IsVisible(_hwnd);
            if (visible && _state.Visibility == WindowVisibility.Hidden)
            {
                Enqueue(new ExternallyShown());
            }
            else if (!visible && _state.Visibility == WindowVisibility.Visible)
            {
                Enqueue(new ExternallyHidden());
            }
        });
    }

    private void ScheduleEnforce()
    {
        if (!_state.ReservesSpace || _hwnd == 0)
        {
            return;
        }

        _enforceTimer?.Dispose();
        _enforceTimer = _bar.Window.Delay(EnforceDelay, Enforce);
    }

    /// <summary>
    /// Something other than AppDock moved the docked window (app restoring its bounds, Win+Arrow, maximize).
    /// Put it back. If the app keeps refusing the size (minimum width), adopt the size it insists on.
    /// </summary>
    private void Enforce()
    {
        if (_hwnd == 0 || !_state.ReservesSpace || _inMoveSize || _processing || IsOwnMove() ||
            WindowOps.IsMinimized(_hwnd) || !WindowOps.IsVisible(_hwnd))
        {
            return;
        }

        var actual = WindowOps.GetVisibleRect(_hwnd);
        if (actual.NearlyEquals(_expectedRect, 2) && !WindowOps.IsMaximized(_hwnd))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _enforceWindowStart > 3000)
        {
            _enforceWindowStart = now;
            _enforceAttempts = 0;
        }

        if (++_enforceAttempts > MaxEnforceAttempts)
        {
            var monitor = ResolveMonitor();
            var thickness = DockGeometry.ThicknessOf(actual, _state.Edge);
            _log.Information("{Target} insists on {Thickness}px; adopting it", _profile.Name, thickness);
            _profile.ThicknessDip[_state.Edge] = monitor.ToDip(
                DockGeometry.ClampThickness(thickness, monitor.Bounds, _state.Edge, monitor.Scale));
            _store.MarkDirty();
            _enforceAttempts = 0;
            _enforceWindowStart = now + 5000; // back off
        }

        _log.Debug("Re-applying dock: window at {Actual}, expected {Expected}", actual, _expectedRect);
        Enqueue(new Reapply());
    }

    /// <summary>
    /// The shell notifies every bar (including us, echoing our own SETPOS) when any bar moves. Only re-apply when
    /// our strip would actually change, so we never disturb the window for nothing (e.g. while the user grabs it).
    /// </summary>
    private void OnBarPositionChanged()
    {
        _posDebounce?.Dispose();
        _posDebounce = _bar.Window.Delay(TimeSpan.FromMilliseconds(80), () =>
        {
            if (!_state.ReservesSpace || _hwnd == 0 || _inMoveSize || _processing)
            {
                return;
            }

            var monitor = ResolveMonitor();
            var wanted = _bar.Query(_state.Edge, monitor.Bounds, ThicknessPixels(monitor, _state.Edge));
            if (!wanted.NearlyEquals(_bar.ReservedRect))
            {
                _log.Information("Another app bar changed; moving from {Old} to {New}", _bar.ReservedRect, wanted);
                Enqueue(new Reapply());
            }
        });
    }

    private void DebounceEnvironment(TimeSpan delay)
    {
        _envDebounce?.Dispose();
        _envDebounce = _bar.Window.Delay(delay, () =>
        {
            _log.Information("Environment changed; re-applying");
            if (_hwnd != 0 && !WindowOps.Exists(_hwnd))
            {
                OnTargetDestroyed();
                return;
            }

            Enqueue(new Reapply());
        });
    }

    private void OnFullScreenChanged(bool opened)
    {
        if (_hwnd == 0 || !_state.ReservesSpace || !_profile.AlwaysOnTop)
        {
            return;
        }

        // Like the taskbar: step back behind a full-screen app on our monitor, come back afterwards.
        // The shell's notification is a heuristic and also fires for ordinary windows, so verify it.
        if (opened)
        {
            var fg = WindowOps.GetForeground();
            var monitor = ResolveMonitor();
            if (!IsFullScreenOn(fg, monitor))
            {
                return;
            }

            _yieldedToFullScreen = true;
            WindowOps.SetTopmost(_hwnd, false);
            _log.Information("Full-screen app on {Monitor}; stepping behind it", monitor.DeviceName);
        }
        else if (_yieldedToFullScreen)
        {
            _yieldedToFullScreen = false;
            WindowOps.SetTopmost(_hwnd, true);
            _log.Information("Full-screen app closed; back on top");
        }
    }

    private bool IsFullScreenOn(nint window, MonitorInfo monitor)
    {
        if (window == 0 || window == _hwnd || WindowOps.GetProcessId(window) == Environment.ProcessId)
        {
            return false;
        }

        // Full screen = covers the whole monitor, taskbar area included (a maximized window does not).
        var r = WindowOps.GetWindowRect(window);
        var b = monitor.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    private void ApplyAnimationSettings()
    {
        _animator.Enabled = _settings.Animation.Enabled;
        _animator.Duration = TimeSpan.FromMilliseconds(_settings.Animation.DurationMs);
    }

    private static string Summarize(DockState s) =>
        $"{s.Mode}/{s.Edge}/{s.Visibility}/{(s.TargetPresent ? "present" : "absent")}";
}
