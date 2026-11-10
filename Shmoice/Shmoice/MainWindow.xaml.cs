using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Shmoice.Models;
using Shmoice.Services;

namespace Shmoice;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private readonly SpeechListener _listener = new();

    public MainWindow()
    {
        InitializeComponent();
        _data = CommandStore.Load();
        DataContext = _data;

        _listener.Recognized += (phrase, confidence) => Dispatcher.Invoke(() => OnHeard(phrase, confidence));
        _listener.LowConfidence += (phrase, confidence) =>
            Dispatcher.Invoke(() => Log($"Ignored \"{phrase}\" ({confidence:P0} < threshold)"));

        if (_data.Commands.Count > 0) CommandList.SelectedIndex = 0;
        UpdateEditorState();
        UpdateApiKeyStatus();
        Log($"Commands loaded from {CommandStore.FilePath}");
    }

    private VoiceCommand? Selected => CommandList.SelectedItem as VoiceCommand;

    // ---- Listening --------------------------------------------------------------------

    private void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listener.IsListening) StopListening();
        else StartListening();
    }

    private void StartListening()
    {
        try
        {
            CommitEdits();
            _listener.Start(EnabledPhrases(), _data.Settings.WakeWord, _data.Settings.ConfidenceThreshold);
            SetStatus(true);
            var wake = string.IsNullOrWhiteSpace(_data.Settings.WakeWord) ? "" : $" (say \"{_data.Settings.WakeWord.Trim()}\" first)";
            Log($"Listening for {EnabledPhrases().Count()} command(s){wake}");
        }
        catch (Exception ex)
        {
            SetStatus(false);
            Log("Could not start listening: " + ex.Message);
        }
    }

    private void StopListening()
    {
        _listener.Stop();
        SetStatus(false);
        Log("Stopped listening");
    }

    /// <summary>The recognizer's grammar is fixed when it starts, so restart it after edits.</summary>
    private void RestartIfListening()
    {
        if (_listener.IsListening) StartListening();
    }

    private IEnumerable<string> EnabledPhrases() =>
        _data.Commands.Where(c => c.Enabled && c.Steps.Count > 0).Select(c => c.Phrase);

    private async void OnHeard(string phrase, float confidence)
    {
        var matches = _data.Commands
            .Where(c => c.Enabled && string.Equals(c.Phrase.Trim(), phrase, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Log($"Heard \"{phrase}\" ({confidence:P0})");
        foreach (var command in matches) await Run(command);
    }

    private async Task Run(VoiceCommand command)
    {
        try
        {
            await ActionExecutor.RunAsync(command);
        }
        catch (Exception ex)
        {
            Log($"\"{command.Phrase}\" failed: {ex.Message}");
        }
    }

    private void SetStatus(bool listening)
    {
        ListenButton.Content = listening ? "■  Stop listening" : "🎙  Start listening";
        StatusText.Text = listening ? "Listening…" : "Not listening";
        StatusDot.Fill = (Brush)FindResource(listening ? "ListeningBrush" : "IdleBrush");
    }

    // ---- Command editing --------------------------------------------------------------

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = new VoiceCommand { Phrase = "new command", Steps = { new ActionStep() } };
        _data.Commands.Add(command);
        CommandList.SelectedItem = command;
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } command) return;
        if (MessageBox.Show($"Delete \"{command.Phrase}\"?", "Shmoice", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _data.Commands.Remove(command);
        SaveAll();
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } command) return;
        var step = new ActionStep();
        command.Steps.Add(step);
        StepsGrid.SelectedItem = step;
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } command && StepsGrid.SelectedItem is ActionStep step)
        {
            CommitEdits();
            command.Steps.Remove(step);
        }
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } command || StepsGrid.SelectedItem is not ActionStep step) return;
        CommitEdits();
        var index = command.Steps.IndexOf(step);
        if (index > 0) command.Steps.Move(index, index - 1);
        StepsGrid.SelectedItem = step;
    }

    private async void TestCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } command) return;
        CommitEdits();
        Log($"Testing \"{command.Phrase}\"");
        await Run(command);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll();

    private void SaveAll()
    {
        CommitEdits();
        try
        {
            CommandStore.Save(_data);
            Log("Saved");
            RestartIfListening();
        }
        catch (Exception ex)
        {
            Log("Save failed: " + ex.Message);
        }
    }

    /// <summary>Pushes any in-progress DataGrid cell edit into the model.</summary>
    private void CommitEdits()
    {
        StepsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private void CommandList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateEditorState();

    private void UpdateEditorState()
    {
        var hasSelection = Selected != null;
        Editor.Visibility = hasSelection ? Visibility.Visible : Visibility.Hidden;
        EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- AI Builder -------------------------------------------------------------------

    private VoiceCommand? _draft;
    private CancellationTokenSource? _generateCts;

    private string GetApiKey()
    {
        var saved = SecretStore.Unprotect(_data.Settings.EncryptedApiKey);
        return saved.Length > 0 ? saved : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
    }

    private void UpdateApiKeyStatus()
    {
        if (SecretStore.Unprotect(_data.Settings.EncryptedApiKey).Length > 0)
            ApiKeyStatus.Text = "✓ Key saved (encrypted for your Windows account). Paste a new one to replace it.";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            ApiKeyStatus.Text = "✓ Using the ANTHROPIC_API_KEY environment variable.";
        else
            ApiKeyStatus.Text = "No key yet. Create one at console.anthropic.com → API Keys, paste it above, and press Save key.";
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        _data.Settings.EncryptedApiKey = SecretStore.Protect(key);
        ApiKeyBox.Clear();
        SaveAll();
        UpdateApiKeyStatus();
        Log(key.Length > 0 ? "API key saved" : "API key removed");
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        // Second click while running = cancel.
        if (_generateCts != null) { _generateCts.Cancel(); return; }

        var description = AiPrompt.Text.Trim();
        if (description.Length == 0) { AiStatus.Text = "Describe what you want the command to do first."; return; }
        var apiKey = GetApiKey();
        if (apiKey.Length == 0) { AiStatus.Text = "Add your Anthropic API key below first."; return; }

        _generateCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        GenerateButton.Content = "Cancel";
        GenerateProgress.Visibility = Visibility.Visible;
        AiStatus.Text = "Thinking…";
        try
        {
            var draft = await AiCommandBuilder.GenerateAsync(apiKey, description, _generateCts.Token);
            ShowDraft(draft);
            AiStatus.Text = draft.Steps.Count == 0
                ? "Couldn't turn that into steps — see the note on the right."
                : "Review the steps, press Test to try them, then add it to your commands.";
            Log($"AI suggested \"{draft.Phrase}\" ({draft.Steps.Count} step(s))");
        }
        catch (OperationCanceledException)
        {
            AiStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Error: " + ex.Message;
            Log("AI builder failed: " + ex.Message);
        }
        finally
        {
            _generateCts.Dispose();
            _generateCts = null;
            GenerateButton.Content = "✨  Generate command";
            GenerateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowDraft(AiDraft draft)
    {
        _draft = new VoiceCommand { Phrase = draft.Phrase };
        foreach (var step in draft.Steps) _draft.Steps.Add(step);
        AiPreview.DataContext = _draft;
        AiExplanation.Text = draft.Explanation;
        AiPreview.Visibility = Visibility.Visible;
        AiEmptyState.Visibility = Visibility.Collapsed;
    }

    private void ClearDraft()
    {
        _draft = null;
        AiPreview.DataContext = null;
        AiPreview.Visibility = Visibility.Hidden;
        AiEmptyState.Visibility = Visibility.Visible;
    }

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        AiStepsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        Log($"Testing AI suggestion \"{_draft.Phrase}\"");
        await Run(_draft);
    }

    private void AiDiscard_Click(object sender, RoutedEventArgs e) => ClearDraft();

    private void AiAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        AiStepsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(_draft.Phrase)) { AiStatus.Text = "Give the command a phrase first."; return; }
        if (_draft.Steps.Count == 0) { AiStatus.Text = "This suggestion has no steps to save."; return; }

        var command = _draft;
        ClearDraft();
        AiPrompt.Clear();
        AiStatus.Text = "";
        _data.Commands.Add(command);
        SaveAll();
        MainTabs.SelectedIndex = 0;
        CommandList.SelectedItem = command;
        Log($"Added \"{command.Phrase}\" from AI Builder");
    }

    // ---- Misc -------------------------------------------------------------------------

    private void Log(string message)
    {
        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > 200) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitEdits();
        try { CommandStore.Save(_data); } catch { /* don't block closing */ }
        _listener.Dispose();
        base.OnClosing(e);
    }
}
