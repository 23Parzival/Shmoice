using NAudio.Wave;

namespace Shmoice.Services;

/// <summary>A short, soft two-note chime that means "go ahead, I'm listening".</summary>
public static class Chime
{
    /// <param name="volume">0..1; 0 plays nothing.</param>
    public static void Play(double volume)
    {
        if (volume <= 0) return;
        var output = new WaveOutEvent();
        output.Init(new ChimeProvider(Math.Min(volume, 1)));
        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }

    /// <summary>Two sine notes, each with a quick fade in and a gentle fade out so there are no clicks.</summary>
    private sealed class ChimeProvider(double volume) : ISampleProvider
    {
        // Full volume peaks at 0.4 of maximum: loud enough to notice, never harsh.
        private readonly double _gain = 0.4 * volume * volume; // squared so the slider feels even to the ear
        private const int Rate = 44100;
        private static readonly (double Freq, double Seconds)[] Notes = [(880, 0.11), (1320, 0.16)];
        private int _note, _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count && _note < Notes.Length)
            {
                var (freq, seconds) = Notes[_note];
                var length = (int)(seconds * Rate);
                var t = (double)_pos / Rate;
                var envelope = Math.Min(1, t / 0.005) * Math.Pow(1 - (double)_pos / length, 2);
                buffer[offset + written++] = (float)(_gain * envelope * Math.Sin(2 * Math.PI * freq * t));
                if (++_pos >= length) { _pos = 0; _note++; }
            }
            return written;
        }
    }
}
