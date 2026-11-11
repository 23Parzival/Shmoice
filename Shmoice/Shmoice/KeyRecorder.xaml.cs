using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shmoice;

/// <summary>
/// Keybind-style recorder for "Press keys" steps: click it, press a key or combo (e.g. Alt+Z),
/// and it stores the combo in the same "alt+z" text format the executor understands.
/// While recording it uses a low-level keyboard hook so system shortcuts like Win+D or Alt+Tab
/// are captured instead of being acted on. The hook only exists while recording.
/// </summary>
public partial class KeyRecorder : UserControl
{
    public static readonly DependencyProperty ComboProperty = DependencyProperty.Register(
        nameof(Combo), typeof(string), typeof(KeyRecorder),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((KeyRecorder)d).UpdateLabel()));

    /// <summary>The recorded combo, e.g. "ctrl+shift+esc". Empty = nothing recorded.</summary>
    public string Combo
    {
        get => (string)GetValue(ComboProperty);
        set => SetValue(ComboProperty, value);
    }

    // Only one recorder can be recording at a time.
    private static KeyRecorder? _active;

    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc; // kept alive so the GC doesn't collect the callback
    private Window? _window;

    private readonly List<int> _modifiersPressed = new(); // in the order pressed, e.g. [ctrl, shift]
    private readonly HashSet<int> _held = new();          // keys currently down (raw VKs)
    private bool _done;                                   // combo finished; waiting for keys to be released

    public KeyRecorder()
    {
        InitializeComponent();
        UpdateLabel();
        Unloaded += (_, _) => StopRecording();
    }

    private bool IsRecording => _hook != IntPtr.Zero;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecording) { StopRecording(); return; }
        StartRecording();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        Combo = "";
    }

    private void StartRecording()
    {
        _active?.StopRecording();
        _modifiersPressed.Clear();
        _held.Clear();
        _done = false;

        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _proc = null; return; }
        _active = this;

        // Cancel if the user clicks somewhere else or switches away from the app.
        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.PreviewMouseDown += Window_PreviewMouseDown;
            _window.Deactivated += Window_Deactivated;
        }
        UpdateLabel();
    }

    private void StopRecording()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        if (_active == this) _active = null;
        if (_window != null)
        {
            _window.PreviewMouseDown -= Window_PreviewMouseDown;
            _window.Deactivated -= Window_Deactivated;
            _window = null;
        }
        UpdateLabel();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the record button itself are handled by Record_Click.
        if (e.OriginalSource is DependencyObject d && IsWithin(d, RecordButton)) return;
        StopRecording();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => StopRecording();

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? n = node; n != null; n = n is Visual ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (n == ancestor) return true;
        return false;
    }

    // ---- Recording logic (runs on the UI thread via the hook) ---------------------------

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
        var msg = wParam.ToInt32();
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

        if (down) OnKeyDown(vk);
        else OnKeyUp(vk);

        return (IntPtr)1; // swallow every key while recording so nothing else reacts to it
    }

    private void OnKeyDown(int vk)
    {
        var isRepeat = !_held.Add(vk);
        if (_done || isRepeat) return;

        if (ModifierName(vk) != null)
        {
            if (!_modifiersPressed.Contains(NormalizeModifier(vk))) _modifiersPressed.Add(NormalizeModifier(vk));
            UpdateLabel();
            return;
        }

        // Esc on its own cancels; with modifiers (e.g. Ctrl+Shift+Esc) it's a real combo.
        if (vk == VK_ESCAPE && _modifiersPressed.Count == 0)
        {
            Finish(null);
            return;
        }

        var parts = _modifiersPressed.Select(m => ModifierName(m)!).ToList();
        parts.Add(KeyName(vk));
        Finish(string.Join("+", parts));
    }

    private void OnKeyUp(int vk)
    {
        _held.Remove(vk);
        // Only modifiers pressed and all released (e.g. just "Win"): record them on their own.
        if (!_done && ModifierName(vk) != null && _modifiersPressed.Count > 0 && !_held.Any())
        {
            Finish(string.Join("+", _modifiersPressed.Select(m => ModifierName(m)!)));
            return;
        }
        // Keep swallowing until every key is up, so e.g. releasing Win doesn't open Start.
        if (_done && _held.Count == 0) StopRecording();
    }

    /// <summary>Saves the combo (null = cancelled) and waits for the remaining keys to be released.</summary>
    private void Finish(string? combo)
    {
        _done = true;
        if (combo != null) Combo = combo;
        if (_held.Count == 0) StopRecording();
        else UpdateLabel();
    }

    // ---- Names ---------------------------------------------------------------------------

    private static int NormalizeModifier(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_LMENU or VK_RMENU => VK_MENU,
        VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
        VK_RWIN => VK_LWIN,
        _ => vk,
    };

    private static string? ModifierName(int vk) => NormalizeModifier(vk) switch
    {
        VK_CONTROL => "ctrl",
        VK_MENU => "alt",
        VK_SHIFT => "shift",
        VK_LWIN => "win",
        _ => null,
    };

    /// <summary>Name in the format ActionExecutor.ParseCombo reads back (aliases or WPF Key names).</summary>
    private static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.Escape => "esc",
            Key.Enter => "enter",
            Key.Back => "backspace",
            Key.Delete => "del",
            Key.Space => "space",
            Key.Tab => "tab",
            Key.PageUp => "pgup",
            Key.PageDown => "pgdn",
            Key.CapsLock => "capslock",
            Key.PrintScreen => "printscreen",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.None => "0x" + vk.ToString("X2", CultureInfo.InvariantCulture),
            _ => key.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>"ctrl+shift+esc" -> "Ctrl + Shift + Esc" for display.</summary>
    private static string Pretty(string combo) => string.Join("  +  ",
        combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));

    private void UpdateLabel()
    {
        if (LabelText == null) return;
        ClearButton.Visibility = string.IsNullOrEmpty(Combo) || IsRecording ? Visibility.Collapsed : Visibility.Visible;

        if (IsRecording && !_done)
        {
            LabelText.Text = _modifiersPressed.Count > 0
                ? Pretty(string.Join("+", _modifiersPressed.Select(m => ModifierName(m)!))) + "  +  …"
                : "Press a key or combo…  (Esc to cancel)";
            LabelText.Foreground = (Brush)FindResource("AppAccentBrush");
            LabelText.FontStyle = FontStyles.Italic;
        }
        else if (string.IsNullOrWhiteSpace(Combo))
        {
            LabelText.Text = "Click to record keys";
            LabelText.Foreground = (Brush)FindResource("MutedTextBrush");
            LabelText.FontStyle = FontStyles.Italic;
        }
        else
        {
            LabelText.Text = Pretty(Combo);
            LabelText.ClearValue(TextBlock.ForegroundProperty);
            LabelText.FontStyle = FontStyles.Normal;
        }
    }

    // ---- Win32 -------------------------------------------------------------------------

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
