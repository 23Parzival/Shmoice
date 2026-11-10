namespace Shmoice.Models;

/// <summary>
/// The kinds of things a command can do. Each step is one of these plus an optional
/// parameter. This flat shape is deliberately simple so a future AI "command builder"
/// can produce it as plain JSON, e.g. { "Type": "KeyCombo", "Parameter": "ctrl+shift+esc" }.
/// </summary>
public enum ActionType
{
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
    MediaStop,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    /// <summary>Parameter: keys joined by '+', e.g. "ctrl+shift+esc", "alt+tab", "f5".</summary>
    KeyCombo,
    /// <summary>Parameter: the text to type.</summary>
    TypeText,
    /// <summary>Parameter: program path, file, folder, or URL. Optional arguments after '|'.</summary>
    Open,
    /// <summary>Parameter: seconds to wait, decimals allowed (e.g. "1.5").</summary>
    Wait,
}

public class ActionStep : ObservableObject
{
    private ActionType _type;
    private string _parameter = "";
    private int _repeat = 1;

    /// <summary>Human-friendly names for the action dropdown.</summary>
    public static readonly IReadOnlyDictionary<ActionType, string> Labels = new Dictionary<ActionType, string>
    {
        [ActionType.MediaPlayPause] = "Play / Pause",
        [ActionType.MediaNext] = "Next track",
        [ActionType.MediaPrevious] = "Previous track",
        [ActionType.MediaStop] = "Stop media",
        [ActionType.VolumeUp] = "Volume up",
        [ActionType.VolumeDown] = "Volume down",
        [ActionType.VolumeMute] = "Mute / Unmute",
        [ActionType.KeyCombo] = "Press keys",
        [ActionType.TypeText] = "Type text",
        [ActionType.Open] = "Open app / file / URL",
        [ActionType.Wait] = "Wait",
    };

    public ActionType Type
    {
        get => _type;
        set
        {
            if (!Set(ref _type, value)) return;
            OnPropertyChanged(nameof(NeedsParameter));
            OnPropertyChanged(nameof(ParameterHint));
        }
    }

    /// <summary>Media and volume steps ignore the parameter, so the UI disables that box for them.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsParameter => Type is ActionType.KeyCombo or ActionType.TypeText or ActionType.Open or ActionType.Wait;

    /// <summary>Placeholder shown in the empty parameter box.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterHint => Type switch
    {
        ActionType.KeyCombo => "e.g. ctrl+shift+esc, alt+tab, win+d",
        ActionType.TypeText => "Text to type",
        ActionType.Open => "e.g. https://youtube.com, notepad.exe, spotify:",
        ActionType.Wait => "Seconds, e.g. 1.5",
        _ => "No parameter needed",
    };

    public string Parameter { get => _parameter; set => Set(ref _parameter, value ?? ""); }

    /// <summary>How many times to run this step (e.g. VolumeUp x5 = +10%).</summary>
    public int Repeat { get => _repeat; set => Set(ref _repeat, Math.Clamp(value, 1, 100)); }
}
