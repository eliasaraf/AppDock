using AppDock.Core.Geometry;
using AppDock.Core.Settings;
using Serilog;

namespace AppDock.Core.Docking;

/// <summary>
/// Owns the docking session for the active target profile and routes app-level commands to it. v1 runs one
/// active session; sessions are independent, so running several is a matter of keeping a list here.
/// </summary>
public sealed class DockManager : IDisposable
{
    private readonly SettingsStore _store;
    private readonly ILogger _log;
    private DockSession? _active;
    private bool _disposed;

    public DockManager(SettingsStore store, ILogger log)
    {
        _store = store;
        _log = log.ForContext<DockManager>();
    }

    public event Action? Changed;

    public event Action<Rect?>? SnapPreviewChanged;

    public event Action<string, NotificationKind>? Notification;

    public DockSession? Active => _active;

    public bool IsPaused => _store.Settings.Paused;

    public void Start()
    {
        if (!IsPaused)
        {
            StartSession(_store.Settings.ActiveProfile);
        }

        Changed?.Invoke();
    }

    public void SetActiveProfile(string profileId)
    {
        if (_store.Settings.Profiles.All(p => p.Id != profileId))
        {
            throw new ArgumentException($"Unknown profile {profileId}", nameof(profileId));
        }

        _store.Settings.ActiveProfileId = profileId;
        _store.MarkDirty();
        if (!IsPaused)
        {
            StartSession(_store.Settings.ActiveProfile);
        }

        Changed?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused)
        {
            return;
        }

        _store.Settings.Paused = paused;
        _store.MarkDirty();
        _log.Information(paused ? "Paused" : "Resumed");
        if (paused)
        {
            StopSession();
        }
        else
        {
            StartSession(_store.Settings.ActiveProfile);
        }

        Changed?.Invoke();
    }

    /// <summary>Restart the session (e.g. after editing the target's matching rules).</summary>
    public void Restart()
    {
        if (!IsPaused)
        {
            StartSession(_store.Settings.ActiveProfile);
        }

        Changed?.Invoke();
    }

    public void ToggleVisibility() => _active?.ToggleVisibility();

    public void DockTo(DockEdge edge) => _active?.DockTo(edge);

    public void Undock() => _active?.Undock();

    public void ApplySettings() => _active?.ApplySettings();

    public void EmergencyRestore() => _active?.EmergencyRestore();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSession();
    }

    private void StartSession(TargetProfile? profile)
    {
        StopSession();
        if (profile is null)
        {
            return;
        }

        var session = new DockSession(profile, _store, _log);
        session.Changed += OnSessionChanged;
        session.SnapPreviewChanged += OnSnapPreview;
        session.Notification += OnNotification;
        _active = session;
        session.Start();
    }

    private void StopSession()
    {
        if (_active is null)
        {
            return;
        }

        _active.Changed -= OnSessionChanged;
        _active.SnapPreviewChanged -= OnSnapPreview;
        _active.Notification -= OnNotification;
        _active.Dispose();
        _active = null;
        SnapPreviewChanged?.Invoke(null);
    }

    private void OnSessionChanged() => Changed?.Invoke();

    private void OnSnapPreview(Rect? rect) => SnapPreviewChanged?.Invoke(rect);

    private void OnNotification(string message, NotificationKind kind) => Notification?.Invoke(message, kind);
}
