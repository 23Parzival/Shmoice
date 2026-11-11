using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Shmoice.Models;

namespace Shmoice.Tabs;

/// <summary>Schedules in a sidebar, with an editor for the selected one. The main window's timer does the running.</summary>
public partial class SchedulesTab : UserControl, ITab
{
    private MainWindow? _host;

    public SchedulesTab() => InitializeComponent();

    private Schedule? Selected => List.SelectedItem as Schedule;

    public void Attach(MainWindow host)
    {
        _host = host;
        List.ItemsSource = host.Data.Schedules;
        CommandCombo.ItemsSource = host.Data.Commands;
        foreach (var s in host.Data.Schedules) Watch(s);
        host.Data.Schedules.CollectionChanged += (_, e) =>
        {
            foreach (var s in e.NewItems?.OfType<Schedule>() ?? []) Watch(s);
            UpdateState();
        };
        if (host.Data.Schedules.Count > 0) List.SelectedIndex = 0;
        UpdateState();
    }

    /// <summary>Any edit saves shortly after and updates the "Next: …" text.</summary>
    private void Watch(Schedule s) => s.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(Schedule.Status) or nameof(Schedule.Title) or nameof(Schedule.Summary)) return;
        _host?.SaveSoon();
        _host?.UpdateScheduleStatus();
        if (s == Selected && e.PropertyName == nameof(Schedule.Kind)) ShowKind();
    };

    private void UpdateState()
    {
        if (_host == null) return;
        ListEmpty.Visibility = _host.Data.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = Selected != null ? Visibility.Visible : Visibility.Hidden;
        EditorEmpty.Visibility = Selected != null ? Visibility.Collapsed : Visibility.Visible;
        Editor.DataContext = Selected;
        ShowKind();
    }

    private void ShowKind()
    {
        var every = Selected?.Kind == ScheduleKind.Every;
        AtPanel.Visibility = every ? Visibility.Collapsed : Visibility.Visible;
        EveryPanel.Visibility = every ? Visibility.Visible : Visibility.Collapsed;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        // No command picked yet, so a half-finished schedule never runs something unexpected. Weekdays by default.
        var schedule = new Schedule { CommandId = null, Days = 0b0011111 };
        _host.Data.Schedules.Add(schedule);
        _host.UpdateScheduleStatus();
        List.SelectedItem = schedule;
        _host.SaveQuietly();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || Selected is not { } s) return;
        var index = List.SelectedIndex;
        _host.Data.Schedules.Remove(s);
        if (_host.Data.Schedules.Count > 0) List.SelectedIndex = Math.Min(index, _host.Data.Schedules.Count - 1);
        _host.SaveQuietly();
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || Selected is not { } s) return;
        if (_host.Data.Commands.FirstOrDefault(c => c.Id == s.CommandId) is not { } command)
        {
            _host.Log("Pick a command for this schedule first");
            return;
        }
        _host.Log($"Running \"{command.Phrase}\" from a schedule");
        await _host.RunCommandAsync(command, RunSource.Schedule);
    }
}

/// <summary>"At a time" / "Every…" for the schedule kind dropdown.</summary>
public class ScheduleKindLabel : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ScheduleKind.Every ? "Every…" : "At a time";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
