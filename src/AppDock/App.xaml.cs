using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AppDock.Core.Docking;
using AppDock.Core.Input;
using AppDock.Core.Settings;
using AppDock.Core.Targeting;
using AppDock.Core.Updates;
using AppDock.Core.Windows;
using AppDock.Services;
using AppDock.Views;
using Serilog;
using Serilog.Events;
using WinForms = System.Windows.Forms;

namespace AppDock;

/// <summary>
/// Composition root. Command line: --startup (launched by Windows), --reset (restore default settings),
/// --verbose (debug logging), --shutdown (ask a running instance to exit), --uninstall (exit + remove autostart).
/// Internal: --apply-update (the helper copy that runs a silent update), --after-update (started by that helper).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Application lifetime: disposed in Cleanup on exit.")]
public partial class App : Application
{
    private const int MaxErrorsPerMinute = 3;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan UpdateIdleThreshold = TimeSpan.FromMinutes(15);

    private SingleInstance? _instance;
    private SettingsStore? _store;
    private MessageWindow? _ui;
    private DockManager? _manager;
    private HotkeyService? _hotkeys;
    private WindowPicker? _picker;
    private TrayIcon? _tray;
    private SnapPreviewWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private DispatcherTimer? _saveTimer;
    private AppServices? _services;
    private readonly CancellationTokenSource _lifetime = new();
    private int _errorCount;
    private long _errorWindowStart;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        ConfigureLogging(verbose: args.Contains("--verbose"));

        // Before the single-instance check: the helper is not a running AppDock and must not disturb one.
        var applyUpdate = Array.IndexOf(args, AutoUpdater.ApplyUpdateArgument);
        if (applyUpdate >= 0 && applyUpdate + 1 < args.Length)
        {
            AutoUpdater.RunInstallAndRelaunch(args[applyUpdate + 1]);
            QuitEarly();
            return;
        }

        _instance = SingleInstance.Acquire(
            onActivate: () => Dispatcher.BeginInvoke(ShowSettings),
            onShutdown: () => Dispatcher.BeginInvoke(ExitApp));

        if (args.Contains("--shutdown") || args.Contains("--uninstall"))
        {
            if (!_instance.IsFirst)
            {
                _instance.SignalShutdown();
                _instance.WaitForPrimaryExit(TimeSpan.FromSeconds(15));
            }

            if (args.Contains("--uninstall"))
            {
                StartupRegistration.Set(false);
                AutoUpdater.DeleteAll();
            }

            QuitEarly();
            return;
        }

        if (!_instance.IsFirst)
        {
            _instance.SignalActivate();
            QuitEarly();
            return;
        }

        InstallCrashHandlers();
        WinForms.Application.EnableVisualStyles();
        Log.Information("AppDock {Version} starting ({Args})", AppInfo.Version, string.Join(' ', args));

        _store = new SettingsStore(SettingsStore.DefaultPath, Log.Logger);
        if (args.Contains("--reset"))
        {
            _store.ResetToDefaults();
        }

        // Nothing is docked yet, so this is the least visible moment to update. After a failed try, wait for idle.
        AutoUpdater.CleanUp();
        if (!args.Contains(AutoUpdater.AfterUpdateArgument) &&
            UpdateChecker.IsConfigured(_store.Settings.UpdateFeedUrl) &&
            AutoUpdater.FindPending() is { } pendingUpdate &&
            AutoUpdater.TryStartInstall(pendingUpdate))
        {
            QuitEarly();
            return;
        }

        _ui = new MessageWindow("AppDock.Main");
        _manager = new DockManager(_store, Log.Logger);
        _overlay = new SnapPreviewWindow();
        _manager.SnapPreviewChanged += rect => _overlay.ShowAt(rect);
        _manager.Notification += (message, kind) => _tray?.Notify(message, kind);

        _hotkeys = new HotkeyService(_ui, Log.Logger);
        _hotkeys.Triggered += () => _manager.ToggleVisibility();
        _hotkeys.RegistrationFailed += message => _tray?.Notify(message, NotificationKind.Warning);

        _picker = new WindowPicker(_ui);
        _picker.HoverChanged += OnPickHover;

        var commands = new AppCommands(ShowSettings, ShowAbout, PickWindow, ExitApp);
        _services = new AppServices(_manager, _store, commands, ApplyHotkey, SuspendHotkey, ResetSettings);
        _tray = new TrayIcon(_manager, _store, commands);

        ApplyHotkey();
        _manager.Changed += () => _hotkeys.Paused = _manager.IsPaused;
        _manager.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => _store.SaveIfDirty();
        _saveTimer.Start();

        if (_store.RecoveredFromCorruption)
        {
            _tray.Notify("Your AppDock settings were damaged and have been reset. A backup was kept next to them.", NotificationKind.Warning);
        }

