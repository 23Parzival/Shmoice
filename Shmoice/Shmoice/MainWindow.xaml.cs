using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shmoice.Models;
using Shmoice.Services;
using Shmoice.Tabs;

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
        // Start big, but never bigger than the screen (e.g. on a small laptop).
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.94));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.94));
        _data = CommandStore.Load();
        DataContext = _data;
        ThemeManager.Apply(_data.Settings); // light / dark and accent color, before anything is shown
        // Switching light / dark rebuilds the tab headers, which drops the room made for ‹ ›; put it back.
        ThemeManager.Applied += () => Dispatcher.BeginInvoke(MakeRoomForNavButtons, System.Windows.Threading.DispatcherPriority.Loaded);
        // "Runs in" picker: "Any app" (Guid.Empty, see NullGuidConverter) followed by every profile.
        ProfileNameConverter.Data = _data;
        ProfileCombo.ItemsSource = new System.Windows.Data.CompositeCollection
        {
            new Profile { Id = Guid.Empty, Name = "Any app" },
            new System.Windows.Data.CollectionContainer { Collection = _data.Profiles },
        };
        foreach (var tab in new ITab[] { ProfilesTabControl, SchedulesTabControl, HistoryTabControl, VoiceTestTabControl, HelpTabControl })
            tab.Attach(this);
        SettingsTabControl.Host = this;

        _listener.Recognized += (phrase, confidence) => Dispatcher.Invoke(() => OnHeard(phrase, confidence));
        _listener.LowConfidence += (phrase, confidence) => Dispatcher.Invoke(() => OnLowConfidence(phrase, confidence));
        ActionExecutor.ListenForWords = ListenForWordsAsync;
        // Commands run on a background thread, so these hop to the UI thread to read the data.
        ActionExecutor.FindCommand = phrase => Dispatcher.Invoke(() => FindCommandByPhrase(phrase));
        ActionExecutor.GetApiKey = () => Dispatcher.Invoke(GetApiKey);
        ActionExecutor.Log = message => Dispatcher.BeginInvoke(() => Log(message));

        if (_data.Commands.Count > 0) CommandList.SelectedIndex = 0;
        UpdateEditorState();
        UpdateApiKeyStatus();
        Log($"Commands loaded from {CommandStore.FilePath}");

        // Always listening: start as soon as the window is up, and restart when settings change.
        _settingsDebounce.Tick += SettingsDebounce_Tick;
        _data.Settings.PropertyChanged += Settings_PropertyChanged;
        Loaded += (_, _) =>
        {
            MakeRoomForNavButtons();
            RefreshListening();
            RecordNavigation(); // the starting point for Back
            _editWatch.Tick += EditWatch_Tick;
            _editWatch.Start();
            UpdateScheduleStatus();
            _scheduleTimer.Tick += ScheduleTimer_Tick;
            _scheduleTimer.Start();
        };
    }

    private VoiceCommand? Selected => CommandList.SelectedItem as VoiceCommand;

    // ---- Listening --------------------------------------------------------------------

    /// <summary>The mic button only picks the input; listening itself is always on.</summary>
    private void MicButton_Click(object sender, RoutedEventArgs e) => ChooseMicrophone();

    /// <summary>The microphone being listened to, as shown on the mic button.</summary>
    public string MicrophoneName => MicButtonText.Text;

    /// <summary>Opens the microphone picker (also used by the Settings tab).</summary>
    public void ChooseMicrophone()
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
    /// <summary>The microphone being listened to right now (or the saved one if not listening).</summary>
    public AudioInput CurrentInput() => _listener.Input ?? SavedInput();

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

    private bool _listeningSettingsChanged;

    /// <summary>
    /// Settings in the header save once you stop changing them. Wake word / confidence also restart
    /// listening; the chime volume doesn't need to.
    /// </summary>
    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.WakeWord) or nameof(AppSettings.ConfidenceThreshold))
            _listeningSettingsChanged = true;
        if (e.PropertyName is nameof(AppSettings.AccentColor) or nameof(AppSettings.Theme))
            ThemeManager.Apply(_data.Settings); // recolor right away; saving can wait
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    private void SettingsDebounce_Tick(object? sender, EventArgs e)
    {
        _settingsDebounce.Stop();
        try { CommandStore.Save(_data); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
        if (!_listeningSettingsChanged) return;
        _listeningSettingsChanged = false;
        RefreshListening();
    }


    private IEnumerable<string> EnabledPhrases() =>
        _data.Commands.Where(c => c.Enabled && c.Steps.Count > 0).Select(c => c.Phrase);

    // ---- Running commands ---------------------------------------------------------------

    /// <summary>Saved list of everything that ran (History tab).</summary>
    public HistoryStore History { get; } = HistoryStore.Load();

    public AppData Data => _data;

    /// <summary>Voice test's practice mode: show what was heard, but don't run anything.</summary>
    public bool PracticeMode { get; set; }

    /// <summary>Something was heard (for the Voice test tab): phrase, confidence, and what Shmoice did about it.</summary>
    public event Action<string, float, string>? Heard;

    private async void OnHeard(string phrase, float confidence)
    {
        var matches = _data.Commands
            .Where(c => c.Enabled && string.Equals(c.Phrase.Trim(), phrase, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var app = ForegroundApp.ProcessName();
        var (toRun, skipReason) = ApplyProfiles(matches, app);

        if (PracticeMode)
        {
            var outcome = toRun.Count > 0 ? $"Would run \"{toRun[0].Phrase}\"" + (toRun.Count > 1 ? $" (+{toRun.Count - 1} more)" : "")
                : skipReason ?? "No matching command";
            Heard?.Invoke(phrase, confidence, outcome + " — practice mode, nothing ran");
            return;
        }

        Log($"Heard \"{phrase}\" ({confidence:P0})");
        if (toRun.Count == 0 && skipReason != null)
        {
            Log(skipReason);
            foreach (var command in matches)
                History.Add(new HistoryItem { CommandId = command.Id, Phrase = command.Phrase, Source = RunSource.Voice, Result = RunResult.Skipped, Confidence = confidence, Detail = skipReason });
        }
        Heard?.Invoke(phrase, confidence, toRun.Count > 0 ? "Ran" : skipReason ?? "No matching command");
        foreach (var command in toRun) await RunCommandAsync(command, RunSource.Voice, confidence);
    }

    private void OnLowConfidence(string phrase, float confidence)
    {
        var reason = $"below your {_data.Settings.ConfidenceThreshold:P0} minimum";
        Heard?.Invoke(phrase, confidence, "Ignored: " + reason);
        if (PracticeMode) return;
        Log($"Ignored \"{phrase}\" ({confidence:P0} < threshold)");
        var command = _data.Commands.FirstOrDefault(c => string.Equals(c.Phrase.Trim(), phrase, StringComparison.OrdinalIgnoreCase));
        History.Add(new HistoryItem { CommandId = command?.Id, Phrase = phrase, Source = RunSource.Voice, Result = RunResult.Ignored, Confidence = confidence, Detail = "Not sure enough: " + reason });
    }

    private (List<VoiceCommand> toRun, string? skipReason) ApplyProfiles(List<VoiceCommand> matches, string? app) =>
        ProfileRules.Choose(matches, _data.Profiles, app);

    /// <summary>Runs a command and records it in History. Every way of running a command comes through here.</summary>
    public async Task RunCommandAsync(VoiceCommand command, RunSource source, float? confidence = null)
    {
        var item = new HistoryItem { CommandId = command.Id, Phrase = command.Phrase, Source = source, Confidence = confidence };
        try
        {
            item.Heard = await ActionExecutor.RunAsync(command);
            item.Result = RunResult.Ran;
        }
        catch (Exception ex)
        {
            item.Result = RunResult.Failed;
            item.Detail = ex.Message;
            Log($"\"{command.Phrase}\" failed: {ex.Message}");
        }
        if (source != RunSource.VoiceTest) History.Add(item);
    }

    private Task Run(VoiceCommand command) => RunCommandAsync(command, RunSource.Test);

    /// <summary>
    /// For "Run another command" steps. When several commands share the phrase (one per profile), the
    /// one that would run if you said it now is used; otherwise the first with that phrase.
    /// </summary>
    private VoiceCommand? FindCommandByPhrase(string phrase)
    {
        var matches = _data.Commands
            .Where(c => string.Equals(c.Phrase.Trim(), phrase.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count <= 1) return matches.FirstOrDefault();
        var (toRun, _) = ApplyProfiles(matches.Where(c => c.Enabled).ToList(), ForegroundApp.ProcessName());
        return toRun.FirstOrDefault() ?? matches[0];
    }

    // ---- Schedules ----------------------------------------------------------------------

    private readonly Scheduler _scheduler = new();
    private readonly System.Windows.Threading.DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private async void ScheduleTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var schedule in _data.Schedules.ToList())
        {
            if (!_scheduler.IsDue(schedule, now)) continue;
            schedule.LastRun = now; // before running, so a slow command can't make it run twice
            SaveSoon();
            if (_data.Commands.FirstOrDefault(c => c.Id == schedule.CommandId) is not { } command) continue;
            Log($"Schedule: running \"{command.Phrase}\"");
            await RunCommandAsync(command, RunSource.Schedule);
        }
        UpdateScheduleStatus();
    }

    /// <summary>Refreshes the "Next: today 09:00" text on every schedule.</summary>
    public void UpdateScheduleStatus()
    {
        var now = DateTime.Now;
        foreach (var schedule in _data.Schedules)
        {
            schedule.Status = _scheduler.Describe(schedule, _data.Commands, now);
            schedule.Title = _data.Commands.FirstOrDefault(c => c.Id == schedule.CommandId) is { } c ? $"“{c.Phrase}”" : "No command chosen";
        }
    }

    /// <summary>
    /// For "Listen for words" steps: chime, show "Say it now…" in the status pill, and wait for the
    /// user to speak. Runs on the command's background thread.
    /// </summary>
    private async Task<string> ListenForWordsAsync(TimeSpan startTimeout)
    {
        string? previousText = null;
        Brush? previousDot = null;
        await Dispatcher.InvokeAsync(() =>
        {
            previousText = StatusText.Text;
            previousDot = StatusDot.Fill;
            StatusText.Text = "Say it now…";
            StatusDot.Fill = (Brush)FindResource("AppAccentBrush");
            Log("Listening for words…");
        });
        try
        {
            Chime.Play(_data.Settings.ChimeVolume);
            await Task.Delay(350); // let the chime finish so it isn't heard as speech
            var words = await _listener.ListenForWordsAsync(startTimeout);
            await Dispatcher.InvokeAsync(() => Log(words.Length > 0 ? $"Heard \"{words}\"" : "Didn't hear anything"));
            return words;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = previousText;
                StatusDot.Fill = previousDot;
            });
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
        ClearSearch(); // so the new command is visible
        // Save edits to the command you were on first, so the log reads sensibly.
        if (CommandStore.HasUnsavedChanges(_data)) SaveAll();
        var command = new VoiceCommand { Phrase = "new command", Steps = { new ActionStep() } };
        _data.Commands.Insert(0, command); // newest first
        _suppressAutoSave = true;
        try { CommandList.SelectedItem = command; }
        finally { _suppressAutoSave = false; }
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

        // Index in the list as shown (it may be filtered by the search box).
        var index = CommandList.Items.IndexOf(command);
        _suppressAutoSave = true;
        try
        {
            _data.Commands.Remove(command);
            // Keep a selection so the editor doesn't go blank: pick the item that took its place.
            if (CommandList.Items.Count > 0)
                CommandList.SelectedIndex = Math.Clamp(index, 0, CommandList.Items.Count - 1);
        }
        finally { _suppressAutoSave = false; }
        SaveAll();
        UpdateNavButtons(); // it may have been the only place to go back to
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
        var copy = new VoiceCommand { Phrase = original.Phrase + " copy", Enabled = original.Enabled, ProfileId = original.ProfileId };
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

    /// <summary>Save now without restarting listening (profiles and schedules don't change what Shmoice listens for).</summary>
    public void SaveQuietly()
    {
        try { CommandStore.Save(_data); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
    }

    /// <summary>Save shortly, once changes pause (for edits in the Profiles and Schedules tabs).</summary>
    public void SaveSoon()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    public void SaveAll(string message = "Saved")
    {
        try
        {
            CommandStore.Save(_data);
            Log(message);
            RefreshListening();
        }
        catch (Exception ex)
        {
            Log("Save failed: " + ex.Message);
        }
    }

    private bool _suppressAutoSave;

    /// <summary>Switching to another command saves any edits made to the one you were on.</summary>
    private void CommandList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateEditorState();
        RecordNavigation();
        if (_suppressAutoSave || e.RemovedItems.Count == 0 || !CommandStore.HasUnsavedChanges(_data)) return;
        var previous = e.RemovedItems[0] as VoiceCommand;
        SaveAll(previous != null ? $"Saved changes to \"{previous.Phrase}\"" : "Saved changes");
    }

    // ---- Back / forward --------------------------------------------------------------

    // One timeline for both where you were and what you changed, like a browser crossed with undo:
    // every entry is a place (tab + open command) plus a snapshot of all commands at that moment.
    // Back over a place switches to it; back over an edit (adding a step, renaming, deleting a
    // command…) restores the commands as they were. Making a change after going back drops the
    // "forward" entries, like a browser.

    private sealed record HistoryEntry(int Tab, Guid? CommandId, string Snapshot);

    private readonly List<HistoryEntry> _history = new();
    private int _historyIndex = -1;
    private bool _navigating;
    private string? _pendingSnapshot; // a change seen on the last tick, waiting to settle
    private readonly System.Windows.Threading.DispatcherTimer _editWatch = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private HistoryEntry? Current => _historyIndex >= 0 ? _history[_historyIndex] : null;

    private string Snapshot() => CommandStore.SnapshotCommands(_data.Commands);

    private void AddEntry(int tab, Guid? commandId, string snapshot)
    {
        // Skip in-between states while the window loads or the list refreshes (no tab, or Commands with nothing open).
        if (_navigating || tab < 0 || (tab == 0 && commandId == null)) return;
        if (Current is { } c && c.Tab == tab && c.CommandId == commandId && c.Snapshot == snapshot) return;
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new HistoryEntry(tab, commandId, snapshot));
        if (_history.Count > 200) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        UpdateNavButtons();
    }

    /// <summary>If the commands changed since the last entry, record that change where it was made.</summary>
    private void FlushEdits()
    {
        _pendingSnapshot = null;
        if (Current is not { } c || _navigating) return;
        var snapshot = Snapshot();
        if (snapshot == c.Snapshot) return;
        // If the change deleted the command you were on, record it where you are now instead.
        if (c.CommandId is { } id && _data.Commands.All(x => x.Id != id))
            AddEntry(MainTabs.SelectedIndex, MainTabs.SelectedIndex == 0 ? Selected?.Id : null, snapshot);
        else
            AddEntry(c.Tab, c.CommandId, snapshot);
    }

    /// <summary>You opened another command or tab.</summary>
    private void RecordNavigation()
    {
        if (_navigating || _data == null) return;
        FlushEdits();
        AddEntry(MainTabs.SelectedIndex, MainTabs.SelectedIndex == 0 ? Selected?.Id : null, Snapshot());
    }

    /// <summary>
    /// Edits are picked up once they've settled for a moment, so typing a phrase becomes one step
    /// back rather than one per letter.
    /// </summary>
    private void EditWatch_Tick(object? sender, EventArgs e)
    {
        if (_navigating || Current is not { } c) return;
        var snapshot = Snapshot();
        if (snapshot == c.Snapshot) { _pendingSnapshot = null; return; }
        // Back can undo this right away (it records the change first), so don't make you wait for it.
        BackButton.IsEnabled = true;
        ForwardButton.IsEnabled = false; // a new change replaces anything you could go forward to
        if (snapshot != _pendingSnapshot) { _pendingSnapshot = snapshot; _stableTicks = 0; return; } // still changing
        if (++_stableTicks >= 3) FlushEdits(); // unchanged for ~0.6 s
    }

    private int _stableTicks;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from lists inside the tabs; only react to the tabs themselves.
        if (e.Source != MainTabs) return;
        RecordNavigation();
    }

    /// <summary>The nearest entry in that direction that's different from where you are now.</summary>
    private int FindTarget(int direction)
    {
        if (Current is not { } c) return -1;
        for (var i = _historyIndex + direction; i >= 0 && i < _history.Count; i += direction)
            if (_history[i] != c) return i;
        return -1;
    }

    private void Navigate(int direction)
    {
        FlushEdits(); // an edit you just made (e.g. a step added by mistake) is the first thing Back undoes
        _stableTicks = 0;
        var target = FindTarget(direction);
        if (target < 0) return;
        var entry = _history[target];
        var restore = entry.Snapshot != Snapshot();
        var phrasesBefore = string.Join("\n", EnabledPhrases());
        _historyIndex = target;
        _navigating = true;
        _suppressAutoSave = true;
        try
        {
            if (restore)
            {
                _data.Commands.Clear();
                foreach (var command in CommandStore.RestoreCommands(entry.Snapshot)) _data.Commands.Add(command);
            }
            MainTabs.SelectedIndex = entry.Tab;
            if (entry.CommandId is { } id && _data.Commands.FirstOrDefault(c => c.Id == id) is { } open)
            {
                if (!CommandList.Items.Contains(open)) ClearSearch(); // hidden by the search box
                CommandList.SelectedItem = open;
                CommandList.ScrollIntoView(open);
            }
        }
        finally
        {
            _navigating = false;
            _suppressAutoSave = false;
        }

        if (restore)
        {
            // Keep the file and the listener in step with what's on screen.
            try { CommandStore.Save(_data); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
            if (string.Join("\n", EnabledPhrases()) != phrasesBefore) RefreshListening();
            Log(direction < 0 ? "Undid a change" : "Redid a change");
        }
        UpdateNavButtons();
    }

    /// <summary>
    /// The back / forward buttons sit where the tab headers start, so shift the row of tab headers
    /// right by their width. (The theme ignores a margin set on the first tab itself.)
    /// </summary>
    private void MakeRoomForNavButtons()
    {
        if (FindChild<System.Windows.Controls.Primitives.TabPanel>(MainTabs) is { } headers)
            headers.Margin = new Thickness(80, headers.Margin.Top, headers.Margin.Right, headers.Margin.Bottom);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void UpdateNavButtons()
    {
        if (BackButton == null || _data == null) return; // still loading
        BackButton.IsEnabled = FindTarget(-1) >= 0;
        ForwardButton.IsEnabled = FindTarget(1) >= 0;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Forward_Click(object sender, RoutedEventArgs e) => Navigate(1);

    /// <summary>Alt+Left / Alt+Right, like a browser.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System && e.SystemKey is Key.Left or Key.Right)
        {
            Navigate(e.SystemKey == Key.Left ? -1 : 1);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>The back / forward buttons on the side of many mice.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            Navigate(e.ChangedButton == MouseButton.XButton1 ? -1 : 1);
            e.Handled = true;
        }
        base.OnPreviewMouseDown(e);
    }

    // ---- Search -----------------------------------------------------------------------

    private void CommandSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var words = CommandSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_data.Commands);
        view.Filter = words.Length == 0 ? null : item => item is VoiceCommand c && Matches(c, words);

        CommandSearchHint.Visibility = CommandSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCommandMatches.Visibility = !view.IsEmpty || _data.Commands.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // If the open command was filtered out, show the first match instead of a blank editor.
        if (Selected == null && !view.IsEmpty) CommandList.SelectedIndex = 0;
    }

    /// <summary>Every word must appear in the phrase or in a step (its action name or parameter).</summary>
    private static bool Matches(VoiceCommand command, string[] words)
    {
        var text = command.Phrase + " " + string.Join(" ", command.Steps.Select(s =>
        {
            var info = ActionCatalog.Get(s.Type);
            return $"{info.Label} {info.Category} {s.Parameter}";
        }));
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void CommandSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClearSearch();
        e.Handled = true;
    }

    private void ClearSearch() => CommandSearch.Text = "";

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
        ClearSearch(); // so the new command is visible
        _data.Commands.Insert(0, command); // newest first
        SaveAll();
        MainTabs.SelectedIndex = 0;
        CommandList.SelectedItem = command;
        CommandList.ScrollIntoView(command);
        Log($"Added \"{command.Phrase}\" from AI Builder");
    }

    // ---- Misc -------------------------------------------------------------------------

    public void Log(string message)
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
