using System.Globalization;
using System.Speech.Recognition;

namespace Shmoice.Services;

/// <summary>
/// Listens on the default microphone using the offline Windows speech recognizer.
/// It only listens for the exact phrases it's given (a closed grammar), which is far
/// more accurate than free dictation and needs no internet or API key.
/// </summary>
public sealed class SpeechListener : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private string _wakeWord = "";
    private double _threshold;

    /// <summary>Raised with the matched phrase (wake word removed) and confidence. Fires on a background thread.</summary>
    public event Action<string, float>? Recognized;

    /// <summary>Raised when something matched but was below the confidence threshold.</summary>
    public event Action<string, float>? LowConfidence;

    public bool IsListening => _engine != null;

    public void Start(IEnumerable<string> phrases, string wakeWord, double confidenceThreshold)
    {
        Stop();

        var choices = phrases.Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
            throw new InvalidOperationException("Add at least one enabled command before listening.");

        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var info = recognizers.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture))
                   ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                   ?? recognizers.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       "No speech recognizer is installed. Add a speech language in Windows Settings > Time & language > Speech.");

        var builder = new GrammarBuilder { Culture = info.Culture };
        _wakeWord = wakeWord.Trim();
        if (_wakeWord.Length > 0) builder.Append(_wakeWord);
        builder.Append(new Choices(choices));

        var engine = new SpeechRecognitionEngine(info);
        try
        {
            engine.LoadGrammar(new Grammar(builder) { Name = "commands" });
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnSpeechRecognized;
            _threshold = confidenceThreshold;
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        _engine = engine;
    }

    public void Stop()
    {
        if (_engine == null) return;
        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.RecognizeAsyncCancel();
        _engine.Dispose();
        _engine = null;
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text.Trim();
        if (_wakeWord.Length > 0 && text.StartsWith(_wakeWord, StringComparison.OrdinalIgnoreCase))
            text = text[_wakeWord.Length..].Trim();

        if (e.Result.Confidence >= _threshold) Recognized?.Invoke(text, e.Result.Confidence);
        else LowConfidence?.Invoke(text, e.Result.Confidence);
    }

    public void Dispose() => Stop();
}
