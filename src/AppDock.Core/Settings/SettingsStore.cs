using System.Text.Json;
using System.Text.Json.Serialization;
using AppDock.Core.Geometry;
using Serilog;

namespace AppDock.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace), a corrupt file is
/// backed up and replaced by defaults, and every value is validated after loading.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _log;
    private readonly Lock _lock = new();

    public SettingsStore(string path, ILogger log)
    {
        Path = path;
        _log = log.ForContext<SettingsStore>();
        Settings = Load();
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppDock", "settings.json");

    public string Path { get; }

    public AppSettings Settings { get; private set; }

    /// <summary>True when the settings file was unreadable and defaults were restored.</summary>
    public bool RecoveredFromCorruption { get; private set; }

    public bool IsDirty { get; private set; }

    public event Action? Changed;

    /// <summary>Mark settings as changed; the host flushes them with <see cref="SaveIfDirty"/>.</summary>
    public void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke();
    }

    public void SaveIfDirty()
    {
        if (IsDirty)
        {
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path)!;
                Directory.CreateDirectory(dir);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, s_json));
                File.Move(temp, Path, overwrite: true);
                IsDirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(ex, "Could not save settings to {Path}", Path);
            }
        }
    }

    public void ResetToDefaults()
    {
        Settings = CreateDefaults();
        MarkDirty();
        Save();
    }

    public static AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        settings.Profiles.Add(BuiltInProfiles.ClaudeDesktop());
#if DEBUG
        settings.Profiles.Add(BuiltInProfiles.TestTarget());
#endif
        settings.ActiveProfileId = BuiltInProfiles.ClaudeDesktopId;
        return settings;
    }

    /// <summary>Parse settings JSON, migrate and validate. Exposed for tests.</summary>
    public static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, s_json)
            ?? throw new JsonException("Settings file is empty.");
        Migrate(settings);
        Validate(settings);
        return settings;
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, s_json);

    private AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            _log.Information("No settings file; using defaults");
            IsDirty = true;
            return CreateDefaults();
        }

        try
        {
            return Parse(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(Path, backup, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                _log.Warning(copyEx, "Could not back up corrupt settings");
            }

            _log.Error(ex, "Settings file was unreadable; backed up to {Backup} and restored defaults", backup);
            RecoveredFromCorruption = true;
            IsDirty = true;
            return CreateDefaults();
        }
    }

    private static void Migrate(AppSettings settings)
    {
        // Schema v1 is the first public schema. Future migrations go here, one step per version:
        // if (settings.SchemaVersion < 2) { ...; settings.SchemaVersion = 2; }
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            // Written by a newer AppDock. Read what we understand; unknown fields are dropped on save.
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    private static void Validate(AppSettings s)
    {
        s.Profiles ??= [];
        s.Profiles.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Id));
        s.Profiles = s.Profiles.DistinctBy(p => p.Id).ToList();
        if (s.Profiles.Count == 0)
        {
            s.Profiles.Add(BuiltInProfiles.ClaudeDesktop());
        }

        if (s.ActiveProfileId is null || s.Profiles.All(p => p.Id != s.ActiveProfileId))
        {
            s.ActiveProfileId = s.Profiles[0].Id;
        }

        foreach (var p in s.Profiles)
        {
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? p.ProcessName ?? "Application" : p.Name.Trim();
            p.ThicknessDip ??= [];
            foreach (var edge in Enum.GetValues<DockEdge>())
            {
                p.ThicknessDip[edge] = Math.Clamp(p.GetThicknessDip(edge), DockGeometry.MinThicknessDip, 4000);
            }

            if (p.Edge is { } e && !Enum.IsDefined(e))
            {
                p.Edge = DockEdge.Right;
            }

            if (p.FloatingBounds is { } fb && (fb.ToRect().IsEmpty || fb.ToRect().Width > 20000 || fb.ToRect().Height > 20000))
            {
                p.FloatingBounds = null;
            }

            p.Runtime ??= new RuntimeState();
            if (!IsValidRegex(p.ExePathPattern))
            {
                p.ExePathPattern = null;
            }

            if (!IsValidRegex(p.TitlePattern))
            {
                p.TitlePattern = null;
            }
        }

        s.Hotkey ??= new HotkeySettings();
        s.Hotkey.DoubleTapIntervalMs = Math.Clamp(s.Hotkey.DoubleTapIntervalMs, 150, 800);
        if (!Enum.IsDefined(s.Hotkey.Mode))
        {
            s.Hotkey.Mode = HotkeyMode.DoubleTap;
        }

        if (s.Hotkey.Mode == HotkeyMode.Chord && (s.Hotkey.ChordKey is <= 0 or > 0xFE))
        {
            s.Hotkey.Mode = HotkeyMode.DoubleTap;
        }

        s.Animation ??= new AnimationSettings();
        s.Animation.DurationMs = Math.Clamp(s.Animation.DurationMs, 0, 1000);
        s.SnapThresholdDip = Math.Clamp(s.SnapThresholdDip, 8, 200);
    }

    private static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
