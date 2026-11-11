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
        var sections = new[] { ListeningSection, AppearanceSection, StartupSection, CommandsSection, AboutSection };
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(CommandStore.FilePath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }
}
