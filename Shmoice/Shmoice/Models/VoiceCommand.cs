using System.Collections.ObjectModel;

namespace Shmoice.Models;

/// <summary>A spoken phrase and the sequence of actions it triggers.</summary>
public class VoiceCommand : ObservableObject
{
    private string _phrase = "";
    private bool _enabled = true;

    /// <summary>Stable identity, so back / forward can find a command again after restoring an earlier version.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Phrase { get => _phrase; set => Set(ref _phrase, value ?? ""); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public ObservableCollection<ActionStep> Steps { get; set; } = new();
}

public class AppSettings : ObservableObject
{
    public const string DefaultWakeWord = "computer";

    private string _wakeWord = DefaultWakeWord;
    private double _confidenceThreshold = 0.7;

    /// <summary>Word that must be said before every command, e.g. "computer". Listening pauses while it's empty.</summary>
    public string WakeWord { get => _wakeWord; set => Set(ref _wakeWord, value ?? ""); }

    /// <summary>0..1 — recognitions below this are ignored to avoid false triggers.</summary>
    public double ConfidenceThreshold { get => _confidenceThreshold; set => Set(ref _confidenceThreshold, Math.Clamp(value, 0, 1)); }

    /// <summary>Anthropic API key, encrypted with Windows DPAPI (see SecretStore). Empty = use ANTHROPIC_API_KEY env var.</summary>
    public string EncryptedApiKey { get; set; } = "";

    /// <summary>Show the "Delete command?" confirmation. Turned off by "Don't ask me again".</summary>
    public bool ConfirmDelete { get; set; } = true;

    /// <summary>Name of the microphone to listen on. Empty = Windows default.</summary>
    public string InputDeviceName { get; set; } = "";

    private double _chimeVolume = 0.6;

    /// <summary>0..1 volume of the "go ahead" chime played by Listen for words. 0 = silent.</summary>
    public double ChimeVolume { get => _chimeVolume; set => Set(ref _chimeVolume, Math.Clamp(value, 0, 1)); }
}

/// <summary>Everything persisted to disk.</summary>
public class AppData
{
    /// <summary>Bumped when the saved format changes, so old files can be upgraded once. 2 = Wait in seconds. 3 = real Play / Pause.</summary>
    public const int CurrentVersion = 3;

    /// <summary>0 for files saved before versioning existed.</summary>
    public int Version { get; set; }

    public AppSettings Settings { get; set; } = new();
    public ObservableCollection<VoiceCommand> Commands { get; set; } = new();
}
