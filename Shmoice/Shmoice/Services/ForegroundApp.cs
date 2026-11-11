using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shmoice.Services;

/// <summary>Which app is in front, for profiles.</summary>
public static class ForegroundApp
{
    /// <summary>Process name of the app you're using right now (e.g. "chrome"), or null if unknown.</summary>
    public static string? ProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Names of apps that currently have a window, for picking a profile's apps.</summary>
    public static List<string> RunningAppNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) names.Add(p.ProcessName);
                }
                catch (Exception) { /* access denied for some system processes */ }
            }
        }
        return names.ToList();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
