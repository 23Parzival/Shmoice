using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.Wave;

namespace Shmoice.Services;

/// <summary>
/// Listens on a microphone (the Windows default or one the user picked) using the offline
/// Windows speech recognizer. It only listens for the exact phrases it's given (a closed
/// grammar), which is far more accurate than free dictation and needs no internet or API key.
/// </summary>
public sealed class SpeechListener : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private WaveInEvent? _mic;
    private BlockingAudioStream? _audio;
    private string _wakeWord = "";
    private double _threshold;

    /// <summary>Raised with the matched phrase (wake word removed) and confidence. Fires on a background thread.</summary>
    public event Action<string, float>? Recognized;

    /// <summary>Raised when something matched but was below the confidence threshold.</summary>
    public event Action<string, float>? LowConfidence;

    public bool IsListening => _engine != null;

    /// <summary>The device currently being listened to.</summary>
    public AudioInput? Input { get; private set; }

    public void Start(IEnumerable<string> phrases, string wakeWord, double confidenceThreshold, AudioInput input)
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
            var commands = new Grammar(builder) { Name = "commands" };
            engine.LoadGrammar(commands);
            _commands = commands;
            // Free dictation, switched on only while a "Listen for words" step is waiting.
            try
            {
                var dictation = new DictationGrammar { Name = "dictation", Enabled = false };
                engine.LoadGrammar(dictation);
                _dictation = dictation;
            }
            catch (Exception) { _dictation = null; } // recognizer without dictation support
            engine.SpeechDetected += OnSpeechDetected;
            if (input.IsDefault) engine.SetInputToDefaultAudioDevice();
            else StartMicrophone(engine, input.DeviceNumber);
            engine.SpeechRecognized += OnSpeechRecognized;
            _threshold = confidenceThreshold;
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            StopMicrophone();
            engine.Dispose();
            throw;
        }
        _engine = engine;
        Input = input;
    }

    /// <summary>
    /// System.Speech can only open the default device by itself, so for a chosen device we record
    /// with NAudio (16 kHz, 16-bit mono, which the recognizer accepts) and stream the audio in.
    /// </summary>
    private void StartMicrophone(SpeechRecognitionEngine engine, int deviceNumber)
    {
        var audio = new BlockingAudioStream();
        var mic = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        };
        mic.DataAvailable += (_, e) => audio.Write(e.Buffer, e.BytesRecorded);
        _audio = audio;
        _mic = mic;
        engine.SetInputToAudioStream(audio,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        mic.StartRecording();
    }

    private void StopMicrophone()
    {
        if (_mic != null)
        {
            try { _mic.StopRecording(); } catch { /* device may already be gone */ }
            _mic.Dispose();
            _mic = null;
        }
        // Lets the engine's blocked read return so it can shut down.
        _audio?.Complete();
        _audio = null;
    }

    public void Stop()
    {
        StopMicrophone();
        Input = null;
        if (_engine == null) return;
        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechDetected -= OnSpeechDetected;
        _engine.RecognizeAsyncCancel();
        _engine.Dispose();
        _engine = null;
        _commands = null;
        _dictation = null;
        _pendingWords?.TrySetCanceled();
    }

    // ---- Dictation ("Listen for words") ----------------------------------------------------

    private Grammar? _commands;
    private Grammar? _dictation;
    private TaskCompletionSource<string>? _pendingWords;
    private volatile bool _speechStarted;

    /// <summary>
    /// Switches from command phrases to free dictation, returns the next thing said, then switches
    /// back. <paramref name="startTimeout"/> is how long to wait for you to start talking; once you
    /// have started, it waits for you to finish. Returns "" if nothing was heard.
    /// </summary>
    public async Task<string> ListenForWordsAsync(TimeSpan startTimeout)
    {
        if (_engine == null || _commands == null)
            throw new InvalidOperationException("Shmoice isn't listening right now (set a wake word and enable a command).");
        if (_dictation == null)
            throw new InvalidOperationException("This Windows speech language doesn't support free dictation.");
        if (_pendingWords != null) throw new InvalidOperationException("Already listening for words.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingWords = tcs;
        _speechStarted = false;
        var commands = _commands;
        var dictation = _dictation;
        commands.Enabled = false; // so what you say can't trigger another command
        dictation.Enabled = true;
        try
        {
            if (await Task.WhenAny(tcs.Task, Task.Delay(startTimeout)) != tcs.Task && _speechStarted)
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15))); // still talking: let them finish
            return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : "";
        }
        finally
        {
            _pendingWords = null;
            if (_dictation == dictation) // the engine wasn't restarted meanwhile
            {
                dictation.Enabled = false;
                commands.Enabled = true;
            }
        }
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        if (_pendingWords != null) _speechStarted = true;
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Grammar?.Name == "dictation")
        {
            if (e.Result.Text.Trim().Length > 0) _pendingWords?.TrySetResult(e.Result.Text.Trim());
            return;
        }

        var text = e.Result.Text.Trim();
        if (_wakeWord.Length > 0 && text.StartsWith(_wakeWord, StringComparison.OrdinalIgnoreCase))
            text = text[_wakeWord.Length..].Trim();

        if (e.Result.Confidence >= _threshold) Recognized?.Invoke(text, e.Result.Confidence);
        else LowConfidence?.Invoke(text, e.Result.Confidence);
    }

    public void Dispose() => Stop();
}
