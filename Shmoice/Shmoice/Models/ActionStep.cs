namespace Shmoice.Models;

/// <summary>
/// The kinds of things a command can do. Each step is one of these plus an optional parameter.
/// Saved files store these by name, so the order here can change freely. Labels, categories,
/// descriptions and parameter rules live in <see cref="ActionCatalog"/>.
/// </summary>
public enum ActionType
{
    /// <summary>No action picked yet (new steps start like this). Skipped when the command runs.</summary>
    None,

    // Voice input
    /// <summary>Waits for you to say something; later steps use it via {heard}. Parameter: seconds to wait.</summary>
    ListenForWords,

    // Media
    MediaPlay, MediaPause, MediaPlayPause, MediaNext, MediaPrevious, MediaStop,

    // Volume & audio
    VolumeUp, VolumeDown, VolumeMute, Mute, Unmute, SetVolume, MicMuteToggle, MicMute, MicUnmute,

    // Keyboard & text
    KeyCombo, HoldKeys, ReleaseKeys, TypeText, PasteText, CopyText, Copy, Cut, Paste, Undo, Redo, SelectAll, PressEnter, PressEscape,

    // Mouse
    MouseClick, MouseRightClick, MouseDoubleClick, MouseMiddleClick,
    ScrollUp, ScrollDown, ScrollLeft, ScrollRight, MoveMouseTo,

    // Browser
    OpenUrl, SearchWeb, SearchYouTube, NewTab, CloseTab, ReopenTab, NextTab, PreviousTab,
    BrowserBack, BrowserForward, Refresh, ZoomIn, ZoomOut, ZoomReset,

    // Apps
    Open, SwitchToApp, CloseApp, ForceQuitApp,

    // Window controls
    CloseWindow, MinimizeWindow, MaximizeWindow, SnapLeft, SnapRight, MoveToNextMonitor, SwitchWindow, TaskView, ShowDesktop,

    // Virtual desktops
    NewDesktop, NextDesktop, PreviousDesktop,

    // System
    StartMenu, WindowsSearch, OpenSettings, FileExplorer, TaskManager, Notifications, QuickSettings,
    ClipboardHistory, EmojiPanel, SnipArea, Screenshot, ToggleDarkMode,

    // Power
    LockPC, Sleep, DisplayOff, ShutDown, Restart, CancelShutdown,

    // Other
    Speak, SayTime, ShowMessage, RunCommand, Wait,

    // Advanced: these produce or use {result}
    /// <summary>Sends an HTTP request; the reply becomes {result}.</summary>
    HttpRequest,
    /// <summary>Replaces {result} (JSON) with one value from it, e.g. "main.temp".</summary>
    PickJson,
    /// <summary>Runs a PowerShell script and waits; its output becomes {result}.</summary>
    RunPowerShell,
    /// <summary>Asks the chosen AI service a question; the answer becomes {result}.</summary>
    AskAi,
    /// <summary>Stops the command (quietly) unless the condition holds, e.g. "{app} is chrome".</summary>
    ContinueIf,
    /// <summary>Runs another command's steps. Parameter: its phrase.</summary>
    RunVoiceCommand,
    /// <summary>Adds a line to a text file. Parameter: "path | text".</summary>
    AppendToFile,
}

/// <summary>What the parameter box accepts for an action.</summary>
public enum ParamKind { None, Text, Integer, Decimal, Keys }

/// <summary>Everything the UI, executor and AI builder need to know about one action.</summary>
/// <param name="Combo">If set, the action is just this keyboard shortcut (e.g. "ctrl+t").</param>
public record ActionInfo(ActionType Type, string Category, string Label, string Description,
    ParamKind Param = ParamKind.None, string? Hint = null, string? Combo = null);

public static class ActionCatalog
{
    /// <summary>Placeholder that later steps use for what a "Listen for words" step heard.</summary>
    public const string HeardPlaceholder = "{heard}";

    /// <summary>Placeholder for the output of the last web request, PowerShell script, Ask AI or Pick-from-result step.</summary>
    public const string ResultPlaceholder = "{result}";

    private const string Voice = "Voice input", Media = "Media", Audio = "Volume & audio", Keys = "Keyboard & text", Mouse = "Mouse",
        Browser = "Browser", Apps = "Apps", Window = "Window controls", Desktops = "Virtual desktops",
        Sys = "System", Power = "Power", Other = "Other", Advanced = "Advanced";

