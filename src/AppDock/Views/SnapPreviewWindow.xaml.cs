using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rect = AppDock.Core.Geometry.Rect;
using AppDock.Core.Windows;
using Microsoft.Win32;

namespace AppDock.Views;

/// <summary>
/// Translucent accent-coloured overlay showing where a dragged window will dock (and, in picker mode, which
/// window will be chosen). Click-through, never activated, positioned in physical pixels.
/// </summary>
public partial class SnapPreviewWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private nint _hwnd;
    private Rect? _current;
    private bool _shown;

    public SnapPreviewWindow()
    {
        InitializeComponent();
        ApplyAccent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowOps.MakeOverlay(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
    }

    /// <summary>Show the overlay over <paramref name="rect"/> (physical pixels), or hide it when null.</summary>
    public void ShowAt(Rect? rect, string? label = null)
    {
        Label.Text = label ?? string.Empty;
        if (rect is not { IsEmpty: false } r)
        {
            FadeTo(0);
            _current = null;
            return;
        }

        if (!_shown)
        {
            Show();
            _shown = true;
        }

        if (_current != r)
        {
            ApplyAccent();
            var wasVisible = _current is not null;
            _current = r;
            WindowOps.PlaceTopmost(_hwnd, r);
            if (!wasVisible)
            {
                Opacity = 0;
            }
        }

        FadeTo(1);
    }

    private void FadeTo(double opacity)
    {
        var animation = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(opacity > 0 ? 140 : 110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private void ApplyAccent()
    {
        var accent = ReadAccentColor();
        Frame.Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, accent.R, accent.G, accent.B));
    }

    private static Color ReadAccentColor()
    {
        // HKCU\Software\Microsoft\Windows\DWM\AccentColor is stored as 0xAABBGGRR.
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
        {
            return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }

        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // We position in physical pixels ourselves; do not let WPF rescale the window between monitors.
        if (msg == WmDpiChanged)
        {
            handled = true;
            if (_current is { } r)
            {
                WindowOps.PlaceTopmost(_hwnd, r);
            }
        }

        return 0;
    }
}
