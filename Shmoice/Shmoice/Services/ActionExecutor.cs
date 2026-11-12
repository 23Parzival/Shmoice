using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>Turns ActionSteps into real input / OS actions using the Win32 SendInput API.</summary>
public static class ActionExecutor
{
    /// <summary>
    /// Set by the app: waits for the user to say something (given how long to wait for them to start)
    /// and returns the words, or "" if nothing was heard. Used by "Listen for words" steps.
    /// </summary>
    public static Func<TimeSpan, Task<string>>? ListenForWords { get; set; }

    /// <summary>Set by the app: finds one of the user's commands by its phrase, for "Run another command" steps.</summary>
    public static Func<string, VoiceCommand?>? FindCommand { get; set; }

    /// <summary>Set by the app: the Anthropic API key ("" if none), for "Ask Claude" steps.</summary>
    public static Func<string>? GetApiKey { get; set; }

    /// <summary>Set by the app: writes a line to the activity log. May be called from a background thread.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>What the steps of one run share: the values behind {heard} and {result}.</summary>
    private sealed class RunContext
    {
        public string Heard = "";  // what the last "Listen for words" step heard
        public string Result = ""; // output of the last web request, script, Ask Claude or Pick value step
        public int Depth;          // how many "Run another command" steps deep we are
    }

    /// <summary>Thrown by "Continue only if…" to end the command early. Not a failure.</summary>
    private sealed class StopCommandException : Exception;

    /// <summary>Runs every step in order. Returns what a "Listen for words" step heard ("" if none).</summary>
    public static Task<string> RunAsync(VoiceCommand command) => Task.Run(async () =>
    {
        var context = new RunContext();
        try { await RunStepsAsync(command, context); }
        catch (StopCommandException) { Log?.Invoke($"\"{command.Phrase}\" stopped early: its condition wasn't met"); }
        return context.Heard;
    });

    private static async Task RunStepsAsync(VoiceCommand command, RunContext context)
    {
        foreach (var step in command.Steps.ToList())
        {
            if (step.Type == ActionType.ListenForWords)
            {
                context.Heard = await ListenAsync(step.Parameter);
                continue;
            }
            for (var i = 0; i < step.Repeat; i++)
            {
                if (!await RunAdvancedStepAsync(step, context))
                    await RunStepAsync(step, FillIn(step, context));
                if (step.Repeat > 1) await Task.Delay(30);
            }
        }
    }

    /// <summary>
    /// The steps that read or write the run's context ({result}, other commands) or fill in their own
    /// placeholders. Returns false for every other step, which <see cref="RunStepAsync"/> handles.
    /// </summary>
    private static async Task<bool> RunAdvancedStepAsync(ActionStep step, RunContext context)
    {
        AdvancedActions.Filler fill = (template, encode) => Fill(template, context, encode);
        switch (step.Type)
        {
            case ActionType.HttpRequest:
                context.Result = await AdvancedActions.SendAsync(AdvancedActions.ParseRequest(step.Parameter, fill));
                return true;
            case ActionType.PickJson:
                context.Result = AdvancedActions.PickJson(context.Result, fill(step.Parameter));
                return true;
            case ActionType.RunPowerShell:
                context.Result = await AdvancedActions.RunPowerShellAsync(fill(step.Parameter));
                return true;
            case ActionType.AskAi:
                var question = fill(step.Parameter);
                if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Enter a question to ask.");
                var apiKey = GetApiKey?.Invoke() ?? "";
                if (apiKey.Length == 0) throw new InvalidOperationException("Ask Claude needs your Anthropic API key. Add it in the AI Builder tab.");
                context.Result = await AiCommandBuilder.AskAsync(apiKey, question);
                return true;
            case ActionType.ContinueIf:
                if (!AdvancedActions.Evaluate(step.Parameter, fill)) throw new StopCommandException();
                return true;
            case ActionType.RunVoiceCommand:
                var phrase = fill(step.Parameter).Trim();
                if (phrase.Length == 0) throw new ArgumentException("Enter the phrase of the command to run.");
                var other = FindCommand?.Invoke(phrase)
                    ?? throw new InvalidOperationException($"There's no command called \"{phrase}\".");
                if (context.Depth >= 5) throw new InvalidOperationException("Commands are running each other in a loop.");
                context.Depth++;
                try { await RunStepsAsync(other, context); }
                finally { context.Depth--; }
                return true;
            default:
                return false;
        }
    }