    /// <summary>Every action, in the order shown in the picker.</summary>
    public static IReadOnlyList<ActionInfo> All { get; } =
    [
        new(ActionType.ListenForWords, Voice, "Listen for words",
            "Waits for you to say something (you'll hear a chime). Later steps can use what you said by writing {heard}, e.g. Search the web: {heard}.",
            ParamKind.Decimal, "Seconds to wait for you to start talking (default 8)"),

        new(ActionType.MediaPlay, Media, "Play", "Resumes whatever media was playing. Never pauses."),
        new(ActionType.MediaPause, Media, "Pause", "Pauses whatever media is playing. Never resumes."),
        new(ActionType.MediaPlayPause, Media, "Play / Pause (toggle)", "Presses the play/pause media key."),
        new(ActionType.MediaNext, Media, "Next track", "Skips to the next track."),
        new(ActionType.MediaPrevious, Media, "Previous track", "Goes back to the previous track."),
        new(ActionType.MediaStop, Media, "Stop media", "Presses the stop media key."),

        new(ActionType.VolumeUp, Audio, "Volume up", "Raises the volume by 2%. Use Repeat for bigger steps."),
        new(ActionType.VolumeDown, Audio, "Volume down", "Lowers the volume by 2%. Use Repeat for bigger steps."),
        new(ActionType.VolumeMute, Audio, "Mute / Unmute (toggle)", "Toggles the speaker mute."),
        new(ActionType.Mute, Audio, "Mute", "Mutes the speakers. Never unmutes."),
        new(ActionType.Unmute, Audio, "Unmute", "Unmutes the speakers. Never mutes."),
        new(ActionType.SetVolume, Audio, "Set volume to…", "Sets the volume to an exact percent, e.g. 30.",
            ParamKind.Integer, "Percent, e.g. 30"),
        new(ActionType.MicMuteToggle, Audio, "Mute / Unmute mic (toggle)", "Toggles mute on your default microphone. Shmoice can't hear you while it's muted."),
        new(ActionType.MicMute, Audio, "Mute mic", "Mutes your default microphone. Shmoice can't hear you while it's muted."),
        new(ActionType.MicUnmute, Audio, "Unmute mic", "Unmutes your default microphone."),

        new(ActionType.KeyCombo, Keys, "Press keys", "Presses a key or shortcut, like Alt+Z.", ParamKind.Keys, "Click to record keys"),
        new(ActionType.HoldKeys, Keys, "Hold keys down", "Presses keys and keeps them held (e.g. W to keep walking in a game) until a Release keys step, in this or another command.",
            ParamKind.Keys, "Click to record keys"),
        new(ActionType.ReleaseKeys, Keys, "Release keys", "Lets go of keys held by Hold keys down.", ParamKind.Keys, "Click to record keys"),
        new(ActionType.TypeText, Keys, "Type text", "Types the text into the focused window.", ParamKind.Text, "Text to type"),
        new(ActionType.PasteText, Keys, "Paste text", "Pastes the text into the focused window in one go (faster than typing for long text). Replaces what's on the clipboard.",
            ParamKind.Text, "Text to paste, e.g. {result}"),
        new(ActionType.CopyText, Keys, "Copy text to clipboard", "Puts the text on the clipboard (doesn't paste it).", ParamKind.Text, "Text to copy"),
        new(ActionType.Copy, Keys, "Copy", "Copies the selection (Ctrl+C).", Combo: "ctrl+c"),
        new(ActionType.Cut, Keys, "Cut", "Cuts the selection (Ctrl+X).", Combo: "ctrl+x"),
        new(ActionType.Paste, Keys, "Paste", "Pastes (Ctrl+V).", Combo: "ctrl+v"),
        new(ActionType.Undo, Keys, "Undo", "Undoes (Ctrl+Z).", Combo: "ctrl+z"),
        new(ActionType.Redo, Keys, "Redo", "Redoes (Ctrl+Y).", Combo: "ctrl+y"),
        new(ActionType.SelectAll, Keys, "Select all", "Selects everything (Ctrl+A).", Combo: "ctrl+a"),
        new(ActionType.PressEnter, Keys, "Press Enter", "Presses Enter.", Combo: "enter"),
        new(ActionType.PressEscape, Keys, "Press Escape", "Presses Esc.", Combo: "esc"),

        new(ActionType.MouseClick, Mouse, "Left click", "Left-clicks wherever the pointer is."),
        new(ActionType.MouseRightClick, Mouse, "Right click", "Right-clicks wherever the pointer is."),
        new(ActionType.MouseDoubleClick, Mouse, "Double click", "Double-clicks wherever the pointer is."),
        new(ActionType.MouseMiddleClick, Mouse, "Middle click", "Middle-clicks wherever the pointer is."),
        new(ActionType.ScrollUp, Mouse, "Scroll up", "Scrolls up one notch. Use Repeat for more."),
        new(ActionType.ScrollDown, Mouse, "Scroll down", "Scrolls down one notch. Use Repeat for more."),
        new(ActionType.ScrollLeft, Mouse, "Scroll left", "Scrolls left one notch. Use Repeat for more."),
        new(ActionType.ScrollRight, Mouse, "Scroll right", "Scrolls right one notch. Use Repeat for more."),
        new(ActionType.MoveMouseTo, Mouse, "Move mouse to…", "Moves the pointer to a screen position in pixels, from the top-left of the main screen.",
            ParamKind.Text, "x, y   e.g. 960, 540"),

        new(ActionType.OpenUrl, Browser, "Open website", "Opens a link in your default browser (in a new tab if it's already open).",
            ParamKind.Text, "Link, e.g. youtube.com or https://example.com/page"),
        new(ActionType.SearchWeb, Browser, "Search the web", "Opens a Google search for the text in your browser.", ParamKind.Text, "What to search for"),
        new(ActionType.SearchYouTube, Browser, "Search YouTube", "Opens a YouTube search for the text.", ParamKind.Text, "What to search for"),
        new(ActionType.NewTab, Browser, "New tab", "Opens a new tab (Ctrl+T).", Combo: "ctrl+t"),
        new(ActionType.CloseTab, Browser, "Close tab", "Closes the current tab (Ctrl+W).", Combo: "ctrl+w"),
        new(ActionType.ReopenTab, Browser, "Reopen closed tab", "Brings back the last closed tab (Ctrl+Shift+T).", Combo: "ctrl+shift+t"),
        new(ActionType.NextTab, Browser, "Next tab", "Switches to the next tab (Ctrl+Tab).", Combo: "ctrl+tab"),
        new(ActionType.PreviousTab, Browser, "Previous tab", "Switches to the previous tab (Ctrl+Shift+Tab).", Combo: "ctrl+shift+tab"),
        new(ActionType.BrowserBack, Browser, "Back", "Goes back a page (Alt+Left).", Combo: "alt+left"),
        new(ActionType.BrowserForward, Browser, "Forward", "Goes forward a page (Alt+Right).", Combo: "alt+right"),
        new(ActionType.Refresh, Browser, "Refresh", "Reloads the page (F5).", Combo: "f5"),
        new(ActionType.ZoomIn, Browser, "Zoom in", "Zooms in (Ctrl+=).", Combo: "ctrl+oemplus"),
        new(ActionType.ZoomOut, Browser, "Zoom out", "Zooms out (Ctrl+-).", Combo: "ctrl+oemminus"),
        new(ActionType.ZoomReset, Browser, "Reset zoom", "Resets zoom to 100% (Ctrl+0).", Combo: "ctrl+0"),

        new(ActionType.Open, Apps, "Open app / file / URL", "Opens a program, file, folder or website. Put extra options after a |.",
            ParamKind.Text, "e.g. https://youtube.com, notepad.exe, spotify:"),
        new(ActionType.SwitchToApp, Apps, "Switch to app", "Brings an open app to the front, even if it's minimized.",
            ParamKind.Text, "App or window name, e.g. chrome, spotify"),
        new(ActionType.CloseApp, Apps, "Close app", "Closes an app like clicking its X, so it can still ask to save.",
            ParamKind.Text, "App name, e.g. notepad, spotify"),
        new(ActionType.ForceQuitApp, Apps, "Force quit app", "Ends an app immediately, like Task Manager's End task. Unsaved work is lost.",
            ParamKind.Text, "App name, e.g. notepad"),
        new(ActionType.CloseWindow, Window, "Close current window", "Closes the window you're using."),
        new(ActionType.MinimizeWindow, Window, "Minimize current window", "Minimizes the window you're using."),
        new(ActionType.MaximizeWindow, Window, "Maximize / restore window", "Maximizes the window you're using, or restores it if it's already maximized."),
        new(ActionType.SnapLeft, Window, "Snap window left", "Snaps the current window to the left half (Win+Left).", Combo: "win+left"),
        new(ActionType.SnapRight, Window, "Snap window right", "Snaps the current window to the right half (Win+Right).", Combo: "win+right"),
        new(ActionType.MoveToNextMonitor, Window, "Move window to other monitor", "Moves the current window to the next monitor (Win+Shift+Right).", Combo: "win+shift+right"),
        new(ActionType.SwitchWindow, Window, "Switch to last window", "Goes back to the window you used before (Alt+Tab).", Combo: "alt+tab"),
        new(ActionType.TaskView, Window, "Task view", "Shows all open windows (Win+Tab).", Combo: "win+tab"),
        new(ActionType.ShowDesktop, Window, "Show desktop", "Minimizes everything (Win+D). Run again to bring it back.", Combo: "win+d"),

        new(ActionType.NewDesktop, Desktops, "New virtual desktop", "Creates a new virtual desktop (Win+Ctrl+D).", Combo: "win+ctrl+d"),
        new(ActionType.NextDesktop, Desktops, "Next virtual desktop", "Switches to the next virtual desktop (Win+Ctrl+Right).", Combo: "win+ctrl+right"),
        new(ActionType.PreviousDesktop, Desktops, "Previous virtual desktop", "Switches to the previous virtual desktop (Win+Ctrl+Left).", Combo: "win+ctrl+left"),

        new(ActionType.StartMenu, Sys, "Open Start menu", "Opens the Start menu.", Combo: "win"),
        new(ActionType.WindowsSearch, Sys, "Open search", "Opens Windows search (Win+S).", Combo: "win+s"),
        new(ActionType.OpenSettings, Sys, "Open Settings", "Opens Windows Settings (Win+I).", Combo: "win+i"),
        new(ActionType.FileExplorer, Sys, "Open File Explorer", "Opens File Explorer (Win+E).", Combo: "win+e"),
        new(ActionType.TaskManager, Sys, "Open Task Manager", "Opens Task Manager (Ctrl+Shift+Esc).", Combo: "ctrl+shift+esc"),
        new(ActionType.Notifications, Sys, "Open notifications", "Opens the notification center (Win+N).", Combo: "win+n"),
        new(ActionType.QuickSettings, Sys, "Open quick settings", "Opens quick settings: Wi-Fi, Bluetooth and more (Win+A).", Combo: "win+a"),
        new(ActionType.ClipboardHistory, Sys, "Clipboard history", "Shows clipboard history (Win+V).", Combo: "win+v"),
        new(ActionType.EmojiPanel, Sys, "Emoji panel", "Opens the emoji picker (Win+.).", Combo: "win+oemperiod"),
        new(ActionType.SnipArea, Sys, "Snip part of the screen", "Starts the Snipping Tool so you can pick an area (Win+Shift+S).", Combo: "win+shift+s"),
        new(ActionType.Screenshot, Sys, "Screenshot (whole screen)", "Saves a picture of all your screens to Pictures\\Screenshots."),
        new(ActionType.ToggleDarkMode, Sys, "Toggle dark mode", "Switches Windows between light and dark mode."),

        new(ActionType.LockPC, Power, "Lock PC", "Locks the PC."),
        new(ActionType.Sleep, Power, "Sleep", "Puts the PC to sleep."),
        new(ActionType.DisplayOff, Power, "Turn off display", "Turns the screens off. Move the mouse or press a key to wake them."),
        new(ActionType.ShutDown, Power, "Shut down", "Shuts down in 30 seconds. Use Cancel shut down / restart to stop it."),
        new(ActionType.Restart, Power, "Restart", "Restarts in 30 seconds. Use Cancel shut down / restart to stop it."),
        new(ActionType.CancelShutdown, Power, "Cancel shut down / restart", "Stops a shut down or restart that's counting down."),

        new(ActionType.Speak, Other, "Say something", "Reads the text aloud.", ParamKind.Text, "What to say"),
        new(ActionType.SayTime, Other, "Say the time", "Says the current time aloud."),
        new(ActionType.ShowMessage, Other, "Show a message", "Shows a small pop-up in the corner of the screen for a few seconds.",
            ParamKind.Text, "Message to show"),
        new(ActionType.RunCommand, Other, "Run command", "Runs a command-line command in the background, with no window.",
            ParamKind.Text, "e.g. ipconfig /flushdns"),
        new(ActionType.Wait, Other, "Wait", "Pauses before the next step.", ParamKind.Decimal, "Seconds, e.g. 1.5"),

        new(ActionType.HttpRequest, Advanced, "Send web request (HTTP)",
            "Calls a web address, e.g. a webhook, smart-home hub or API. Write: METHOD link | Header: value | body. " +
            "Only the link is required; headers and body are optional. The reply is kept as {result} for later steps. Fails if the server answers with an error.",
            ParamKind.Text, "e.g. POST https://example.com/hook | Authorization: Bearer abc | {\"text\": \"{heard}\"}"),
        new(ActionType.PickJson, Advanced, "Pick value from result",
            "When {result} is JSON (most APIs), replaces it with one value from inside it. Use dots and [numbers] to reach it, e.g. main.temp or items[0].name.",
            ParamKind.Text, "Path, e.g. main.temp or items[0].name"),
        new(ActionType.RunPowerShell, Advanced, "Run PowerShell script",
            "Runs PowerShell with no window and waits for it to finish (up to 60 seconds). What it prints is kept as {result}. Fails if the script reports an error.",
            ParamKind.Text, "e.g. (Get-Date).DayOfWeek"),
        new(ActionType.AskAi, Advanced, "Ask AI",
            "Sends a question to your AI and keeps the short answer as {result}, e.g. to say aloud or paste. Uses the AI service chosen in Settings → AI.",
            ParamKind.Text, "Question, e.g. {heard} or Summarize this: {clipboard}"),
        new(ActionType.ContinueIf, Advanced, "Continue only if…",
            "Stops the command here unless the condition is true. Write two values with one of: is, is not, contains, does not contain, starts with, ends with, >, <, is empty, is not empty.",
            ParamKind.Text, "e.g. {app} is chrome   or   {result} contains rain   or   {time} > 18:00"),
        new(ActionType.RunVoiceCommand, Advanced, "Run another command",
            "Runs the steps of another of your commands, so commands can be built from smaller ones. {heard} and {result} carry over.",
            ParamKind.Text, "The other command's phrase, e.g. volume up"),
        new(ActionType.AppendToFile, Advanced, "Add line to file",
            "Adds a line of text to the end of a text file, creating the file if needed. Write: file | text.",
            ParamKind.Text, "e.g. %USERPROFILE%\\Documents\\notes.txt | {date} {time}  {heard}"),
    ];

