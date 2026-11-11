using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Shmoice.Models;
using Shmoice.Services;

namespace Shmoice.Tabs;

/// <summary>Create profiles (sets of commands that only run in certain apps) and choose their commands.</summary>
public partial class ProfilesTab : UserControl, ITab
{
    /// <summary>One row in "Commands in this profile".</summary>
    public record CommandRow(VoiceCommand Command, string Phrase, bool InProfile, string Note);

    private MainWindow? _host;

    public ProfilesTab()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshCommands(); };
    }

    private Profile? Selected => List.SelectedItem as Profile;

    public void Attach(MainWindow host)
    {
        _host = host;
        List.ItemsSource = host.Data.Profiles;
        host.Data.Profiles.CollectionChanged += Profiles_CollectionChanged;
        foreach (var p in host.Data.Profiles) p.PropertyChanged += (_, _) => host.SaveSoon();
        if (host.Data.Profiles.Count > 0) List.SelectedIndex = 0;
        UpdateState();
    }

    private void Profiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var p in e.NewItems?.OfType<Profile>() ?? []) p.PropertyChanged += (_, _) => _host?.SaveSoon();
        UpdateState();
    }

    private void UpdateState()
    {
        if (_host == null) return;
        ListEmpty.Visibility = _host.Data.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = Selected != null ? Visibility.Visible : Visibility.Hidden;
        EditorEmpty.Visibility = Selected != null ? Visibility.Collapsed : Visibility.Visible;
        Editor.DataContext = Selected;
        RefreshCommands();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var profile = new Profile { Name = "New profile" };
        _host.Data.Profiles.Add(profile);
        List.SelectedItem = profile;
        _host.SaveQuietly();
        _host.Log("Added a profile");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || Selected is not { } profile) return;
        var count = _host.Data.Commands.Count(c => c.ProfileId == profile.Id);
        if (!ConfirmDialog.Ask(Window.GetWindow(this)!, "Delete this profile?",
                count == 0 ? $"“{profile.Name}” will be removed."
                    : $"“{profile.Name}” will be removed. Its {count} command(s) are kept and will work in any app.",
                "Delete", out _, showDontAsk: false))
            return;
        foreach (var c in _host.Data.Commands.Where(c => c.ProfileId == profile.Id)) c.ProfileId = null;
        _host.Data.Profiles.Remove(profile);
        if (_host.Data.Profiles.Count > 0) List.SelectedIndex = 0;
        _host.SaveQuietly();
        _host.Log($"Deleted profile \"{profile.Name}\"");
    }

    // ---- Apps --------------------------------------------------------------------------

    private void RunningApps_DropDownOpened(object? sender, EventArgs e) => RunningApps.ItemsSource = ForegroundApp.RunningAppNames();

    private void RunningApps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } profile || RunningApps.SelectedItem is not string app) return;
        if (!profile.AppNames().Contains(app, StringComparer.OrdinalIgnoreCase))
            profile.Apps = profile.Apps.Trim().Length == 0 ? app : profile.Apps.TrimEnd(' ', ',') + ", " + app;
        RunningApps.SelectedItem = null;
    }

    // ---- Commands in the profile ---------------------------------------------------------

    private void RefreshCommands()
    {
        if (_host == null || Selected is not { } profile) { CommandChecks.ItemsSource = null; return; }
        CommandChecks.ItemsSource = _host.Data.Commands.Select(c =>
        {
            var other = c.ProfileId is { } id && id != profile.Id ? _host.Data.Profiles.FirstOrDefault(p => p.Id == id) : null;
            return new CommandRow(c, c.Phrase, c.ProfileId == profile.Id, other != null ? $"  (in {other.Name})" : "");
        }).ToList();
    }

    private void CommandCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || Selected is not { } profile || sender is not CheckBox { DataContext: CommandRow row } box) return;
        row.Command.ProfileId = box.IsChecked == true ? profile.Id : null;
        _host.SaveQuietly();
        RefreshCommands();
    }
}
