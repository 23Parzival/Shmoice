using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>Loads and saves commands + settings as JSON in %APPDATA%\Shmoice\commands.json.</summary>
public static class CommandStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shmoice", "commands.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The JSON as it is on disk, to tell whether there are unsaved edits.</summary>
    private static string? _lastSaved;

    public static AppData Load()
    {
        var data = LoadOrDefaults();
        _lastSaved = File.Exists(FilePath) ? JsonSerializer.Serialize(data, Options) : null;
        return data;
    }

    private static AppData LoadOrDefaults()
    {
        if (!File.Exists(FilePath)) return CreateDefaults();
        try
        {
            var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath), Options) ?? CreateDefaults();
            Upgrade(data);
            return data;
        }
        catch (JsonException)
        {
            // Keep the broken file around rather than silently overwriting the user's work.
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            return CreateDefaults();
        }
    }

    public static void Save(AppData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(data, Options);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        _lastSaved = json;
    }

    /// <summary>The commands as JSON: a snapshot for back / forward to return to.</summary>
    public static string SnapshotCommands(IEnumerable<VoiceCommand> commands) => JsonSerializer.Serialize(commands, Options);

    public static List<VoiceCommand> RestoreCommands(string snapshot) =>
        JsonSerializer.Deserialize<List<VoiceCommand>>(snapshot, Options) ?? new();

    /// <summary>True if anything has changed since the last load or save.</summary>
    public static bool HasUnsavedChanges(AppData data) => JsonSerializer.Serialize(data, Options) != _lastSaved;

    /// <summary>Brings files saved by older versions up to the current format. Saved on the next Save.</summary>
    private static void Upgrade(AppData data)
    {
        // Older versions kept the parameter when switching to an action that doesn't use one.
        foreach (var step in data.Commands.SelectMany(c => c.Steps).Where(s => !s.NeedsParameter && s.Parameter.Length > 0))
            step.Parameter = "";

        if (data.Version < 2)
        {
            // Wait used to be milliseconds; it's now seconds. "1500" -> "1.5".
            foreach (var step in data.Commands.SelectMany(c => c.Steps).Where(s => s.Type == ActionType.Wait))
                if (double.TryParse(step.Parameter, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ms))
                    step.Parameter = (ms / 1000).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (data.Version < 3)
        {
            // "play" / "pause" used the Play/Pause toggle key, so "play" could pause things.
            // Switch commands named exactly that to the real Play / Pause actions.
            foreach (var command in data.Commands)
            {
                var target = command.Phrase.Trim().ToLowerInvariant() switch
                {
                    "play" => ActionType.MediaPlay,
                    "pause" => ActionType.MediaPause,
                    _ => (ActionType?)null,
                };
                if (target is { } type)
                    foreach (var step in command.Steps.Where(s => s.Type == ActionType.MediaPlayPause))
                        step.Type = type;
            }
        }
        // A wake word is now required; older files could have saved it empty.
        if (string.IsNullOrWhiteSpace(data.Settings.WakeWord)) data.Settings.WakeWord = AppSettings.DefaultWakeWord;
        data.Version = AppData.CurrentVersion;
    }

    private static AppData CreateDefaults()
    {
        static VoiceCommand Cmd(string phrase, ActionType type, int repeat = 1) => new()
        {
            Phrase = phrase,
            Steps = { new ActionStep { Type = type, Repeat = repeat } },
        };

        return new AppData
        {
            Version = AppData.CurrentVersion,
            Commands =
            {
                Cmd("play", ActionType.MediaPlay),
                Cmd("pause", ActionType.MediaPause),
                Cmd("next song", ActionType.MediaNext),
                Cmd("previous song", ActionType.MediaPrevious),
                Cmd("volume up", ActionType.VolumeUp, 5),
                Cmd("volume down", ActionType.VolumeDown, 5),
                Cmd("mute", ActionType.VolumeMute),
            },
        };
    }
}
