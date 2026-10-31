using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AppDock.Core.Docking;
using AppDock.Core.Geometry;
using AppDock.Core.Input;
using AppDock.Core.Settings;
using AppDock.Core.Windows;
using WinForms = System.Windows.Forms;

namespace AppDock.Services;

/// <summary>
/// Notification-area icon. Left click toggles the docked app; right click opens a Fluent-styled WPF menu that
/// reflects the current state.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly DockManager _manager;
    private readonly SettingsStore _store;
    private readonly AppCommands _commands;
    private ContextMenu? _menu;
    private Action? _balloonClick;

    public TrayIcon(DockManager manager, SettingsStore store, AppCommands commands)
    {
        _manager = manager;
        _store = store;
        _commands = commands;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("AppDock.ico")
            ?? throw new InvalidOperationException("Icon resource missing.");
        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize),
            Text = AppInfo.Name,
            Visible = true,
        };
        _icon.MouseUp += OnMouseUp;
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        _manager.Changed += Refresh;
        Refresh();
    }

    /// <param name="force">Show even when the user turned informational notifications off (direct feedback).</param>
    /// <param name="onClick">Optional action when the user clicks the notification.</param>
    public void Notify(string message, NotificationKind kind, bool force = false, Action? onClick = null)
    {
        if (!force && !_store.Settings.ShowNotifications && kind == NotificationKind.Info)
        {
            return;
        }

        var icon = kind switch
        {
            NotificationKind.Error => WinForms.ToolTipIcon.Error,
            NotificationKind.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, AppInfo.Name, message, icon);
    }

    public void Dispose()
    {
        _manager.Changed -= Refresh;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void Refresh()
    {
        var text = _manager.IsPaused ? $"{AppInfo.Name} - paused" : $"{AppInfo.Name} - {_manager.Active?.Describe() ?? "no app selected"}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            if (_manager.Active is { HasTarget: true })
            {
                _manager.ToggleVisibility();
            }
            else
            {
                _commands.ShowSettings();
            }
        }
        else if (e.Button == WinForms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    internal void ShowMenu()
    {
        if (_menu is not null)
        {
            _menu.IsOpen = false;
        }

        var session = _manager.Active;
        var state = session?.State;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        menu.Items.Add(new MenuItem
        {
            Header = _manager.IsPaused ? "AppDock is paused" : session?.Describe() ?? "No app selected",
            IsEnabled = false,
            Icon = Glyph(""),
        });
        menu.Items.Add(new Separator());

        var hasTarget = session is { HasTarget: true } && !_manager.IsPaused;
        var hidden = state?.Visibility == WindowVisibility.Hidden;
        menu.Items.Add(Item(hidden ? "Show" : "Hide", hidden ? "" : "", _manager.ToggleVisibility, hasTarget,
            HotkeyText.Describe(_store.Settings.Hotkey)));

        var dock = new MenuItem { Header = "Dock to", Icon = Glyph(""), IsEnabled = session is not null && !_manager.IsPaused };
        foreach (var (edge, label, glyph) in new[]
                 {
                     (DockEdge.Left, "Left", ""), (DockEdge.Right, "Right", ""),
                     (DockEdge.Top, "Top", ""), (DockEdge.Bottom, "Bottom", ""),
                 })
        {
            var item = Item(label, glyph, () => _manager.DockTo(edge));
            item.IsCheckable = true;
            item.IsChecked = state is { IsDocked: true } && state.Edge == edge;
            dock.Items.Add(item);
        }

        dock.Items.Add(new Separator());
        var floating = Item("Floating (undock)", "", _manager.Undock);
        floating.IsCheckable = true;
        floating.IsChecked = state is { IsDocked: false };
        dock.Items.Add(floating);
        menu.Items.Add(dock);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Choose app window...", "", _commands.PickWindow));
        menu.Items.Add(Item("Settings", "", _commands.ShowSettings));
        var pause = Item(_manager.IsPaused ? "Resume AppDock" : "Pause AppDock", _manager.IsPaused ? "" : "",
            () => _manager.SetPaused(!_manager.IsPaused));
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("About AppDock", "", _commands.ShowAbout));
        menu.Items.Add(Item("Exit", "", _commands.Exit));

        menu.Opened += (_, _) =>
        {
            // Make the menu's popup the foreground window so clicking anywhere else closes it.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                WindowOps.ForceForeground(source.Handle);
            }
        };
        menu.Closed += (_, _) => _menu = null;
        _menu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string glyph, Action action, bool enabled = true, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Icon = Glyph(glyph), IsEnabled = enabled, InputGestureText = gesture ?? string.Empty };
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 14,
    };
}

/// <summary>App-level actions shared by the tray menu, settings and onboarding.</summary>
internal sealed record AppCommands(Action ShowSettings, Action ShowAbout, Action PickWindow, Action Exit);