        if (!_store.Settings.FirstRunCompleted)
        {
            Dispatcher.BeginInvoke(ShowOnboarding, DispatcherPriority.ApplicationIdle);
        }
        else
        {
            if (_store.Settings.StartWithWindows && !StartupRegistration.IsEnabled())
            {
                StartupRegistration.Set(true); // keep autostart pointing at the current install
            }

            if (!args.Contains("--startup"))
            {
                _tray.Notify($"AppDock is running. {_manager.Active?.Describe()}.", NotificationKind.Info);
            }
        }

        _ = RunUpdateLoopAsync();
#if DEBUG
        if (args.Contains("--debug-tray-menu"))
        {
            Dispatcher.BeginInvoke(() => _tray.ShowMenu(), DispatcherPriority.ApplicationIdle);
        }
#endif
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Information("Windows session ending");
        ExitApp();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        Log.Information("AppDock exited");
        Log.CloseAndFlush();
        _instance?.Dispose();
        _lifetime.Dispose();
        base.OnExit(e);
    }

    // ---- Commands ----
    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Cleanup();
        Shutdown();
    }

    private void QuitEarly()
    {
        Log.CloseAndFlush();
        _instance?.Dispose();
        _instance = null;
        Shutdown();
    }

    private void Cleanup()
    {
        _lifetime.Cancel();
        _saveTimer?.Stop();
        _picker?.Dispose();
        _hotkeys?.Dispose();
        _manager?.Dispose(); // releases screen space and shows the app again
        _store?.SaveIfDirty();
        _tray?.Dispose();
        _overlay?.Close();
        _ui?.Dispose();
        _picker = null;
        _hotkeys = null;
        _manager = null;
        _tray = null;
        _overlay = null;
        _ui = null;
    }

    private void ShowSettings()
    {
        if (_services is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_services);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    private void ShowAbout()
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
        }

        _aboutWindow.Activate();
    }

    private void ShowOnboarding()
    {
        if (_store is null || _manager is null)
        {
            return;
        }

        var welcome = new OnboardingWindow();
        welcome.ShowDialog();

        _store.Settings.FirstRunCompleted = true;
        _store.Settings.StartWithWindows = welcome.StartWithWindows;
        StartupRegistration.Set(welcome.StartWithWindows);
        _store.MarkDirty();

        if (welcome.PickOtherApp)
        {
            PickWindow();
        }
        else
        {
            if (_store.Settings.Profiles.All(p => p.Id != BuiltInProfiles.ClaudeDesktopId))
            {
                _store.Settings.Profiles.Insert(0, BuiltInProfiles.ClaudeDesktop());
            }

            _manager.SetActiveProfile(BuiltInProfiles.ClaudeDesktopId);
            if (_manager.Active is { HasTarget: false })
            {
                _tray?.Notify("AppDock will dock Claude as soon as you open it.", NotificationKind.Info, force: true);
            }
        }
    }

    private async void PickWindow()
    {
        if (_picker is null || _manager is null || _store is null || _picker.IsActive)
        {
            return;
        }

        _tray?.Notify("Click the window you want to dock. Press Esc to cancel.", NotificationKind.Info, force: true);
        var hwnd = await _picker.PickAsync();
        if (hwnd is not { } picked || _manager is null)
        {
            return;
        }

        var info = WindowInfo.Capture(picked);
        var existing = _store.Settings.Profiles.FirstOrDefault(p => TargetFinder.Matches(p, info));
        var profile = existing ?? TargetFinder.CreateProfile(info);
        if (existing is null)
        {
            _store.Settings.Profiles.Add(profile);
        }

        _store.MarkDirty();
        _manager.SetActiveProfile(profile.Id);
        Log.Information("User picked {Process} ({Class}) as the target", info.ProcessName, info.ClassName);
        _tray?.Notify($"Now docking {profile.Name}.", NotificationKind.Info, force: true);
    }

    private void OnPickHover(nint hwnd)
    {
        if (_overlay is null)
        {
            return;
        }

        if (hwnd == 0)
        {
            _overlay.ShowAt(null);
            return;
        }

        var title = WindowOps.GetTitle(hwnd);
        _overlay.ShowAt(WindowOps.GetVisibleRect(hwnd), $"Click to dock{Environment.NewLine}{title}");
    }

    private void ApplyHotkey()
    {
        if (_hotkeys is not null && _store is not null)
        {
            _hotkeys.Configure(_store.Settings.Hotkey);
        }
    }

    private void SuspendHotkey(bool suspend)
    {
        if (_hotkeys is not null && _store is not null)
        {
            if (suspend)
            {
                _hotkeys.Configure(new HotkeySettings { Mode = HotkeyMode.Disabled });
            }
            else
            {
                ApplyHotkey();
            }
        }
    }

    private void ResetSettings()
    {
        if (_store is null || _manager is null)
        {
            return;
        }

        _manager.SetPaused(true);
        _store.ResetToDefaults();
        _store.Settings.FirstRunCompleted = true;
        _store.MarkDirty();
        _manager.SetPaused(false);
        _manager.Restart();
        ApplyHotkey();
        StartupRegistration.Set(_store.Settings.StartWithWindows);
    }

    /// <summary>Keeps AppDock current without interrupting: checks now and then, installs only when nobody is using the PC.</summary>
    private async Task RunUpdateLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _lifetime.Token);
            var nextCheck = DateTime.UtcNow;
            string? notifiedVersion = null;
            while (true)
            {
                var feedUrl = _store?.Settings.UpdateFeedUrl;
                if (DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow + UpdateCheckInterval;
                    var release = await UpdateChecker.CheckAsync(feedUrl, _lifetime.Token);
                    if (release is not null && release.Version != notifiedVersion && await NeedsManualInstallAsync(release))
                    {
                        notifiedVersion = release.Version;
                        Log.Information("Update available: {Version}", release.Version);
                        _tray?.Notify($"AppDock {release.Version} is available. Click to download it.", NotificationKind.Info,
                            force: true, onClick: () => AppInfo.OpenUrl(release.Url));
                    }
                }

                if (UpdateChecker.IsConfigured(feedUrl) && CanInstallUpdateNow() &&
                    AutoUpdater.FindPending() is { } installer && AutoUpdater.TryStartInstall(installer))
                {
                    ExitApp();
                    return;
                }

                await Task.Delay(TimeSpan.FromMinutes(1), _lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> NeedsManualInstallAsync(UpdateChecker.Release release)
    {
        if (!AutoUpdater.CanSelfUpdate || UpdatePolicy.Classify(AppInfo.Version, release.Version) != UpdateAction.InstallSilently)
        {
            return true;
        }

        return await AutoUpdater.PrepareAsync(release, _lifetime.Token) == PrepareResult.NeedsManualInstall;
    }

    private bool CanInstallUpdateNow()
    {
        var session = _manager?.Active;
        return UserActivity.IsIdleFor(UpdateIdleThreshold) &&
               UpdatePolicy.RestartKeepsDockState(session?.Profile.RestoreDockOnLaunch ?? true, session?.State);
    }

    // ---- Logging and crash handling ----
    private static void ConfigureLogging(bool verbose)
    {
        Directory.CreateDirectory(AppInfo.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(
                Path.Combine(AppInfo.LogsDirectory, "appdock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext:l} {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            OnRecoverableError(e.Exception);
        };
        MessageWindow.CallbackException += ex => Dispatcher.BeginInvoke(() => OnRecoverableError(ex));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Fatal unhandled exception");
            _manager?.EmergencyRestore();
            _store?.Save();
            Log.CloseAndFlush();
        };
    }

    /// <summary>
    /// Log and keep running. A burst of errors means something is persistently wrong: restore the desktop, explain,
    /// and exit cleanly rather than misbehave.
    /// </summary>
    private void OnRecoverableError(Exception ex)
    {
        Log.Error(ex, "Unhandled error");
        var now = Environment.TickCount64;
        if (now - _errorWindowStart > 60_000)
        {
            _errorWindowStart = now;
            _errorCount = 0;
        }

        if (++_errorCount < MaxErrorsPerMinute)
        {
            _tray?.Notify("AppDock recovered from an unexpected error. Details are in the log.", NotificationKind.Warning);
            return;
        }

        _manager?.EmergencyRestore();
        ShowCrashDialog(ex);
        ExitApp();
    }

    private static void ShowCrashDialog(Exception ex)
    {
        try
        {
            var copy = new WinForms.TaskDialogButton("Copy details") { AllowCloseDialog = false };
            copy.Click += (_, _) => Clipboard.SetText(ex.ToString());
            var logs = new WinForms.TaskDialogButton("Open logs") { AllowCloseDialog = false };
            logs.Click += (_, _) => AppInfo.OpenFolder(AppInfo.LogsDirectory);
            var page = new WinForms.TaskDialogPage
            {
                Caption = AppInfo.Name,
                Heading = "AppDock ran into a problem and will close",
                Text = "Your app has been restored and the screen space released. Start AppDock again from the Start menu. " +
                       "If this keeps happening, please send the log files to support.",
                Icon = WinForms.TaskDialogIcon.Error,
                Expander = new WinForms.TaskDialogExpander(ex.ToString()) { CollapsedButtonText = "Technical details" },
                Buttons = { copy, logs, WinForms.TaskDialogButton.Close },
            };
            WinForms.TaskDialog.ShowDialog(page);
        }
#pragma warning disable CA1031 // The crash dialog itself must never crash.
        catch (Exception dialogError)
#pragma warning restore CA1031
        {
            Log.Error(dialogError, "Could not show the crash dialog");
        }
    }
}
