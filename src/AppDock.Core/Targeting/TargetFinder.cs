using System.Text.RegularExpressions;
using AppDock.Core.Geometry;
using AppDock.Core.Settings;
using AppDock.Core.Windows;

namespace AppDock.Core.Targeting;

/// <summary>Snapshot of a top-level window, as seen when matching targets.</summary>
public sealed record WindowInfo(
    nint Handle,
    uint ProcessId,
    string? ProcessName,
    string? ExePath,
    string ClassName,
    string Title,
    bool IsVisible,
    Rect Bounds)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName ?? ClassName : Title;

    public static WindowInfo Capture(nint hwnd)
    {
        var pid = WindowOps.GetProcessId(hwnd);
        var path = ProcessInfo.GetImagePath(pid);
        return new WindowInfo(
            hwnd,
            pid,
            path is null ? null : Path.GetFileNameWithoutExtension(path),
            path,
            WindowOps.GetClassName(hwnd),
            WindowOps.GetTitle(hwnd),
            WindowOps.IsVisible(hwnd),
            WindowOps.GetVisibleRect(hwnd));
    }
}

/// <summary>Finds the main window of a target application.</summary>
public static class TargetFinder
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Best matching main window for <paramref name="profile"/>: a visible, unowned, non-tool top-level window;
    /// the largest wins (Electron apps own many small helper windows).
    /// </summary>
    public static WindowInfo? Find(TargetProfile profile, uint excludeProcessId = 0) =>
        Candidates(profile, excludeProcessId).MaxBy(w => (long)w.Bounds.Width * w.Bounds.Height);

    public static IEnumerable<WindowInfo> Candidates(TargetProfile profile, uint excludeProcessId = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var pathCache = new Dictionary<uint, string?>();
        foreach (var hwnd in WindowOps.EnumerateTopLevel())
        {
            if (!IsMainWindowCandidate(hwnd))
            {
                continue;
            }

            var pid = WindowOps.GetProcessId(hwnd);
            if (pid == excludeProcessId || pid == Environment.ProcessId)
            {
                continue;
            }

            // Cheap filters first: class name, then the (cached) process path.
            var cls = WindowOps.GetClassName(hwnd);
            if (profile.WindowClass is { Length: > 0 } wantClass && !string.Equals(cls, wantClass, StringComparison.Ordinal))
            {
                continue;
            }

            if (!pathCache.TryGetValue(pid, out var path))
            {
                path = ProcessInfo.GetImagePath(pid);
                pathCache[pid] = path;
            }

            var info = new WindowInfo(
                hwnd, pid, path is null ? null : Path.GetFileNameWithoutExtension(path), path, cls,
                WindowOps.GetTitle(hwnd), true, WindowOps.GetVisibleRect(hwnd));
            if (Matches(profile, info))
            {
                yield return info;
            }
        }
    }

    /// <summary>Validate a remembered handle (crash recovery); hidden windows are accepted here.</summary>
    public static WindowInfo? FromRememberedHandle(TargetProfile profile, nint hwnd, uint pid)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!WindowOps.Exists(hwnd) || WindowOps.GetProcessId(hwnd) != pid)
        {
            return null;
        }

        var info = WindowInfo.Capture(hwnd);
        return Matches(profile, info) ? info : null;
    }

    public static bool Matches(TargetProfile profile, WindowInfo w)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(w);
        if (profile.ProcessName is { Length: > 0 } name &&
            !string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (profile.WindowClass is { Length: > 0 } cls && !string.Equals(w.ClassName, cls, StringComparison.Ordinal))
        {
            return false;
        }

        // When the path cannot be read (e.g. access denied) do not reject on it; other rules still apply.
        if (profile.ExePathPattern is { Length: > 0 } pathPattern && w.ExePath is not null &&
            !SafeIsMatch(w.ExePath, pathPattern))
        {
            return false;
        }

        if (profile.TitlePattern is { Length: > 0 } titlePattern && !SafeIsMatch(w.Title, titlePattern))
        {
            return false;
        }

        // A profile with no rules at all must not match everything.
        return profile.ProcessName is { Length: > 0 } || profile.WindowClass is { Length: > 0 } ||
               profile.ExePathPattern is { Length: > 0 } || profile.TitlePattern is { Length: > 0 };
    }

    /// <summary>Visible, unowned, non-tool top-level windows that the user would call "an app window".</summary>
    public static IReadOnlyList<WindowInfo> ListAppWindows()
    {
        var result = new List<WindowInfo>();
        foreach (var hwnd in WindowOps.EnumerateTopLevel())
        {
            if (!IsMainWindowCandidate(hwnd) || WindowOps.IsCloaked(hwnd))
            {
                continue;
            }

            var info = WindowInfo.Capture(hwnd);
            if (info.ProcessId != Environment.ProcessId && !string.IsNullOrWhiteSpace(info.Title) &&
                info.Bounds.Width >= 120 && info.Bounds.Height >= 80)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>Build a profile that recognises the app owning <paramref name="w"/>.</summary>
    public static TargetProfile CreateProfile(WindowInfo w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var name = w.ProcessName ?? "Application";
        if (w.ExePath is not null)
        {
            try
            {
                var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(w.ExePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    name = description.Trim();
                }
            }
            catch (FileNotFoundException)
            {
            }
        }

        return new TargetProfile
        {
            Name = name,
            ProcessName = w.ProcessName,
            // No path rule: auto-updating apps (Squirrel, MSIX) change their install path on every update.
            WindowClass = w.ClassName,
        };
    }

    private static bool IsMainWindowCandidate(nint hwnd) =>
        WindowOps.IsVisible(hwnd) && WindowOps.GetOwner(hwnd) == 0 && !WindowOps.IsToolWindow(hwnd);

    private static bool SafeIsMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