    private static readonly Dictionary<ActionType, ActionInfo> ByType = All.ToDictionary(a => a.Type);

    /// <summary>A step with no action chosen yet. Not listed in the picker.</summary>
    public static ActionInfo None { get; } = new(ActionType.None, "", "", "", Hint: "Choose an action first");

    public static ActionInfo Get(ActionType type) =>
        ByType.TryGetValue(type, out var info) ? info : type == ActionType.None ? None : new ActionInfo(type, Other, type.ToString(), "");
}

public class ActionStep : ObservableObject
{
    private ActionType _type;
    private string _parameter = "";
    private int _repeat = 1;

    public ActionType Type
    {
        get => _type;
        set
        {
            var oldKind = ParameterKind;
            if (!Set(ref _type, value)) return;
            // A leftover parameter would be misleading (e.g. a URL on "New tab"), so clear it when the
            // new action takes no parameter or a different kind. Same kind (e.g. two app names) is kept.
            if (ParameterKind == ParamKind.None || ParameterKind != oldKind) Parameter = "";
            OnPropertyChanged(nameof(NeedsParameter));
            OnPropertyChanged(nameof(ParameterKind));
            OnPropertyChanged(nameof(ParameterHint));
            OnPropertyChanged(nameof(ParameterTip));
        }
    }

    /// <summary>Most steps ignore the parameter, so the UI disables that box for them.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsParameter => ParameterKind != ParamKind.None;

    [System.Text.Json.Serialization.JsonIgnore]
    public ParamKind ParameterKind => ActionCatalog.Get(Type).Param;

    /// <summary>Placeholder shown in the empty parameter box.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterHint => ActionCatalog.Get(Type).Hint ?? "No parameter needed";

    public string Parameter
    {
        get => _parameter;
        set { if (Set(ref _parameter, value ?? "")) OnPropertyChanged(nameof(ParameterTip)); }
    }

    /// <summary>Tooltip for the parameter box: what's typed (it can be wider than the box), or the hint while empty. Null = no tooltip.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ParameterTip => !NeedsParameter ? null : Parameter.Length > 0 ? Parameter : ParameterHint;

    /// <summary>How many times to run this step (e.g. VolumeUp x5 = +10%).</summary>
    public int Repeat { get => _repeat; set => Set(ref _repeat, Math.Clamp(value, 1, 100)); }
}
