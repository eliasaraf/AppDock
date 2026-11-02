using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppDock.Core.Updates;
using Serilog;

namespace AppDock.Services;

internal enum PrepareResult
{
    /// <summary>The installer is downloaded, verified and waiting for a quiet moment.</summary>
    Ready,

    /// <summary>Something temporary went wrong (network, corrupt download). Try again at the next check.</summary>
    RetryLater,

    /// <summary>This release cannot install silently, so the user has to install it by hand.</summary>
    NeedsManualInstall,
}

/// <summary>
/// Silent updates: downloads a verified installer to %LOCALAPPDATA%\AppDock\updates, then runs it through a copy of
/// this exe (<c>--apply-update</c>). The copy is needed because the installer replaces the installed exe and closes
/// the running AppDock; it waits for msiexec and starts AppDock again, also when the install failed.
/// </summary>
internal static class AutoUpdater
{
    public const string ApplyUpdateArgument = "--apply-update";
    public const string AfterUpdateArgument = "--after-update";

    private const int MaxInstallAttempts = 3;
    private const long MaxInstallerBytes = 50 * 1024 * 1024;
    private const string AttemptsSuffix = ".attempts";
    private const string HelperName = "AppDock-updater.exe";

    private static readonly string UpdatesDirectory = Path.Combine(AppInfo.DataDirectory, "updates");

    private static readonly string InstalledExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name, AppInfo.Name + ".exe");

    /// <summary>False for development runs and unsigned builds, which must never replace the real install.</summary>
    public static bool CanSelfUpdate { get; } =
        string.Equals(Path.GetFullPath(AppInfo.ExecutablePath), InstalledExecutable, StringComparison.OrdinalIgnoreCase) &&
        PackageVerifier.SignerThumbprint(AppInfo.ExecutablePath) is not null;

    public static async Task<PrepareResult> PrepareAsync(UpdateChecker.Release release, CancellationToken token)
    {
        if (release.InstallerUrl is null || release.InstallerSha256 is null || release.InstallerSize is <= 0 or > MaxInstallerBytes)
        {
            Log.Warning("Release {Version} has no usable installer asset", release.Version);
            return PrepareResult.NeedsManualInstall;
        }

        var target = Path.Combine(UpdatesDirectory, UpdatePolicy.AssetName(release.Version));
        if (ReadAttempts(target) >= MaxInstallAttempts)
        {
            return PrepareResult.NeedsManualInstall;
        }

        var temp = target + ".download";
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            if (File.Exists(target) && PackageVerifier.ComputeSha256(target) == release.InstallerSha256)
            {
                return PrepareResult.Ready;
            }

            await DownloadAsync(release.InstallerUrl, temp, token).ConfigureAwait(false);
            if (PackageVerifier.ComputeSha256(temp) != release.InstallerSha256)
            {
                Log.Warning("Downloaded installer for {Version} does not match its published hash", release.Version);
                return PrepareResult.RetryLater;
            }

            if (!PackageVerifier.IsSignedBySameCertificate(temp, AppInfo.ExecutablePath))
            {
                Log.Warning("Installer for {Version} is not signed by the certificate of this build", release.Version);
                return PrepareResult.NeedsManualInstall;
            }

            File.Move(temp, target, overwrite: true);
            Log.Information("Update {Version} downloaded", release.Version);
            return PrepareResult.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            Log.Information("Update download failed: {Message}", ex.Message);
            return PrepareResult.RetryLater;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>The newest downloaded installer that is newer than this build and has attempts left, if any.</summary>
    public static string? FindPending()
    {
        if (!CanSelfUpdate || !Directory.Exists(UpdatesDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(UpdatesDirectory, "*.msi")
            .Select(path => (path, version: UpdatePolicy.VersionFromAssetName(Path.GetFileName(path))))
            .Where(f => f.version is not null && UpdatePolicy.Classify(AppInfo.Version, f.version) == UpdateAction.InstallSilently)
            .Where(f => ReadAttempts(f.path) < MaxInstallAttempts)
            .OrderByDescending(f => Version.Parse(f.version!))
            .Select(f => f.path)
            .FirstOrDefault();
    }

    /// <summary>Starts the install. On true the caller must exit straight away.</summary>
    public static bool TryStartInstall(string installer)
    {
        try
        {
            // Checked again: the file has been sitting on disk since the download, possibly across restarts.
            if (!PackageVerifier.IsSignedBySameCertificate(installer, AppInfo.ExecutablePath))
            {
                Log.Warning("Discarding {Installer}: signature check failed", installer);
                TryDelete(installer);
                return false;
            }

            // Counted first, so an install that keeps failing or crashing cannot restart AppDock forever.
            File.WriteAllText(installer + AttemptsSuffix, (ReadAttempts(installer) + 1).ToString(CultureInfo.InvariantCulture));

            var helper = Path.Combine(UpdatesDirectory, HelperName);
            File.Copy(AppInfo.ExecutablePath, helper, overwrite: true);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(ApplyUpdateArgument);
            start.ArgumentList.Add(installer);
            Process.Start(start)?.Dispose();
            Log.Information("Installing update from {Installer}", installer);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warning("Could not start the update: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Runs in the helper copy: installs without UI, then starts the installed AppDock again.</summary>
    public static void RunInstallAndRelaunch(string installer)
    {
        try
        {
            var msiexec = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/i", installer, "/qn", "/norestart", "/l*v", Path.Combine(UpdatesDirectory, "install.log") })
            {
                msiexec.ArgumentList.Add(argument);
            }

            using var process = Process.Start(msiexec);
            process?.WaitForExit();
            Log.Information("Installer exited with code {ExitCode}", process?.ExitCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Could not run the installer");
        }

        try
        {
            var relaunch = new ProcessStartInfo(InstalledExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(InstalledExecutable) };
            relaunch.ArgumentList.Add("--startup");
            relaunch.ArgumentList.Add(AfterUpdateArgument);
            Process.Start(relaunch)?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Could not restart AppDock after the update");
        }
    }

    /// <summary>Removes installers that are no longer newer than this build. The helper may still be exiting; next time then.</summary>
    public static void CleanUp()
    {
        if (!Directory.Exists(UpdatesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(UpdatesDirectory))
        {
            var name = Path.GetFileName(file);
            var assetName = name.EndsWith(AttemptsSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^AttemptsSuffix.Length] : name;
            var version = UpdatePolicy.VersionFromAssetName(assetName);
            var stale = name.Equals(HelperName, StringComparison.OrdinalIgnoreCase) ||
                        (version is not null && UpdatePolicy.Classify(AppInfo.Version, version) != UpdateAction.InstallSilently);
            if (stale)
            {
                TryDelete(file);
            }
        }
    }

    public static void DeleteAll()
    {
        try
        {
            if (Directory.Exists(UpdatesDirectory))
            {
                Directory.Delete(UpdatesDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Information("Could not remove downloaded updates: {Message}", ex.Message);
        }
    }

    private static async Task DownloadAsync(Uri url, string path, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxInstallerBytes)
        {
            throw new IOException("Installer is larger than expected.");
        }

        var output = File.Create(path);
        await using (output.ConfigureAwait(false))
        {
            await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }

    private static int ReadAttempts(string installer)
    {
        try
        {
            var path = installer + AttemptsSuffix;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path), out var attempts) ? attempts : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Could not read install attempts: {Message}", ex.Message);
            return MaxInstallAttempts; // cannot tell, so do not risk a restart loop
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }
}
