using System.IO;
using Microsoft.Win32;

namespace Shmoice.Services;

/// <summary>
/// "Start with Windows". Uses the per-user Run key. The installer's optional checkbox creates a
/// shortcut in the Startup folder instead, so both are checked and both are removed when turned off.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Shmoice";

    private static string StartupShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Shmoice.lnk");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null || File.Exists(StartupShortcut);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find Shmoice.exe.");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut);
        }
    }
}
