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
        ListenButton.Content = listening ? "Stop listening" : "Start listening";
        StatusText.Text = listening ? "Listening…" : "Not listening";
        StatusDot.Fill = listening ? Brushes.LimeGreen : Brushes.Gray;
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

    private void UpdateEditorState() => Editor.IsEnabled = Selected != null;

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
