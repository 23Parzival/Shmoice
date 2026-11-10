using System.Collections.ObjectModel;

namespace Shmoice.Models;

/// <summary>A spoken phrase and the sequence of actions it triggers.</summary>
public class VoiceCommand : ObservableObject
{
    private string _phrase = "";
    private bool _enabled = true;

    public string Phrase { get => _phrase; set => Set(ref _phrase, value ?? ""); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ObservableCollection<ActionStep> Steps { get; set; } = new();
}

public class AppSettings : ObservableObject
{
    private string _wakeWord = "";
    private double _confidenceThreshold = 0.7;

    /// <summary>Optional word that must be said before a command, e.g. "computer".</summary>
    public string WakeWord { get => _wakeWord; set => Set(ref _wakeWord, value ?? ""); }

    /// <summary>0..1 — recognitions below this are ignored to avoid false triggers.</summary>
    public double ConfidenceThreshold { get => _confidenceThreshold; set => Set(ref _confidenceThreshold, Math.Clamp(value, 0, 1)); }
}

/// <summary>Everything persisted to disk.</summary>
public class AppData
{
    public AppSettings Settings { get; set; } = new();
    public ObservableCollection<VoiceCommand> Commands { get; set; } = new();
}
