using System.Globalization;
using AppDock.Core.Docking;

namespace AppDock.Core.Updates;

public enum UpdateAction
{
    None,
    InstallSilently,
    NotifyOnly,
}

/// <summary>The rules for automatic updates, separate from the download and install work so they can be tested.</summary>
public static class UpdatePolicy
{
    private const string AssetPrefix = "AppDock-";
    private const string AssetSuffix = "-x64.msi";
    private const string Sha256Prefix = "sha256:";

    /// <summary>Same major version installs silently; a new major version may change behavior, so the user decides.</summary>
    public static UpdateAction Classify(string currentVersion, string remoteVersion)
    {
        if (!Version.TryParse(currentVersion, out var current) ||
            !Version.TryParse(remoteVersion, out var remote) ||
            remote <= current)
        {
            return UpdateAction.None;
        }

        return remote.Major == current.Major ? UpdateAction.InstallSilently : UpdateAction.NotifyOnly;
    }

    /// <summary>
    /// Installing restarts AppDock, and a restart only keeps what the settings describe. Anything else (hidden with
    /// the hotkey, or docked or undocked against the "restore on launch" setting) has to wait for the next start.
    /// </summary>
    public static bool RestartKeepsDockState(bool restoreDockOnLaunch, DockState? state)
    {
        if (state is null || !state.TargetPresent)
        {
            return true;
        }

        return state.IsDocked
            ? restoreDockOnLaunch && state.Visibility == WindowVisibility.Visible
            : !restoreDockOnLaunch;
    }

    public static string AssetName(string version) => AssetPrefix + version + AssetSuffix;

    /// <summary>Reads the version out of an installer file name; null if it is not one of ours.</summary>
    public static string? VersionFromAssetName(string fileName)
    {
        if (!fileName.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = fileName[AssetPrefix.Length..^AssetSuffix.Length];
        return Version.TryParse(version, out _) ? version : null;
    }

    /// <summary>Reads GitHub's asset digest ("sha256:&lt;64 hex digits&gt;") as lowercase hex; null if it is anything else.</summary>
    public static string? ParseSha256Digest(string? digest)
    {
        if (digest is null || !digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[Sha256Prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLower(CultureInfo.InvariantCulture) : null;
    }
}
