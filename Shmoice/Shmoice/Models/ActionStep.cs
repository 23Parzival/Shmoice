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
    /// <summary>Parameter: milliseconds to wait.</summary>
    Wait,
}

public class ActionStep : ObservableObject
{
    private ActionType _type;
    private string _parameter = "";
    private int _repeat = 1;

    public ActionType Type { get => _type; set => Set(ref _type, value); }

    public string Parameter { get => _parameter; set => Set(ref _parameter, value ?? ""); }

    /// <summary>How many times to run this step (e.g. VolumeUp x5 = +10%).</summary>
    public int Repeat { get => _repeat; set => Set(ref _repeat, Math.Clamp(value, 1, 100)); }
}
