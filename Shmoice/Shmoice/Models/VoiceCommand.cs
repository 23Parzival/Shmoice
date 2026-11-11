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

    private Guid? _profileId;

    /// <summary>The profile this command belongs to: it only runs while one of that profile's apps is in front. Null = any app.</summary>
    public Guid? ProfileId { get => _profileId; set => Set(ref _profileId, value); }

    public ObservableCollection<ActionStep> Steps { get; set; } = new();
}

/// <summary>
/// A group of commands that only run while one of the given apps is in front, e.g. a "Game" profile
/// for "eldenring". Lets the same phrase do different things in different apps.
/// </summary>
public class Profile : ObservableObject
{
    private string _name = "";
    private string _apps = "";
    private bool _enabled = true;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    /// <summary>App names (process names), comma-separated, e.g. "chrome, firefox". ".exe" is optional.</summary>
    public string Apps { get => _apps; set => Set(ref _apps, value ?? ""); }

    /// <summary>When off, the profile's commands don't run at all.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public IEnumerable<string> AppNames() => Apps
        .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a)
        .Where(a => a.Length > 0);

    public bool Matches(string? processName) =>
        processName != null && AppNames().Any(a => a.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

public enum ScheduleKind
{
    /// <summary>At a time of day, on chosen days of the week.</summary>
    AtTime,
    /// <summary>Every N minutes while Shmoice is running.</summary>
    Every,
}

/// <summary>Runs a command automatically, without speaking.</summary>
public class Schedule : ObservableObject
{
    private bool _enabled = true;
    private Guid? _commandId;
    private ScheduleKind _kind = ScheduleKind.AtTime;
    private string _time = "09:00";
    private int _days = 0b1111111; // bit 0 = Monday … bit 6 = Sunday
    private int _intervalMinutes = 30;
    private string _status = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Guid? CommandId { get => _commandId; set => Set(ref _commandId, value); }

    public ScheduleKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) OnPropertyChanged(nameof(IsAtTime)); }
    }

    /// <summary>24-hour "HH:mm", for <see cref="ScheduleKind.AtTime"/>.</summary>
    public string Time { get => _time; set => Set(ref _time, value ?? ""); }

    public int Days { get => _days; set => Set(ref _days, value & 0b1111111); }

    public int IntervalMinutes { get => _intervalMinutes; set => Set(ref _intervalMinutes, Math.Clamp(value, 1, 24 * 60)); }

    /// <summary>When it last ran (local time), so it doesn't run twice for the same slot.</summary>
    public DateTime? LastRun { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAtTime => Kind == ScheduleKind.AtTime;

    /// <summary>"Next: today 09:00" etc., kept up to date by the scheduler.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _title = "";

    /// <summary>The command's phrase, for the schedule list (kept up to date by the main window).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>"09:00 · weekdays" or "Every 30 min", for the schedule list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary
    {
        get
        {
            if (Kind == ScheduleKind.Every) return IntervalMinutes == 1 ? "Every minute" : $"Every {IntervalMinutes} min";
            var days = Days switch
            {
                0b1111111 => "every day",
                0b0011111 => "weekdays",
                0b1100000 => "weekends",
                0 => "no days",
                _ => string.Join(" ", new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }.Where((_, i) => (Days & (1 << i)) != 0)),
            };
            return $"{Time} · {days}";
        }
    }

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is nameof(Kind) or nameof(Time) or nameof(Days) or nameof(IntervalMinutes)) base.OnPropertyChanged(nameof(Summary));
    }

    // One property per weekday so each day can be a checkbox.
    [System.Text.Json.Serialization.JsonIgnore] public bool Mon { get => Day(0); set => SetDay(0, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Tue { get => Day(1); set => SetDay(1, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Wed { get => Day(2); set => SetDay(2, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Thu { get => Day(3); set => SetDay(3, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Fri { get => Day(4); set => SetDay(4, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Sat { get => Day(5); set => SetDay(5, value); }
    [System.Text.Json.Serialization.JsonIgnore] public bool Sun { get => Day(6); set => SetDay(6, value); }

    private bool Day(int i) => (_days & (1 << i)) != 0;
    private void SetDay(int i, bool on) => Days = on ? _days | (1 << i) : _days & ~(1 << i);

    /// <summary>Monday = 0 … Sunday = 6.</summary>
    public bool RunsOn(DayOfWeek day) => Day(((int)day + 6) % 7);
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

    private bool _confirmDelete = true;
    private bool _showSplash = true;
    private bool _startMinimized;

    /// <summary>Show the "Delete command?" confirmation. Turned off by "Don't ask me again".</summary>
    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

    /// <summary>Show the logo for a moment when Shmoice starts.</summary>
    public bool ShowSplash { get => _showSplash; set => Set(ref _showSplash, value); }

    /// <summary>Open minimized to the taskbar (handy with Start with Windows).</summary>
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    public const string DefaultAccent = "#7C5CFF";

    private string _accentColor = DefaultAccent;
    private string _theme = "Dark";

    /// <summary>Highlight color for selections, buttons, checkboxes etc., as "#RRGGBB".</summary>
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value ?? DefaultAccent); }

    /// <summary>"Dark", "Light" or "System" (follow Windows).</summary>
    public string Theme { get => _theme; set => Set(ref _theme, value ?? "Dark"); }

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
    public ObservableCollection<Profile> Profiles { get; set; } = new();
    public ObservableCollection<Schedule> Schedules { get; set; } = new();
}

public enum RunSource { Voice, Schedule, Test, VoiceTest }

public enum RunResult { Ran, Failed, Ignored, Skipped }

/// <summary>One line of history: a command that was run (or heard but not run), and how it went.</summary>
public class HistoryItem
{
    public DateTime Time { get; set; } = DateTime.Now;
    public Guid? CommandId { get; set; }
    public string Phrase { get; set; } = "";
    public RunSource Source { get; set; }
    public RunResult Result { get; set; }
    /// <summary>Recognition confidence (0..1) for voice runs.</summary>
    public float? Confidence { get; set; }
    /// <summary>What a "Listen for words" step heard, if any.</summary>
    public string Heard { get; set; } = "";
    /// <summary>Error message, or why it was ignored or skipped.</summary>
    public string Detail { get; set; } = "";
}
