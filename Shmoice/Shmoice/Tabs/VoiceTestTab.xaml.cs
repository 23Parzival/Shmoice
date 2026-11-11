using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using Shmoice.Services;

namespace Shmoice.Tabs;

/// <summary>
/// Check the mic, practice saying commands without running them, and spot phrases that sound alike.
/// The level meter only runs while this tab is showing; practice mode turns off when you leave it.
/// </summary>
public partial class VoiceTestTab : UserControl, ITab
{
    public record HeardLine(string Time, string Phrase, float Confidence, string Outcome);

    private MainWindow? _host;
    private readonly ObservableCollection<HeardLine> _feed = new();
    private WaveInEvent? _meter;
    private float _peak;
    private double _loudest;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public VoiceTestTab()
    {
        InitializeComponent();
        Feed.ItemsSource = _feed;
        _tick.Tick += Tick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Shown();
            else Hidden();
        };
    }

    public void Attach(MainWindow host)
    {
        _host = host;
        host.Heard += (phrase, confidence, outcome) =>
        {
            _feed.Insert(0, new HeardLine(DateTime.Now.ToString("HH:mm:ss"), phrase, confidence, outcome));
            while (_feed.Count > 100) _feed.RemoveAt(_feed.Count - 1);
            FeedEmpty.Visibility = Visibility.Collapsed;
        };
        UpdatePracticeHint();
    }

    private void Shown()
    {
        StartMeter();
        CheckPhrases();
    }

    private void Hidden()
    {
        StopMeter();
        // Don't leave commands silently switched off while you're elsewhere in the app.
        if (_host is { PracticeMode: true })
        {
            _host.PracticeMode = false;
            PracticeCheck.IsChecked = false;
            UpdatePracticeHint();
            _host.Log("Practice mode off: commands run again");
        }
    }

    // ---- Level meter -------------------------------------------------------------------

    private void StartMeter()
    {
        StopMeter();
        if (_host == null) return;
        var input = _host.CurrentInput();
        MicName.Text = input.Name;
        LevelBar.Value = 0;
        _loudest = 0;
        LevelHint.Text = "Say something. The bar should move while you talk.";
        try
        {
            var meter = new WaveInEvent { DeviceNumber = input.DeviceNumber, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            meter.DataAvailable += (_, a) =>
            {
                var peak = 0;
                for (var i = 0; i + 1 < a.BytesRecorded; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(a.Buffer, i)));
                _peak = Math.Max(_peak, peak / 32768f);
            };
            meter.StartRecording();
            _meter = meter;
            _tick.Start();
        }
        catch (Exception ex)
        {
            LevelHint.Text = "Couldn't open this microphone: " + ex.Message;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        var level = Math.Sqrt(_peak); // square root makes quiet speech visible
        _peak = 0;
        LevelBar.Value = Math.Max(level, LevelBar.Value * 0.85);
        _loudest = Math.Max(_loudest, level);
        if (_loudest > 0.15) LevelHint.Text = "This microphone is picking you up.";
    }

    private void StopMeter()
    {
        _tick.Stop();
        if (_meter == null) return;
        try { _meter.StopRecording(); } catch { /* device may be gone */ }
        _meter.Dispose();
        _meter = null;
    }

    // ---- Practice mode -----------------------------------------------------------------

    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.PracticeMode = PracticeCheck.IsChecked == true;
        UpdatePracticeHint();
        _host.Log(_host.PracticeMode ? "Practice mode on: commands won't run" : "Practice mode off: commands run again");
    }

    private void UpdatePracticeHint() => PracticeHint.Text = _host?.PracticeMode == true
        ? "On: say your commands freely; nothing will run. It turns off when you leave this tab."
        : "Off: commands you say here run as normal.";

    private void ClearFeed_Click(object sender, RoutedEventArgs e)
    {
        _feed.Clear();
        FeedEmpty.Visibility = Visibility.Visible;
    }

    // ---- Phrases that sound alike ------------------------------------------------------

    private void CheckPhrases_Click(object sender, RoutedEventArgs e) => CheckPhrases();

    private void CheckPhrases()
    {
        if (_host == null) return;
        var commands = _host.Data.Commands.Where(c => c.Enabled && c.Steps.Count > 0).ToList();
        var pairs = PhraseSimilarity.FindSimilar(commands.Select(c => c.Phrase))
            // The same phrase in two different profiles is intended (different apps), not a clash.
            .Where(p => !(p.A.Equals(p.B, StringComparison.OrdinalIgnoreCase) && InDifferentProfiles(commands, p.A)))
            .ToList();
        SimilarList.ItemsSource = pairs;
        SimilarEmpty.Text = pairs.Count == 0
            ? $"None found. All {commands.Count} of your enabled commands sound different enough."
            : "Shmoice may mix these up. Consider rewording one of each pair.";
    }

    private static bool InDifferentProfiles(List<Models.VoiceCommand> commands, string phrase)
    {
        var profiles = commands.Where(c => c.Phrase.Trim().Equals(phrase, StringComparison.OrdinalIgnoreCase)).Select(c => c.ProfileId).ToList();
        return profiles.Distinct().Count() == profiles.Count;
    }
}
