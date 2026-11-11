using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shmoice.Models;
using Shmoice.Services;

namespace Shmoice;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private readonly SpeechListener _listener = new();
    private readonly System.Windows.Threading.DispatcherTimer _settingsDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(700) };

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

        // Always listening: start as soon as the window is up, and restart when settings change.
        _settingsDebounce.Tick += SettingsDebounce_Tick;
        _data.Settings.PropertyChanged += Settings_PropertyChanged;
        Loaded += (_, _) => RefreshListening();
    }

    private VoiceCommand? Selected => CommandList.SelectedItem as VoiceCommand;

    // ---- Listening --------------------------------------------------------------------

    /// <summary>The mic button only picks the input; listening itself is always on.</summary>
    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        List<AudioInput> inputs;
        try { inputs = AudioInputs.List(); }
        catch (Exception ex)
        {
            Log("Could not list microphones: " + ex.Message);
            return;
        }

        var chosen = InputDeviceDialog.Ask(this, inputs, _data.Settings.InputDeviceName);
        if (chosen == null) return;
        _data.Settings.InputDeviceName = chosen.IsDefault ? "" : chosen.Name;
        CommandStore.Save(_data);
        Log($"Microphone set to {chosen.Name}");
        RefreshListening();
    }

    /// <summary>The saved microphone, or the Windows default if it isn't connected.</summary>
    private AudioInput SavedInput()
    {
        var saved = _data.Settings.InputDeviceName;
        if (saved.Length == 0) return AudioInput.WindowsDefault;
        try
        {
            if (AudioInputs.List().FirstOrDefault(i => i.Name == saved) is { } match) return match;
        }
        catch { /* fall through to the default */ }
        Log($"\"{saved}\" isn't connected, using the Windows default microphone");
        return AudioInput.WindowsDefault;
    }

    /// <summary>
    /// (Re)starts the recognizer with the current commands, wake word and mic. The grammar is
    /// fixed when it starts, so this runs at launch and after anything that changes it.
    /// </summary>
    private void RefreshListening()
    {
        var wakeWord = _data.Settings.WakeWord.Trim();
        if (wakeWord.Length == 0)
        {
            _listener.Stop();
            SetStatus(false, "Set a wake word to listen");
            return;
        }
        if (!EnabledPhrases().Any())
        {
            _listener.Stop();
            SetStatus(false, "No commands to listen for");
            return;
        }

        var input = SavedInput();
        try
        {
            _listener.Start(EnabledPhrases(), wakeWord, _data.Settings.ConfidenceThreshold, input);
            SetStatus(true, $"Listening for \"{wakeWord}\"");
            Log($"Listening on {input.Name} for {EnabledPhrases().Count()} command(s), say \"{wakeWord}\" first");
        }
        catch (Exception ex)
        {
            SetStatus(false, "Not listening");
            Log("Could not start listening: " + ex.Message);
        }
    }

    /// <summary>Wake word / confidence edits restart listening once typing pauses.</summary>
    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.WakeWord) or nameof(AppSettings.ConfidenceThreshold))) return;
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    private void SettingsDebounce_Tick(object? sender, EventArgs e)
    {
        _settingsDebounce.Stop();
        try { CommandStore.Save(_data); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
        RefreshListening();
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

    private void SetStatus(bool listening, string text)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(listening ? "ListeningBrush" : "IdleBrush");
        MicButtonText.Text = _listener.Input?.Name ?? (_data.Settings.InputDeviceName.Length > 0
            ? _data.Settings.InputDeviceName
            : AudioInput.WindowsDefault.Name);
    }

    // ---- Command editing --------------------------------------------------------------

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = new VoiceCommand { Phrase = "new command", Steps = { new ActionStep() } };
        _data.Commands.Insert(0, command); // newest first
        CommandList.SelectedItem = command;
        CommandList.ScrollIntoView(command);
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } command) DeleteCommand(command);
    }

    private void CommandList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected is { } command)
        {
            DeleteCommand(command);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Selected is { } toRename)
        {
            Rename(toRename);
            e.Handled = true;
        }
    }

    private void DeleteCommand(VoiceCommand command)
    {
        if (_data.Settings.ConfirmDelete)
        {
            var steps = command.Steps.Count == 1 ? "1 step" : $"{command.Steps.Count} steps";
            var confirmed = ConfirmDialog.Ask(this, "Delete this command?",
                $"“{command.Phrase}” and its {steps} will be removed. This can't be undone.",
                "Delete", out var dontAskAgain);
            if (!confirmed) return;
            if (dontAskAgain) _data.Settings.ConfirmDelete = false;
        }

        var index = _data.Commands.IndexOf(command);
        _data.Commands.Remove(command);
        // Keep a selection so the editor doesn't go blank: pick the item that took its place.
        if (_data.Commands.Count > 0)
            CommandList.SelectedIndex = Math.Min(index, _data.Commands.Count - 1);
        SaveAll();
        Log($"Deleted \"{command.Phrase}\"");
    }

    private void Rename(VoiceCommand command)
    {
        MainTabs.SelectedIndex = 0;
        CommandList.SelectedItem = command;
        // Wait for the editor to bind to the newly selected command before focusing.
        Dispatcher.BeginInvoke(() => { PhraseBox.Focus(); PhraseBox.SelectAll(); },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---- Right-click menu -------------------------------------------------------------

    /// <summary>Select the item being right-clicked so the editor shows what the menu acts on.</summary>
    private void CommandItem_RightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) item.IsSelected = true;
    }

    private void CommandMenu_Opened(object sender, RoutedEventArgs e)
    {
        // "Ask before deleting" is an app setting, not part of the command, so set it when the menu opens.
        if (sender is ContextMenu menu)
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "AskBeforeDelete")))
                item.IsChecked = _data.Settings.ConfirmDelete;
    }

    private static VoiceCommand? MenuTarget(object sender) => (sender as FrameworkElement)?.DataContext as VoiceCommand;

    private async void MenuTest_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is not { } command) return;
        Log($"Testing \"{command.Phrase}\"");
        await Run(command);
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } command) Rename(command);
    }

    private void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is not { } original) return;
        var copy = new VoiceCommand { Phrase = original.Phrase + " copy", Enabled = original.Enabled };
        foreach (var s in original.Steps)
            copy.Steps.Add(new ActionStep { Type = s.Type, Parameter = s.Parameter, Repeat = s.Repeat });
        _data.Commands.Insert(_data.Commands.IndexOf(original), copy); // just above the original
        SaveAll();
        Rename(copy); // a duplicate almost always needs a new phrase
        Log($"Duplicated \"{original.Phrase}\"");
    }

    private void MenuMoveTop_Click(object sender, RoutedEventArgs e) => MoveCommand(MenuTarget(sender), 0);

    private void MenuMoveBottom_Click(object sender, RoutedEventArgs e) => MoveCommand(MenuTarget(sender), _data.Commands.Count - 1);

    private void MoveCommand(VoiceCommand? command, int newIndex)
    {
        if (command == null) return;
        var index = _data.Commands.IndexOf(command);
        if (index < 0 || index == newIndex) return;
        _data.Commands.Move(index, newIndex);
        CommandList.SelectedItem = command;
        CommandList.ScrollIntoView(command);
        SaveAll();
    }

    private void MenuAskBeforeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        _data.Settings.ConfirmDelete = item.IsChecked;
        SaveAll();
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget(sender) is { } command) DeleteCommand(command);
    }

    /// <summary>The dashed "Add a step" row; its DataContext is the command it belongs to (saved command or AI draft).</summary>
    private void AddStepInline_Click(object sender, RoutedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as VoiceCommand)?.Steps.Add(new ActionStep());

    // The per-row buttons below are shared by the Commands editor and the AI preview.
    // Each button's DataContext is its step; find whichever command owns it.

    private (ActionStep step, System.Collections.ObjectModel.ObservableCollection<ActionStep> owner)? StepFrom(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActionStep step) return null;
        foreach (var steps in new[] { Selected?.Steps, _draft?.Steps })
            if (steps != null && steps.Contains(step)) return (step, steps);
        return null;
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepFrom(sender) is var (step, owner)) owner.Remove(step);
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e)
    {
        if (StepFrom(sender) is not var (step, owner)) return;
        var index = owner.IndexOf(step);
        if (index > 0) owner.Move(index, index - 1);
    }

    private void RepeatUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActionStep step) step.Repeat++;
    }

    private void RepeatDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActionStep step) step.Repeat--;
    }

    private async void TestCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } command) return;
        Log($"Testing \"{command.Phrase}\"");
        await Run(command);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll();

    private void SaveAll()
    {
        try
        {
            CommandStore.Save(_data);
            Log("Saved");
            RefreshListening();
        }
        catch (Exception ex)
        {
            Log("Save failed: " + ex.Message);
        }
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
        Log($"Testing AI suggestion \"{_draft.Phrase}\"");
        await Run(_draft);
    }

    private void AiDiscard_Click(object sender, RoutedEventArgs e) => ClearDraft();

    private void AiAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        if (string.IsNullOrWhiteSpace(_draft.Phrase)) { AiStatus.Text = "Give the command a phrase first."; return; }
        if (_draft.Steps.Count == 0) { AiStatus.Text = "This suggestion has no steps to save."; return; }

        var command = _draft;
        ClearDraft();
        AiPrompt.Clear();
        AiStatus.Text = "";
        _data.Commands.Insert(0, command); // newest first
        SaveAll();
        MainTabs.SelectedIndex = 0;
        CommandList.SelectedItem = command;
        CommandList.ScrollIntoView(command);
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
        try { CommandStore.Save(_data); } catch { /* don't block closing */ }
        _listener.Dispose();
        base.OnClosing(e);
    }
}