    private static async Task<string> ListenAsync(string secondsText)
    {
        if (ListenForWords == null) throw new InvalidOperationException("Listening isn't available.");
        var seconds = secondsText.Trim().Length == 0 ? 8 : Math.Clamp(ParseSeconds(secondsText), 1, 60);
        var words = await ListenForWords(TimeSpan.FromSeconds(seconds));
        // Stop here rather than e.g. searching for nothing.
        if (words.Length == 0) throw new InvalidOperationException("Didn't hear anything, so the rest of the command was skipped.");
        return words;
    }

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(
        @"\{(heard|result|clipboard|date|time|app)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Fills in the step's parameter (values are URL-encoded inside an Open website link).</summary>
    private static string FillIn(ActionStep step, RunContext context)
    {
        var p = step.Parameter;
        // Inside a link (e.g. "youtube.com/results?search_query={heard}") the words must be encoded;
        // a parameter that is nothing but a placeholder is a whole link, so it's left as is.
        var insideLink = step.Type == ActionType.OpenUrl && !Placeholder.Match(p.Trim()).Value.Equals(p.Trim(), StringComparison.Ordinal);
        return Fill(p, context, insideLink ? Uri.EscapeDataString : null);
    }

    /// <summary>
    /// Replaces the placeholders a parameter can use: {heard} (Listen for words), {result} (last web
    /// request / script / Ask Claude), {clipboard}, {date} (2026-01-31), {time} (18:05) and {app}
    /// (the app in front, e.g. chrome). Done in one pass, so a value is never itself filled in again.
    /// </summary>
    private static string Fill(string template, RunContext context, Func<string, string>? encode = null)
    {
        if (!template.Contains('{')) return template;
        return Placeholder.Replace(template, match =>
        {
            var value = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "heard" => context.Heard,
                "result" => context.Result,
                "clipboard" => SystemActions.GetClipboardText(),
                "date" => DateTime.Now.ToString("yyyy-MM-dd"),
                "time" => DateTime.Now.ToString("HH:mm"),
                _ => ForegroundApp.ProcessName() ?? "",
            };
            return encode != null ? encode(value) : value;
        });
    }

