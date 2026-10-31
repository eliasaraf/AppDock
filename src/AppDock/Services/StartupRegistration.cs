using System;
using Microsoft.Win32;
using Serilog;

namespace AppDock.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Command => $"\"{AppInfo.ExecutablePath}\" --startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppInfo.Name) is string value &&
               value.Contains(AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(AppInfo.Name, Command);
            }
            else if (key.GetValue(AppInfo.Name) is not null)
            {
                key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Error(ex, "Could not update the startup registration");
        }
    }
}
