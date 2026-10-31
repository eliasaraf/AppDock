using AppDock.Core.Geometry;
using AppDock.Core.Settings;
using AppDock.Core.Targeting;
using Serilog;

namespace AppDock.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AppDockTests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Defaults_contain_claude_desktop_docked_right()
    {
        var s = SettingsStore.CreateDefaults();
        var claude = Assert.Single(s.Profiles, p => p.Id == BuiltInProfiles.ClaudeDesktopId);
        Assert.Equal(DockEdge.Right, claude.Edge);
        Assert.Equal(claude, s.ActiveProfile);
        Assert.Equal(HotkeyMode.DoubleTap, s.Hotkey.Mode);
        Assert.Equal(TapKey.Ctrl, s.Hotkey.TapKey);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var s = SettingsStore.CreateDefaults();
        s.Profiles[0].Edge = DockEdge.Top;
        s.Profiles[0].ThicknessDip[DockEdge.Top] = 512;
        s.Profiles[0].FloatingBounds = new RectDto(10, 20, 810, 620);
        s.Hotkey.DoubleTapIntervalMs = 250;

        var back = SettingsStore.Parse(SettingsStore.Serialize(s));
        Assert.Equal(DockEdge.Top, back.Profiles[0].Edge);
        Assert.Equal(512, back.Profiles[0].ThicknessDip[DockEdge.Top]);
        Assert.Equal(new RectDto(10, 20, 810, 620), back.Profiles[0].FloatingBounds);
        Assert.Equal(250, back.Hotkey.DoubleTapIntervalMs);
    }

    [Fact]
    public void Floating_edge_null_round_trips()
    {
        var s = SettingsStore.CreateDefaults();
        s.Profiles[0].Edge = null;
        Assert.Null(SettingsStore.Parse(SettingsStore.Serialize(s)).Profiles[0].Edge);
    }

    [Fact]
    public void Validation_clamps_out_of_range_values_and_drops_bad_regexes()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "profiles": [ { "id": "x", "name": " ", "processName": "foo", "exePathPattern": "([", "thicknessDip": { "Left": 5 } } ],
          "activeProfileId": "missing",
          "hotkey": { "mode": "Chord", "chordKey": 0, "doubleTapIntervalMs": 5 },
          "animation": { "durationMs": 99999 },
          "snapThresholdDip": 0
        }
        """;
        var s = SettingsStore.Parse(json);
        var p = Assert.Single(s.Profiles);
        Assert.Equal("x", s.ActiveProfileId);
        Assert.Equal("foo", p.Name);
        Assert.Null(p.ExePathPattern);
        Assert.Equal(DockGeometry.MinThicknessDip, p.ThicknessDip[DockEdge.Left]);
        Assert.Equal(440, p.ThicknessDip[DockEdge.Right]);
        Assert.Equal(HotkeyMode.DoubleTap, s.Hotkey.Mode); // invalid chord falls back
        Assert.Equal(150, s.Hotkey.DoubleTapIntervalMs);
        Assert.Equal(1000, s.Animation.DurationMs);
        Assert.Equal(8, s.SnapThresholdDip);
    }

    [Fact]
    public void Empty_profile_list_gets_claude_default()
    {
        var s = SettingsStore.Parse("""{ "profiles": [] }""");
        Assert.Equal(BuiltInProfiles.ClaudeDesktopId, Assert.Single(s.Profiles).Id);
    }

    [Fact]
    public void Newer_schema_is_read_best_effort()
    {
        var s = SettingsStore.Parse("""{ "schemaVersion": 99, "futureField": true, "profiles": [ { "id": "a", "processName": "x" } ] }""");
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal("a", s.ActiveProfileId);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_restored()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = new SettingsStore(SettingsPath, new LoggerConfiguration().CreateLogger());
        Assert.True(store.RecoveredFromCorruption);
        Assert.NotEmpty(store.Settings.Profiles);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Save_is_atomic_and_loadable()
    {
        var store = new SettingsStore(SettingsPath, new LoggerConfiguration().CreateLogger());
        store.Settings.SnapThresholdDip = 55;
        store.MarkDirty();
        store.SaveIfDirty();
        Assert.False(store.IsDirty);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        var reloaded = new SettingsStore(SettingsPath, new LoggerConfiguration().CreateLogger());
        Assert.Equal(55, reloaded.Settings.SnapThresholdDip);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\AnthropicClaude\app-0.10.0\claude.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_0.10.0.0_x64__pzs8sxrjxfjjc\app\claude.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Claude\claude.exe", true)]
    [InlineData(@"C:\Users\x\.local\bin\claude.exe", false)] // Claude Code CLI
    public void Claude_profile_matches_desktop_installs_only(string path, bool expected)
    {
        var w = new WindowInfo(1, 1, "claude", path, "Chrome_WidgetWin_1", "Claude", true, new Rect(0, 0, 800, 600));
        Assert.Equal(expected, TargetFinder.Matches(BuiltInProfiles.ClaudeDesktop(), w));
    }

    [Fact]
    public void Matching_checks_process_and_class()
    {
        var profile = new TargetProfile { ProcessName = "Notepad", WindowClass = "Notepad" };
        Assert.True(TargetFinder.Matches(profile, new WindowInfo(1, 1, "notepad", null, "Notepad", "x", true, default)));
        Assert.False(TargetFinder.Matches(profile, new WindowInfo(1, 1, "notepad", null, "Other", "x", true, default)));
        Assert.False(TargetFinder.Matches(profile, new WindowInfo(1, 1, "calc", null, "Notepad", "x", true, default)));
    }

    [Fact]
    public void Profile_without_rules_matches_nothing() =>
        Assert.False(TargetFinder.Matches(new TargetProfile(), new WindowInfo(1, 1, "a", null, "b", "c", true, default)));
}