    private static async Task RunStepAsync(ActionStep step, string p)
    {
        if (step.Type == ActionType.None) return; // no action chosen yet

        // Many actions are just a fixed keyboard shortcut.
        if (ActionCatalog.Get(step.Type).Combo is { } combo)
        {
            SendCombo(combo);
            return;
        }

        switch (step.Type)
        {
            case ActionType.MediaPlayPause: Tap(VK_MEDIA_PLAY_PAUSE); break;
            // Fall back to the toggle key only if Windows' media sessions can't be used.
            case ActionType.MediaPlay: if (!await MediaControl.PlayAsync()) Tap(VK_MEDIA_PLAY_PAUSE); break;
            case ActionType.MediaPause: if (!await MediaControl.PauseAsync()) Tap(VK_MEDIA_PLAY_PAUSE); break;
            case ActionType.MediaNext: Tap(VK_MEDIA_NEXT_TRACK); break;
            case ActionType.MediaPrevious: Tap(VK_MEDIA_PREV_TRACK); break;
            case ActionType.MediaStop: Tap(VK_MEDIA_STOP); break;
            case ActionType.VolumeUp: Tap(VK_VOLUME_UP); break;
            case ActionType.VolumeDown: Tap(VK_VOLUME_DOWN); break;
            case ActionType.VolumeMute: Tap(VK_VOLUME_MUTE); break;
            case ActionType.Mute: SystemActions.SetSpeakerMute(true); break;
            case ActionType.Unmute: SystemActions.SetSpeakerMute(false); break;
            case ActionType.SetVolume: SystemActions.SetVolume(p); break;
            case ActionType.MicMuteToggle: SystemActions.SetMicMute(null); break;
            case ActionType.MicMute: SystemActions.SetMicMute(true); break;
            case ActionType.MicUnmute: SystemActions.SetMicMute(false); break;
            case ActionType.KeyCombo: SendCombo(p); break;
            case ActionType.HoldKeys: Send(ParseCombo(p).Select(k => KeyInput(k, Extended(k))).ToArray()); break;
            case ActionType.ReleaseKeys: Send(Enumerable.Reverse(ParseCombo(p)).Select(k => KeyInput(k, Extended(k) | KEYEVENTF_KEYUP)).ToArray()); break;
            case ActionType.TypeText: TypeText(p); break;
            case ActionType.PasteText:
                if (p.Length == 0) throw new ArgumentException("There's no text to paste.");
                SystemActions.CopyText(p);
                await Task.Delay(80); // give the clipboard a moment before the app reads it
                SendCombo("ctrl+v");
                break;
            case ActionType.AppendToFile: AdvancedActions.AppendToFile(p); break;
            case ActionType.CopyText: SystemActions.CopyText(p); break;
            case ActionType.MouseClick: Click(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP); break;
            case ActionType.MouseRightClick: Click(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP); break;
            case ActionType.MouseMiddleClick: Click(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP); break;
            case ActionType.MouseDoubleClick:
                Click(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                await Task.Delay(40);
                Click(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                break;
            case ActionType.ScrollUp: Send([MouseInput(MOUSEEVENTF_WHEEL, WHEEL_DELTA)]); break;
            case ActionType.ScrollDown: Send([MouseInput(MOUSEEVENTF_WHEEL, -WHEEL_DELTA)]); break;
            case ActionType.ScrollLeft: Send([MouseInput(MOUSEEVENTF_HWHEEL, -WHEEL_DELTA)]); break;
            case ActionType.ScrollRight: Send([MouseInput(MOUSEEVENTF_HWHEEL, WHEEL_DELTA)]); break;
            case ActionType.MoveMouseTo: SystemActions.MoveMouseTo(p); break;
            case ActionType.OpenUrl: OpenUrl(p); break;
            case ActionType.SearchWeb: Search("https://www.google.com/search?q=", p); break;
            case ActionType.SearchYouTube: Search("https://www.youtube.com/results?search_query=", p); break;
            case ActionType.Open: Open(p); break;
            case ActionType.SwitchToApp: SystemActions.SwitchTo(p, () => Send([KeyInput(0x12, 0), KeyInput(0x12, KEYEVENTF_KEYUP)])); break;
            case ActionType.CloseApp: SystemActions.CloseApp(p, force: false); break;
            case ActionType.ForceQuitApp: SystemActions.CloseApp(p, force: true); break;
            case ActionType.CloseWindow: SystemActions.CloseForegroundWindow(); break;
            case ActionType.MinimizeWindow: SystemActions.MinimizeForegroundWindow(); break;
            case ActionType.MaximizeWindow: SystemActions.ToggleMaximizeForegroundWindow(); break;
            case ActionType.Screenshot: SystemActions.Screenshot(); break;
            case ActionType.ToggleDarkMode: SystemActions.ToggleDarkMode(); break;
            case ActionType.LockPC: SystemActions.Lock(); break;
            case ActionType.Sleep: SystemActions.Sleep(); break;
            case ActionType.DisplayOff: SystemActions.DisplayOff(); break;
            case ActionType.ShutDown: SystemActions.Shutdown("/s /t 30"); break;
            case ActionType.Restart: SystemActions.Shutdown("/r /t 30"); break;
            case ActionType.CancelShutdown: SystemActions.Shutdown("/a"); break;
            case ActionType.Speak: SystemActions.Speak(p); break;
            case ActionType.SayTime: SystemActions.Speak($"It's {DateTime.Now:h:mm tt}"); break;
            case ActionType.ShowMessage: SystemActions.ShowMessage(p); break;
            case ActionType.RunCommand: SystemActions.RunCommand(p); break;
            case ActionType.Wait:
                await Task.Delay(TimeSpan.FromSeconds(ParseSeconds(p)));
                break;
        }
    }

    /// <summary>Wait parameter in seconds; accepts "2", "1.5" or "1,5". Capped at 10 minutes.</summary>
    public static double ParseSeconds(string text)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (normalized.Length == 0) return 0; // empty = no wait (matches old behaviour)
        if (!double.TryParse(normalized, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) || double.IsNaN(seconds))
            throw new ArgumentException($"Wait needs a number of seconds, e.g. 1.5 (got \"{text}\").");
        return Math.Clamp(seconds, 0, 600);
    }

    /// <summary>Opens a web link in the default browser; "twitch.tv/x" becomes "https://twitch.tv/x".</summary>
    public static string NormalizeUrl(string text)
    {
        var url = text.Trim();
        if (url.Length == 0) throw new ArgumentException("Enter a link to open, e.g. youtube.com.");
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            throw new ArgumentException($"\"{text.Trim()}\" isn't a web link. Use something like youtube.com or https://example.com.");
        return uri.AbsoluteUri;
    }

    private static void OpenUrl(string text) =>
        Process.Start(new ProcessStartInfo { FileName = NormalizeUrl(text), UseShellExecute = true });

    private static void Search(string baseUrl, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter what to search for.");
        Process.Start(new ProcessStartInfo { FileName = baseUrl + Uri.EscapeDataString(query.Trim()), UseShellExecute = true });
    }

    private static void Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Open needs a program, file, or URL.");
        var parts = target.Split('|', 2);
        Process.Start(new ProcessStartInfo
        {
            FileName = parts[0].Trim().Trim('"'),
            Arguments = parts.Length > 1 ? parts[1].Trim() : "",
            UseShellExecute = true, // lets Windows resolve URLs, documents, and apps on PATH
        });
    }

