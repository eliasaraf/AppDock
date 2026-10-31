using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AppDock.Services;

/// <summary>Product identity and well-known paths.</summary>
internal static class AppInfo
{
    public const string Name = "AppDock";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0";

    public static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AppDock.exe");

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
