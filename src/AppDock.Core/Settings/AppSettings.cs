using AppDock.Core.Geometry;

namespace AppDock.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<TargetProfile> Profiles { get; set; } = [];

    public string? ActiveProfileId { get; set; }

    public HotkeySettings Hotkey { get; set; } = new();

    public AnimationSettings Animation { get; set; } = new();

    /// <summary>Distance from a screen edge (in DIPs) at which dropping a dragged window docks it.</summary>
    public int SnapThresholdDip { get; set; } = 40;

    /// <summary>Suspend Windows Snap while the target is dragged, so the edges belong to AppDock.</summary>
    public bool SuspendWindowsSnapWhileDragging { get; set; } = true;

    public bool StartWithWindows { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    public bool Paused { get; set; }

    public const string DefaultUpdateFeedUrl = "https://api.github.com/repos/eliasaraf/AppDock/releases/latest";

    /// <summary>GitHub "latest release" API URL for automatic updates and update notifications. Set to empty to turn both off.</summary>
    public string? UpdateFeedUrl { get; set; } = DefaultUpdateFeedUrl;

    public TargetProfile? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();
}

public enum HotkeyMode
{
    DoubleTap,
    Chord,
    Disabled,
}

public enum TapKey
{
    Ctrl,
    Shift,
    Alt,
}

public sealed class HotkeySettings
{
    public HotkeyMode Mode { get; set; } = HotkeyMode.DoubleTap;

    public TapKey TapKey { get; set; } = TapKey.Ctrl;

    /// <summary>Maximum gap between the two taps.</summary>
    public int DoubleTapIntervalMs { get; set; } = 300;

    /// <summary>MOD_* flags for <see cref="HotkeyMode.Chord"/>.</summary>
    public uint ChordModifiers { get; set; }

    /// <summary>Virtual-key code for <see cref="HotkeyMode.Chord"/>.</summary>
    public int ChordKey { get; set; }
}

public sealed class AnimationSettings
{
    public bool Enabled { get; set; } = true;

    public int DurationMs { get; set; } = 200;
}

/// <summary>How to recognise one target application, plus its remembered dock layout.</summary>
public sealed class TargetProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Application";

    /// <summary>Process name without ".exe", case-insensitive.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Optional regex the full executable path must match.</summary>
    public string? ExePathPattern { get; set; }

    /// <summary>Optional exact window class.</summary>
    public string? WindowClass { get; set; }

    /// <summary>Optional regex the window title must match.</summary>
    public string? TitlePattern { get; set; }

    /// <summary>Docked edge; null means floating (always written, since the default is Right).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public DockEdge? Edge { get; set; } = DockEdge.Right;

    /// <summary>Monitor device name; null means "the monitor the window is on".</summary>
    public string? Monitor { get; set; }

    /// <summary>Thickness per edge in DIPs, so it survives DPI changes.</summary>
    public Dictionary<DockEdge, int> ThicknessDip { get; set; } = new()
    {
        [DockEdge.Left] = 440,
        [DockEdge.Right] = 440,
        [DockEdge.Top] = 340,
        [DockEdge.Bottom] = 340,
    };

    /// <summary>Last floating bounds (physical pixels).</summary>
    public RectDto? FloatingBounds { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Re-dock automatically when the app (or AppDock) starts.</summary>
    public bool RestoreDockOnLaunch { get; set; } = true;

    /// <summary>Crash-recovery breadcrumbs: which window AppDock last hid, so it can be shown again.</summary>
    public RuntimeState Runtime { get; set; } = new();

    public int GetThicknessDip(DockEdge edge) =>
        ThicknessDip.TryGetValue(edge, out var value) ? value : DockGeometry.IsVertical(edge) ? 440 : 340;

    public bool MatchesIdentity(TargetProfile other) =>
        string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(WindowClass, other.WindowClass, StringComparison.Ordinal);
}

public sealed class RuntimeState
{
    public long LastHwnd { get; set; }

    public uint LastProcessId { get; set; }

    public bool HiddenByAppDock { get; set; }
}

public sealed record RectDto(int Left, int Top, int Right, int Bottom)
{
    public static RectDto From(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public Rect ToRect() => new(Left, Top, Right, Bottom);
}

public static class BuiltInProfiles
{
    public const string ClaudeDesktopId = "claude-desktop";
    public const string TestTargetId = "appdock-test-target";

    /// <summary>
    /// Claude Desktop (Electron). Installed per-user under %LOCALAPPDATA%\AnthropicClaude or as an MSIX package.
    /// The path pattern excludes the Claude Code CLI, which is also named claude.exe.
    /// </summary>
    public static TargetProfile ClaudeDesktop() => new()
    {
        Id = ClaudeDesktopId,
        Name = "Claude",
        ProcessName = "claude",
        ExePathPattern = @"(?i)(\\AnthropicClaude\\|\\WindowsApps\\[^\\]*Claude[^\\]*\\|\\Programs\\Claude\\|\\Claude\\app-)",
        WindowClass = "Chrome_WidgetWin_1",
    };

    /// <summary>The bundled test window (tools/AppDock.TestTarget), for development and QA.</summary>
    public static TargetProfile TestTarget() => new()
    {
        Id = TestTargetId,
        Name = "AppDock Test Target",
        ProcessName = "AppDock.TestTarget",
    };
}
