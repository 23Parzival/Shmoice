using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Shmoice.Services;

/// <summary>A microphone / recording device the user can pick.</summary>
/// <param name="DeviceNumber">WaveIn device index, or -1 for "whatever Windows' default is".</param>
public sealed record AudioInput(int DeviceNumber, string Name)
{
    public static readonly AudioInput WindowsDefault = new(-1, "Windows default microphone");

    public bool IsDefault => DeviceNumber < 0;

    public override string ToString() => Name;
}

public static class AudioInputs
{
    /// <summary>Lists recording devices, with "Windows default" first.</summary>
    public static List<AudioInput> List()
    {
        var list = new List<AudioInput> { AudioInput.WindowsDefault };

        // WaveIn names are cut off at 31 characters, so look up the full names from Core Audio.
        var fullNames = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                fullNames.Add(d.FriendlyName);
        }
        catch { /* fall back to the short names */ }

        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var shortName = WaveInEvent.GetCapabilities(i).ProductName;
            var name = fullNames.FirstOrDefault(n => n.StartsWith(shortName, StringComparison.OrdinalIgnoreCase)) ?? shortName;
            list.Add(new AudioInput(i, name));
        }
        return list;
    }
}
