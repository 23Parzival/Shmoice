using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shmoice.Services;

namespace Shmoice.Tabs;

/// <summary>All of Shmoice's settings in one place. Bound settings save automatically (see MainWindow).</summary>
public partial class SettingsTab : UserControl
{
    public MainWindow? Host { get; set; }

    public SettingsTab()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = $"Shmoice {version?.ToString(3) ?? ""}";
        DataFolderText.Text = Path.GetDirectoryName(CommandStore.FilePath);
        // Refresh things that can change outside this tab each time it's shown.
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private void Refresh()
    {
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        MicText.Text = Host?.MicrophoneName ?? "";
        RefreshAppearance();
        RefreshAi();
    }

    // ---- AI --------------------------------------------------------------------------------

    private const int AiSectionIndex = 4; // position in the sidebar

    private bool _updatingAi; // true while filling the controls from the saved settings

    /// <summary>Opens the AI section (the AI Builder tab's "Change" button).</summary>
    public void ShowAiSection() => Sections.SelectedIndex = AiSectionIndex;

    private AiProvider CurrentProvider => AiProviders.Get(Host?.Data.Settings.AiProvider);

    private Models.AiProviderConfig? CurrentConfig =>
        Host == null ? null : AiProviders.ConfigFor(Host.Data.Settings, CurrentProvider);

    /// <summary>Shows the chosen service and what's saved for it.</summary>
    private void RefreshAi()
    {
        if (Host == null || CurrentConfig is not { } config) return;
        var provider = CurrentProvider;
        _updatingAi = true;
        try
        {
            if (AiProviderCombo.ItemsSource == null) AiProviderCombo.ItemsSource = AiProviders.All;
            AiProviderCombo.SelectedItem = provider;
            AiNote.Text = provider.Note;

            var openAi = provider.Kind == AiApiKind.OpenAiCompatible;
            AiAddressRow.Visibility = openAi ? Visibility.Visible : Visibility.Collapsed;
            AiAddress.Text = config.BaseUrl.Length > 0 ? config.BaseUrl : provider.DefaultBaseUrl;
            AiAddressHint.Text = provider.Id == AiProviders.CustomId
                ? "The service's address up to and including /v1, e.g. http://localhost:8080/v1 or http://192.168.1.20:5000/v1."
                : "Filled in for you. Change it only if yours is somewhere else, e.g. on another PC.";

            FindModelsButton.Visibility = openAi ? Visibility.Visible : Visibility.Collapsed;
            AiModel.ItemsSource = provider.SuggestedModels;
            AiModel.Text = config.Model;
            AiModelHint.Text = provider.DefaultModel.Length > 0
                ? $"Leave empty to use {provider.DefaultModel}."
                : "Press Find models to choose from what the service has, or type the model's name.";

            AiKey.Clear();
            RefreshAiKeyStatus();
            AiTestStatus.Text = "Sends a one-line hello to check the address, key and model all work.";
        }
        finally { _updatingAi = false; }
    }

    private void RefreshAiKeyStatus()
    {
        if (CurrentConfig is not { } config) return;
        var provider = CurrentProvider;
        if (SecretStore.Unprotect(config.EncryptedApiKey).Length > 0)
            AiKeyStatus.Text = "✓ Key saved (encrypted for your Windows account). Paste a new one to replace it, or save an empty box to remove it.";
        else if (AiProviders.ApiKey(provider, config).Length > 0)
            AiKeyStatus.Text = "✓ Using the ANTHROPIC_API_KEY environment variable.";
        else
            AiKeyStatus.Text = provider.Local
                ? "Not needed for most models running on your own PC. Leave it empty unless yours asks for one."
                : "No key yet. Paste it here and press Save key.";
    }

    /// <summary>Something in this section changed: save it and update the summary on the AI Builder tab.</summary>
    private void AiChanged()
    {
        Host?.SaveQuietly();
        Host?.UpdateAiStatus();
    }

