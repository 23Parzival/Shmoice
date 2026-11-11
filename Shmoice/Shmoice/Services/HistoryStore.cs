using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>
/// Everything Shmoice ran (or heard and didn't run), newest first, kept in
/// %APPDATA%\Shmoice\history.json so it survives restarts. Keeps the last <see cref="MaxItems"/>.
/// </summary>
public class HistoryStore
{
    public const int MaxItems = 1000;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shmoice", "history.json");

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public ObservableCollection<HistoryItem> Items { get; } = new();

    public static HistoryStore Load()
    {
        var store = new HistoryStore();
        try
        {
            if (File.Exists(FilePath))
                foreach (var item in (JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(FilePath), Options) ?? new())
                             .OrderByDescending(i => i.Time).Take(MaxItems))
                    store.Items.Add(item); // newest first
        }
        catch (Exception) { /* a broken history file just starts fresh */ }
        return store;
    }

    public void Add(HistoryItem item)
    {
        Items.Insert(0, item);
        while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, Options));
        }
        catch (Exception) { /* history is nice to have; never let it break a command */ }
    }
}
