using System.Runtime.InteropServices;
using AppDock.Core.Interop;

namespace AppDock.Core.Windows;

public static class ProcessInfo
{
    private static readonly Lazy<bool> s_selfElevated = new(() => IsElevated((uint)Environment.ProcessId) ?? false);

    public static bool IsCurrentProcessElevated => s_selfElevated.Value;

    /// <summary>Full executable path, or null when the process cannot be queried.</summary>
    public static unsafe string? GetImagePath(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return Native.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Process name without extension (e.g. <c>claude</c>), or null.</summary>
    public static string? GetProcessName(uint pid)
    {
        var path = GetImagePath(pid);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Whether the process runs elevated; null when unknown (access denied usually means elevated).</summary>
    public static bool? IsElevated(uint pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
            {
                return null;
            }

            try
            {
                return Native.GetTokenInformation(token, Native.TokenElevation, out var elevation,
                    Marshal.SizeOf<TOKEN_ELEVATION>(), out _)
                    ? elevation.TokenIsElevated != 0
                    : null;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
