using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AppDock.Core.Docking;
using AppDock.Core.Geometry;
using AppDock.Core.Input;
using AppDock.Core.Interop;
using AppDock.Core.Settings;
using AppDock.Core.Windows;
using AppDock.Services;

namespace AppDock.Views;

/// <summary>Settings page. Every change applies immediately, like Windows Settings; there is no Save button.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _sizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _loading = true; // until the first LoadAll: XAML raises change events while loading

    internal SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        FooterText.Text = $"{AppInfo.Name} {AppInfo.Version}   {AppInfo.Copyright}";
        _sizeDebounce.Tick += (_, _) =>
        {
            _sizeDebounce.Stop();
            ApplySize();
        };
        _services.Manager.Changed += OnManagerChanged;
        Closed += (_, _) => _services.Manager.Changed -= OnManagerChanged;
        LoadAll();
    }

    private AppSettings Settings => _services.Store.Settings;

    private TargetProfile? Profile => Settings.ActiveProfile;

    // State changes can come from drags, the tray or the hotkey: keep the live parts of the page in sync
    // without rebuilding controls the user may be interacting with.
    private void OnManagerChanged() => Dispatcher.BeginInvoke(RefreshLiveState);

    private void RefreshLiveState()
    {
        _loading = true;
        try
        {
            var session = _services.Manager.Active;
            StatusText.Text = _services.Manager.IsPaused ? "Paused" : session?.Describe() ?? "No app selected";
            TargetStatusText.Text = DescribeTarget(session);
            if (!ProfileBox.IsDropDownOpen && ProfileBox.SelectedItem != Profile)
            {
                ProfileBox.ItemsSource = null;
                ProfileBox.ItemsSource = Settings.Profiles;
                ProfileBox.SelectedItem = Profile;
                RemoveProfileButton.IsEnabled = Settings.Profiles.Count > 1;
            }

            if (!EdgeBox.IsDropDownOpen)
            {
                var edge = Profile?.Edge;
                SelectByTag(EdgeBox, edge?.ToString() ?? "Floating");
                SizeTitle.Text = DockGeometry.IsVertical(edge ?? DockEdge.Right) ? "Width" : "Height";
                SizeSlider.IsEnabled = edge is not null;
                if (!SizeSlider.IsMouseCaptureWithin && !_sizeDebounce.IsEnabled && edge is { } e)
                {
                    SizeSlider.Value = Profile!.GetThicknessDip(e);
                    SizeValue.Text = $"{(int)SizeSlider.Value} px";
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadAll()
    {
        _loading = true;
        try
        {
            var session = _services.Manager.Active;
            StatusText.Text = _services.Manager.IsPaused ? "Paused" : session?.Describe() ?? "No app selected";
            TargetStatusText.Text = DescribeTarget(session);

            ProfileBox.ItemsSource = null;
            ProfileBox.ItemsSource = Settings.Profiles;
            ProfileBox.SelectedItem = Profile;
            RemoveProfileButton.IsEnabled = Settings.Profiles.Count > 1;

            var p = Profile;
            var edge = p?.Edge;
            SelectByTag(EdgeBox, edge?.ToString() ?? "Floating");
            var sizeEdge = edge ?? DockEdge.Right;
            SizeTitle.Text = DockGeometry.IsVertical(sizeEdge) ? "Width" : "Height";
            SizeSlider.IsEnabled = edge is not null;
            SizeSlider.Value = p?.GetThicknessDip(sizeEdge) ?? 440;
            SizeValue.Text = $"{(int)SizeSlider.Value} px";

            var monitors = new List<MonitorChoice> { new(null, "Same as the app") };
            monitors.AddRange(Monitors.All().Select((m, i) => new MonitorChoice(
                m.DeviceName,
                $"Display {i + 1}{(m.IsPrimary ? " (main)" : string.Empty)}  {m.Bounds.Width}x{m.Bounds.Height}")));
            MonitorBox.ItemsSource = monitors;
            MonitorBox.SelectedItem = monitors.FirstOrDefault(m => m.Device == p?.Monitor) ?? monitors[0];

            TopmostBox.IsChecked = p?.AlwaysOnTop ?? true;
            AutoDockBox.IsChecked = p?.RestoreDockOnLaunch ?? true;

            var hk = Settings.Hotkey;
            SelectByTag(HotkeyModeBox, hk.Mode switch
            {
                HotkeyMode.Chord => "Chord",
                HotkeyMode.Disabled => "Disabled",
                _ => hk.TapKey.ToString(),
            });
            ChordBox.Visibility = hk.Mode == HotkeyMode.Chord ? Visibility.Visible : Visibility.Collapsed;
            ChordBox.Text = hk.ChordKey > 0 ? HotkeyText.Chord(hk.ChordModifiers, hk.ChordKey) : "Click, then press keys";
            TapSpeedCard.Visibility = hk.Mode == HotkeyMode.DoubleTap ? Visibility.Visible : Visibility.Collapsed;
            TapSpeedSlider.Value = hk.DoubleTapIntervalMs;
            TapSpeedValue.Text = $"{hk.DoubleTapIntervalMs} ms";

            AnimationsBox.IsChecked = Settings.Animation.Enabled;
            AnimationSlider.Value = Math.Max(AnimationSlider.Minimum, Settings.Animation.DurationMs);
            AnimationSlider.IsEnabled = Settings.Animation.Enabled;
            AnimationValue.Text = $"{(int)AnimationSlider.Value} ms";

            SuspendSnapBox.IsChecked = Settings.SuspendWindowsSnapWhileDragging;
            SnapSlider.Value = Settings.SnapThresholdDip;
            SnapValue.Text = $"{Settings.SnapThresholdDip} px";

            StartupBox.IsChecked = StartupRegistration.IsEnabled();
            NotificationsBox.IsChecked = Settings.ShowNotifications;
        }
        finally
        {
            _loading = false;
        }
    }

    private string DescribeTarget(DockSession? session)
    {
        if (_services.Manager.IsPaused)
        {
            return "AppDock is paused. Resume it from the notification-area menu.";
        }

        if (session is null)
        {
            return "Choose an app to dock.";
        }

        if (!session.HasTarget)
        {
            return $"Waiting for {session.Profile.Name} to open. It will be docked as soon as it appears.";
        }

        var title = WindowOps.GetTitle(session.TargetHandle);
        return $"Following \"{(string.IsNullOrWhiteSpace(title) ? session.Profile.Name : title)}\".";
    }

    // ---- App ----
    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not TargetProfile p || p.Id == Settings.ActiveProfileId)
        {
            return;
        }

        _services.Manager.SetActiveProfile(p.Id);
    }

    private void OnPickClicked(object sender, RoutedEventArgs e) => _services.Commands.PickWindow();

    private void OnRemoveProfileClicked(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } p || Settings.Profiles.Count <= 1)
        {
            return;
        }

        var answer = MessageBox.Show(this, $"Stop docking {p.Name} and remove it from AppDock?", AppInfo.Name,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        Settings.Profiles.Remove(p);
        _services.Store.MarkDirty();
        _services.Manager.SetActiveProfile(Settings.Profiles[0].Id);
    }

    // ---- Position ----
    private void OnEdgeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EdgeBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (tag == "Floating")
        {
            if (Profile is { } p)
            {
                p.Edge = null;
                _services.Store.MarkDirty();
            }

            _services.Manager.Undock();
        }
        else
        {
            var edge = Enum.Parse<DockEdge>(tag);
            if (Profile is { } p)
            {
                p.Edge = edge;
                _services.Store.MarkDirty();
            }

            _services.Manager.DockTo(edge);
        }

        LoadAll();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        SizeValue.Text = $"{(int)SizeSlider.Value} px";
        _sizeDebounce.Stop();
        _sizeDebounce.Start();
    }

    private void ApplySize()
    {
        if (Profile is not { Edge: { } edge })
        {
            return;
        }

        if (_services.Manager.Active is { } session)
        {
            session.SetThickness(edge, (int)SizeSlider.Value);
        }
        else
        {
            Profile.ThicknessDip[edge] = (int)SizeSlider.Value;
            _services.Store.MarkDirty();
        }
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorBox.SelectedItem is not MonitorChoice choice)
        {
            return;
        }

        if (_services.Manager.Active is { } session)
        {
            session.SetMonitor(choice.Device);
        }
        else if (Profile is { } p)
        {
            p.Monitor = choice.Device;
            _services.Store.MarkDirty();
        }
    }

    private void OnTopmostClicked(object sender, RoutedEventArgs e) => UpdateProfile(p => p.AlwaysOnTop = TopmostBox.IsChecked == true);

    private void OnAutoDockClicked(object sender, RoutedEventArgs e) => UpdateProfile(p => p.RestoreDockOnLaunch = AutoDockBox.IsChecked == true);

    private void UpdateProfile(Action<TargetProfile> change)
    {
        if (_loading || Profile is not { } p)
        {
            return;
        }

        change(p);
        _services.Store.MarkDirty();
        _services.Manager.ApplySettings();
    }

    // ---- Shortcut ----
    private void OnHotkeyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HotkeyModeBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var hk = Settings.Hotkey;
        switch (tag)
        {
            case "Chord":
                hk.Mode = HotkeyMode.Chord;
                if (hk.ChordKey <= 0)
                {
                    // A sensible default until the user records one.
                    hk.ChordModifiers = Native.MOD_CONTROL | Native.MOD_ALT;
                    hk.ChordKey = 0x44; // D
                }

                break;
            case "Disabled":
                hk.Mode = HotkeyMode.Disabled;
                break;
            default:
                hk.Mode = HotkeyMode.DoubleTap;
                hk.TapKey = Enum.Parse<TapKey>(tag);
                break;
        }

        _services.Store.MarkDirty();
        _services.ApplyHotkey();
        LoadAll();
    }

    private void OnChordFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _services.SuspendHotkey(true);
        ChordBox.Text = "Press the new shortcut...";
    }

    private void OnChordBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        _services.SuspendHotkey(false);
        _services.ApplyHotkey();
        LoadAll();
    }

    private void OnChordKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
        {
            return;
        }

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control))
        {
            mods |= Native.MOD_CONTROL;
        }

        if (m.HasFlag(ModifierKeys.Alt))
        {
            mods |= Native.MOD_ALT;
        }

        if (m.HasFlag(ModifierKeys.Shift))
        {
            mods |= Native.MOD_SHIFT;
        }

        if (m.HasFlag(ModifierKeys.Windows))
        {
            mods |= Native.MOD_WIN;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        var isFunctionKey = vk is >= 0x70 and <= 0x87;
        if (mods == 0 && !isFunctionKey)
        {
            ChordBox.Text = "Add Ctrl, Alt, Shift or Win";
            return;
        }

        Settings.Hotkey.Mode = HotkeyMode.Chord;
        Settings.Hotkey.ChordModifiers = mods;
        Settings.Hotkey.ChordKey = vk;
        _services.Store.MarkDirty();
        ChordBox.Text = HotkeyText.Chord(mods, vk);
        Keyboard.ClearFocus();
    }

    private void OnTapSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Hotkey.DoubleTapIntervalMs = (int)TapSpeedSlider.Value;
        TapSpeedValue.Text = $"{(int)TapSpeedSlider.Value} ms";
        _services.Store.MarkDirty();
        _services.ApplyHotkey();
    }

    // ---- Behaviour ----
    private void OnAnimationsClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Animation.Enabled = AnimationsBox.IsChecked == true;
        AnimationSlider.IsEnabled = Settings.Animation.Enabled;
        _services.Store.MarkDirty();
        _services.Manager.ApplySettings();
    }

    private void OnAnimationSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Animation.DurationMs = (int)AnimationSlider.Value;
        AnimationValue.Text = $"{(int)AnimationSlider.Value} ms";
        _services.Store.MarkDirty();
        _services.Manager.ApplySettings();
    }

    private void OnSuspendSnapClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.SuspendWindowsSnapWhileDragging = SuspendSnapBox.IsChecked == true;
        _services.Store.MarkDirty();
        _services.Manager.ApplySettings();
    }

    private void OnSnapDistanceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.SnapThresholdDip = (int)SnapSlider.Value;
        SnapValue.Text = $"{(int)SnapSlider.Value} px";
        _services.Store.MarkDirty();
    }

    // ---- General ----
    private void OnStartupClicked(object sender, RoutedEventArgs e)
    {
        Settings.StartWithWindows = StartupBox.IsChecked == true;
        StartupRegistration.Set(Settings.StartWithWindows);
        _services.Store.MarkDirty();
    }

    private void OnNotificationsClicked(object sender, RoutedEventArgs e)
    {
        Settings.ShowNotifications = NotificationsBox.IsChecked == true;
        _services.Store.MarkDirty();
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e) => AppInfo.OpenFolder(AppInfo.LogsDirectory);

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Reset all AppDock settings to their defaults?", AppInfo.Name,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _services.ResetSettings();
        LoadAll();
    }

    private static void SelectByTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

    private sealed record MonitorChoice(string? Device, string Label);
}