    // ---- Key combos -------------------------------------------------------------------

    private static readonly Dictionary<string, ushort> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["del"] = 0x2E, ["backspace"] = 0x08,
        ["pgup"] = 0x21, ["pgdn"] = 0x22, ["space"] = 0x20, ["tab"] = 0x09,
    };

    public static ushort[] ParseCombo(string combo)
    {
        var keys = new List<ushort>();
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Aliases.TryGetValue(raw, out var vk)) { keys.Add(vk); continue; }
            // Raw virtual-key code, e.g. "0xC1" (the key recorder writes these for keys WPF has no name for).
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                keys.Add(code);
                continue;
            }
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw; // "1" -> Key.D1
            if (Enum.TryParse<Key>(name, ignoreCase: true, out var key) && key != Key.None)
            {
                keys.Add((ushort)KeyInterop.VirtualKeyFromKey(key));
                continue;
            }
            throw new ArgumentException($"Unknown key \"{raw}\" in \"{combo}\".");
        }
        if (keys.Count == 0) throw new ArgumentException("Key combo is empty.");
        return keys.ToArray();
    }

    private static void SendCombo(string combo)
    {
        var keys = ParseCombo(combo);
        var inputs = keys.Select(k => KeyInput(k, Extended(k)))                 // press in order
            .Concat(Enumerable.Reverse(keys).Select(k => KeyInput(k, Extended(k) | KEYEVENTF_KEYUP))) // release in reverse
            .ToArray();
        Send(inputs);
    }

    /// <summary>
    /// Arrows, Home/End, PgUp/PgDn, Insert/Delete and the Windows keys are "extended" keys. Without the
    /// flag Windows treats e.g. Left as the number-pad 4, which breaks shortcuts like Win+Left.
    /// </summary>
    private static uint Extended(ushort vk) =>
        vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5
            ? KEYEVENTF_EXTENDEDKEY : 0;

    private static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var c in text)
        {
            inputs.Add(UnicodeInput(c, 0));
            inputs.Add(UnicodeInput(c, KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    private static void Tap(ushort vk) =>
        Send([KeyInput(vk, KEYEVENTF_EXTENDEDKEY), KeyInput(vk, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP)]);

    private static void Click(uint down, uint up) => Send([MouseInput(down, 0), MouseInput(up, 0)]);

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput failed (error {Marshal.GetLastWin32Error()}). " +
                "Windows blocks input to apps running as administrator unless Shmoice is also elevated.");
    }

    // ---- Win32 interop ----------------------------------------------------------------

    private const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const int WHEEL_DELTA = 120;

    private static INPUT MouseInput(uint flags, int wheel) =>
        new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)wheel) } } };

    private static INPUT KeyInput(ushort vk, uint flags) =>
        new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    private static INPUT UnicodeInput(char c, uint flags) =>
        new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | flags } } };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    // The union must include MOUSEINPUT (the largest member) so the struct size matches Win32.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
}