    private void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAi || Host == null || AiProviderCombo.SelectedItem is not AiProvider provider) return;
        Host.Data.Settings.AiProvider = provider.Id;
        RefreshAi();
        AiChanged();
        e.Handled = true; // don't let it look like the sidebar selection changed
    }

    private void AiAddress_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_updatingAi || CurrentConfig is not { } config) return;
        var provider = CurrentProvider;
        var typed = AiAddress.Text.Trim();
        var address = AiProviders.NormalizeBaseUrl(typed);
        if (typed.Length > 0 && address.Length == 0)
        {
            AiAddressHint.Text = $"“{typed}” isn't a web address. Use something like http://localhost:11434/v1.";
            return;
        }
        // Saved empty when it's just the default, so a future default change is picked up.
        var toSave = address == provider.DefaultBaseUrl ? "" : address;
        if (toSave == config.BaseUrl) { AiAddress.Text = address.Length > 0 ? address : provider.DefaultBaseUrl; return; }
        config.BaseUrl = toSave;
        RefreshAi();
        AiChanged();
    }

    private void AiModel_Changed(object sender, EventArgs e)
    {
        if (_updatingAi || CurrentConfig is not { } config) return;
        // Picking from the list updates Text a moment after SelectionChanged, so prefer the picked item.
        var model = (e is SelectionChangedEventArgs && AiModel.SelectedItem is string picked ? picked : AiModel.Text).Trim();
        if (e is SelectionChangedEventArgs s) s.Handled = true;
        if (model == config.Model) return;
        config.Model = model;
        AiChanged();
    }

    private async void FindModels_Click(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        FindModelsButton.IsEnabled = false;
        AiModelHint.Text = "Asking the service for its models…";
        try
        {
            var models = await AiClient.ListModelsAsync(AiProviders.Connect(Host.Data.Settings, needModel: false));
            if (models.Count == 0)
            {
                AiModelHint.Text = "The service has no models yet. Download or load one first, then try again.";
                return;
            }
            var current = AiModel.Text;
            _updatingAi = true;
            try { AiModel.ItemsSource = models; AiModel.Text = current; }
            finally { _updatingAi = false; }
            AiModelHint.Text = models.Count == 1 ? "Found 1 model. Open the list to pick it." : $"Found {models.Count} models. Open the list to pick one.";
            AiModel.Focus();
            AiModel.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            AiModelHint.Text = ex.Message;
        }
        finally { FindModelsButton.IsEnabled = true; }
    }

    private void SaveAiKey_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentConfig is not { } config) return;
        var key = AiKey.Password.Trim();
        config.EncryptedApiKey = SecretStore.Protect(key);
        AiKey.Clear();
        RefreshAiKeyStatus();
        AiChanged();
        Host?.Log(key.Length > 0 ? $"{CurrentProvider.Name} API key saved" : $"{CurrentProvider.Name} API key removed");
    }

    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        // Pick up a model or address still being typed.
        AiModel_Changed(AiModel, EventArgs.Empty);
        AiConnection ai;
        try { ai = AiProviders.Connect(Host.Data.Settings); }
        catch (Exception ex) { AiTestStatus.Text = ex.Message; return; }

        TestAiButton.IsEnabled = false;
        AiTestStatus.Text = $"Asking {ai.Model}… (a model on your own PC can take a while the first time)";
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var reply = await AiClient.CompleteAsync(ai, "You are being tested from a settings page. Reply with one short friendly sentence.",
                "Say hello.", quick: true, ct: timeout.Token);
            reply = reply.ReplaceLineEndings(" ").Trim();
            if (reply.Length > 160) reply = reply[..160] + "…";
            AiTestStatus.Text = $"✓ Working. {ai.Model} answered in {watch.Elapsed.TotalSeconds:0.0} s: “{reply}”";
        }
        catch (OperationCanceledException)
        {
            AiTestStatus.Text = "No answer after 3 minutes. Is the model loaded?";
        }
        catch (Exception ex)
        {
            AiTestStatus.Text = "✗ " + ex.Message;
        }
        finally { TestAiButton.IsEnabled = true; }
    }

    // ---- Appearance ----------------------------------------------------------------------

    private bool _updatingAppearance; // true while setting the controls from the saved settings

    /// <summary>Shows the saved theme and accent as selected.</summary>
    private void RefreshAppearance()
    {
        if (Host == null) return;
        var s = Host.Data.Settings;
        _updatingAppearance = true;
        try
        {
            (s.Theme switch { "Light" => ThemeLight, "System" => ThemeSystem, _ => ThemeDark }).IsChecked = true;

            if (Swatches.Children.Count == 0)
                foreach (var (name, hex) in ThemeManager.Presets)
                {
                    var swatch = new RadioButton
                    {
                        GroupName = "Accent",
                        Tag = hex,
                        ToolTip = name,
                        Style = (Style)FindResource("Swatch"),
                        Background = new System.Windows.Media.SolidColorBrush(ThemeManager.ParseColor(hex)!.Value),
                    };
                    swatch.Checked += Swatch_Checked;
                    Swatches.Children.Add(swatch);
                }
            var current = ThemeManager.ParseColor(s.AccentColor);
            var matched = false;
            foreach (RadioButton swatch in Swatches.Children)
            {
                var isCurrent = current != null && ThemeManager.ParseColor((string)swatch.Tag) == current;
                swatch.IsChecked = isCurrent;
                matched |= isCurrent;
            }
            CustomHex.Text = matched ? "" : s.AccentColor;
            CustomPreview.Fill = current is { } c ? new System.Windows.Media.SolidColorBrush(c) : null;
        }
        finally { _updatingAppearance = false; }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingAppearance || Host == null || sender is not RadioButton { Tag: string theme }) return;
        Host.Data.Settings.Theme = theme; // MainWindow re-themes the app and saves
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingAppearance || Host == null || sender is not RadioButton { Tag: string hex }) return;
        Host.Data.Settings.AccentColor = hex;
        RefreshAppearance();
    }

    private void CustomHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCustomHex();
    }

    private void CustomHex_LostFocus(object sender, RoutedEventArgs e) => ApplyCustomHex();

    private void ApplyCustomHex()
    {
        if (Host == null || CustomHex.Text.Trim().Length == 0) return;
        if (ThemeManager.ParseColor(CustomHex.Text) is not { } color)
        {
            CustomHint.Text = $"“{CustomHex.Text.Trim()}” isn't a color. Use six hex digits like #FF5599.";
            return;
        }
        CustomHint.Text = "Type a color like #FF5599 and press Enter.";
        Host.Data.Settings.AccentColor = ThemeManager.ToHex(color);
        RefreshAppearance();
    }

    /// <summary>Shows the section picked in the sidebar.</summary>
    private void Sections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AboutSection == null) return; // still loading
        var sections = new[] { ListeningSection, AppearanceSection, StartupSection, CommandsSection, AiSection, AboutSection };
        for (var i = 0; i < sections.Length; i++)
            sections[i].Visibility = i == Sections.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        Host?.ChooseMicrophone();
        Refresh();
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartWithWindows.IsChecked == true);
            Host?.Log(StartWithWindows.IsChecked == true ? "Shmoice will start when you sign in" : "Shmoice won't start when you sign in");
        }
        catch (Exception ex)
        {
            Host?.Log("Couldn't change Start with Windows: " + ex.Message);
        }
        Refresh();
    }

    /// <summary>Lets you hear the chime volume you just picked (keyboard: only the keys that move the slider).</summary>
    private void Chime_Released(object sender, RoutedEventArgs e)
    {
        if (e is KeyEventArgs k && k.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown))
            return;
        if (Host != null) Chime.Play(Host.Data.Settings.ChimeVolume);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            UpdateStatus.Text = await Host.CheckForUpdatesAsync();
            InstallUpdateButton.Visibility = Host.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Host != null) await Host.InstallUpdateAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(CommandStore.FilePath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }
}
