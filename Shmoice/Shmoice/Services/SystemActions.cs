using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;

namespace Shmoice.Services;

/// <summary>Window, app, volume and system actions used by <see cref="ActionExecutor"/>.</summary>
public static class SystemActions
{
    // ---- Volume ---------------------------------------------------------------------

    /// <summary>Sets the default speakers to an exact percent (and unmutes them unless it's 0).</summary>
    public static void SetVolume(string text)
    {
        if (!int.TryParse(text.Trim().TrimEnd('%'), out var percent))
            throw new ArgumentException($"Set volume needs a percent from 0 to 100 (got \"{text}\").");
        percent = Math.Clamp(percent, 0, 100);
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
        device.AudioEndpointVolume.Mute = percent == 0;
    }

    public static void SetSpeakerMute(bool mute)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.Mute = mute;
    }

    /// <summary>Mutes, unmutes, or (null) toggles the default microphones (both "default" and "communications").</summary>
    public static void SetMicMute(bool? mute)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<MMDevice>();
        foreach (var role in new[] { Role.Console, Role.Communications })
        {
            try
            {
                var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                if (devices.Any(x => x.ID == d.ID)) d.Dispose(); else devices.Add(d);
            }
            catch (COMException) { } // no microphone for this role
        }
        if (devices.Count == 0) throw new InvalidOperationException("No microphone found.");
        var target = mute ?? !devices[0].AudioEndpointVolume.Mute;
        foreach (var d in devices)
        {
            using (d) d.AudioEndpointVolume.Mute = target;
        }
    }

    // ---- Mouse ------------------------------------------------------------------------

    /// <summary>"960, 540" -> moves the pointer there (screen pixels).</summary>
    public static void MoveMouseTo(string text)
    {
        var parts = text.Split([',', ' ', 'x', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
            throw new ArgumentException($"Move mouse needs a position like \"960, 540\" (got \"{text}\").");
        SetCursorPos(x, y);
    }

    // ---- Clipboard & speech -----------------------------------------------------------

    public static void CopyText(string text)
    {
        // The clipboard needs the UI thread, and can be briefly locked by another app.
        Application.Current.Dispatcher.Invoke(() =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try { Clipboard.SetText(text); return; }
                catch (COMException) when (attempt < 5) { Thread.Sleep(50); }
            }
        });
    }

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Say something needs some text to say.");
        using var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
        synth.Speak(text);
    }

    // ---- Apps & windows ---------------------------------------------------------------

    /// <summary>
    /// Brings the first open window whose app name or title contains <paramref name="name"/> to the front.
    /// <paramref name="tapAlt"/> sends an Alt tap first: Windows only lets a background app change the
    /// foreground window right after keyboard input.
    /// </summary>
    public static void SwitchTo(string name, Action tapAlt)
    {
        var hwnd = FindWindow(name)
            ?? throw new InvalidOperationException($"No open window matches \"{name.Trim()}\". Is the app running?");
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        tapAlt();
        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Closes the app like clicking its X (it can still ask to save), or with <paramref name="force"/> ends it
    /// immediately. Matches by app name; only a polite close falls back to matching window titles.
    /// </summary>
    public static void CloseApp(string name, bool force)
    {
        var matches = Matching(name, includeTitles: !force);
        if (matches.Count == 0) throw new InvalidOperationException($"No open app matches \"{name.Trim()}\".");
        foreach (var p in matches)
        {
            using (p)
            {
                if (force) p.Kill(entireProcessTree: false);
                else p.CloseMainWindow();
            }
        }
    }

    public static void CloseForegroundWindow() => PostMessage(Foreground(), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void MinimizeForegroundWindow() => ShowWindow(Foreground(), SW_MINIMIZE);

    public static void ToggleMaximizeForegroundWindow()
    {
        var hwnd = Foreground();
        ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
    }

    private static IntPtr Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No window is active.");
        return hwnd;
    }

    private static IntPtr? FindWindow(string name)
    {
        var matches = Matching(name, includeTitles: true);
        var hwnd = matches.Count > 0 ? matches[0].MainWindowHandle : (IntPtr?)null;
        foreach (var p in matches) p.Dispose();
        return hwnd;
    }

    /// <summary>
    /// Running apps with a window whose process name matches (exact matches first). If none match by name
    /// and <paramref name="includeTitles"/> is set, apps whose window title contains the text instead.
    /// Never matches Shmoice itself.
    /// </summary>
    private static List<Process> Matching(string name, bool includeTitles)
    {
        var query = name.Trim();
        if (query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) query = query[..^4];
        if (query.Length == 0) throw new ArgumentException("Enter an app name, e.g. chrome.");

        var withWindows = Process.GetProcesses()
            .Where(p => p.Id != Environment.ProcessId && SafeHandle(p) != IntPtr.Zero).ToList();
        var result = withWindows.Where(p => p.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.ProcessName.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (result.Count == 0 && includeTitles)
            result = withWindows.Where(p => SafeTitle(p).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in withWindows.Except(result)) p.Dispose();
        return result;
    }

    private static IntPtr SafeHandle(Process p) { try { return p.MainWindowHandle; } catch { return IntPtr.Zero; } }
    private static string SafeTitle(Process p) { try { return p.MainWindowTitle; } catch { return ""; } }

    // ---- System -----------------------------------------------------------------------

    public static void Lock()
    {
        if (!LockWorkStation()) throw new InvalidOperationException("Windows refused to lock.");
    }

    public static void Sleep()
    {
        if (!SetSuspendState(false, false, false)) throw new InvalidOperationException("Windows refused to sleep.");
    }

    /// <summary>Turns the monitors off; any mouse or key input wakes them.</summary>
    public static void DisplayOff() => PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);

    /// <summary>Runs Windows' shutdown.exe, e.g. "/s /t 30" (shut down in 30 s) or "/a" (cancel).</summary>
    public static void Shutdown(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(5000);
        if (p.HasExited && p.ExitCode != 0 && args == "/a")
            throw new InvalidOperationException("There's no shut down or restart to cancel.");
    }

    /// <summary>Flips Windows (and apps) between light and dark mode.</summary>
    public static void ToggleDarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true)
            ?? throw new InvalidOperationException("Couldn't find the Windows theme settings.");
        var light = (key.GetValue("AppsUseLightTheme") as int? ?? 1) == 1;
        var value = light ? 0 : 1;
        key.SetValue("AppsUseLightTheme", value, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", value, Microsoft.Win32.RegistryValueKind.DWord);
        // Tell open windows the theme changed so they repaint.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>Runs a command line through cmd.exe with no window.</summary>
    public static void RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Enter a command to run.");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
    }

    /// <summary>Shows a small pop-up in the bottom-right corner for a few seconds.</summary>
    public static void ShowMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a message to show.");
        Application.Current.Dispatcher.BeginInvoke(() => Shmoice.MessagePopup.Show(text.Trim()));
    }

    /// <summary>Saves the whole screen (all monitors) to Pictures\Screenshots as a PNG.</summary>
    public static void Screenshot()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Shmoice {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(file);
            encoder.Save(stream);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---- Win32 -------------------------------------------------------------------------

    private const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private static readonly IntPtr HWND_BROADCAST = 0xFFFF, SC_MONITORPOWER = 0xF170;
    private const uint WM_SYSCOMMAND = 0x0112, WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
