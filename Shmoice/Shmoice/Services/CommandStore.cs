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

    public static AppData Load()
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
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Brings files saved by older versions up to the current format. Saved on the next Save.</summary>
    private static void Upgrade(AppData data)
    {
        if (data.Version < 2)
        {
            // Wait used to be milliseconds; it's now seconds. "1500" -> "1.5".
            foreach (var step in data.Commands.SelectMany(c => c.Steps).Where(s => s.Type == ActionType.Wait))
                if (double.TryParse(step.Parameter, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ms))
                    step.Parameter = (ms / 1000).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
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
                Cmd("play", ActionType.MediaPlayPause),
                Cmd("pause", ActionType.MediaPlayPause),
                Cmd("next song", ActionType.MediaNext),
                Cmd("previous song", ActionType.MediaPrevious),
                Cmd("volume up", ActionType.VolumeUp, 5),
                Cmd("volume down", ActionType.VolumeDown, 5),
                Cmd("mute", ActionType.VolumeMute),
            },
        };
    }
}
