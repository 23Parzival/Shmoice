using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Shmoice.Models;

namespace Shmoice.Tabs;

/// <summary>Everything Shmoice ran or heard, with search, a filter, most-used stats and "Run again".</summary>
public partial class HistoryTab : UserControl, ITab
{
    private MainWindow? _host;
    private ICollectionView? _view;

    public HistoryTab() => InitializeComponent();

    public void Attach(MainWindow host)
    {
        _host = host;
        _view = CollectionViewSource.GetDefaultView(host.History.Items);
        _view.Filter = Matches;
        List.ItemsSource = _view;
        host.History.Items.CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    public record MostUsedRow(string Phrase, int Count);

    private bool Matches(object o)
    {
        if (o is not HistoryItem item) return false;
        var result = ResultFilter.SelectedIndex switch
        {
            1 => item.Result == RunResult.Ran,
            2 => item.Result == RunResult.Failed,
            3 => item.Result is RunResult.Ignored or RunResult.Skipped,
            _ => true,
        };
        var source = SourceFilter.SelectedIndex switch
        {
            1 => item.Source == RunSource.Voice,
            2 => item.Source == RunSource.Schedule,
            3 => item.Source == RunSource.Test,
            _ => true,
        };
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = $"{item.Phrase} {item.Detail} {item.Heard} {item.Source} {item.Result}";
        return result && source && words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchHint == null || SourceFilter == null || _view == null) return; // still loading
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateSummary();
    }

    /// <summary>Clicking a most-used command searches for it.</summary>
    private void MostUsed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MostUsed.SelectedItem is not MostUsedRow row) return;
        SearchBox.Text = row.Phrase;
        MostUsed.SelectedItem = null;
    }

    private void UpdateSummary()
    {
        if (_host == null) return;
        var items = _host.History.Items;
        CountAll.Text = items.Count.ToString();
        CountRan.Text = items.Count(i => i.Result == RunResult.Ran).ToString();
        CountFailed.Text = items.Count(i => i.Result == RunResult.Failed).ToString();
        CountIgnored.Text = items.Count(i => i.Result is RunResult.Ignored or RunResult.Skipped).ToString();

        if (items.Count == 0)
        {
            EmptyText.Text = "Nothing yet. Commands you run show up here, by voice, by schedule, or with Test.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Text = "Nothing matches.";
            EmptyText.Visibility = _view?.IsEmpty == true ? Visibility.Visible : Visibility.Collapsed;
        }

        var top = items.Where(i => i.Result == RunResult.Ran && i.Source == RunSource.Voice)
            .GroupBy(i => i.Phrase, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(8)
            .Select(g => new MostUsedRow(g.Key, g.Count())).ToList();
        MostUsed.ItemsSource = top;
        MostUsedEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RunAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || (sender as FrameworkElement)?.DataContext is not HistoryItem item) return;
        var command = _host.Data.Commands.FirstOrDefault(c => c.Id == item.CommandId)
                      ?? _host.Data.Commands.FirstOrDefault(c => string.Equals(c.Phrase, item.Phrase, StringComparison.OrdinalIgnoreCase));
        if (command == null)
        {
            _host.Log($"\"{item.Phrase}\" isn't one of your commands any more");
            return;
        }
        _host.Log($"Running \"{command.Phrase}\" again");
        await _host.RunCommandAsync(command, RunSource.Test);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || _host.History.Items.Count == 0) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this)!, "Clear history?",
                $"All {_host.History.Items.Count} entries will be removed. Your commands aren't affected.", "Clear", out _, showDontAsk: false))
            return;
        _host.History.Clear();
        _host.Log("History cleared");
    }
}

/// <summary>Formats a history line: ConverterParameter picks the piece (When, Source, Detail, Badge…).</summary>
public class HistoryFormat : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC2, 0x3B, 0x3B));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB7, 0x7B, 0x12));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x62));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not HistoryItem item) return "";
        var detail = item.Detail.Length > 0 ? item.Detail : item.Heard.Length > 0 ? $"Heard: “{item.Heard}”" : "";
        return (parameter as string) switch
        {
            "When" => item.Time.Date == DateTime.Today ? item.Time.ToString("HH:mm:ss")
                : item.Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday " + item.Time.ToString("HH:mm")
                : item.Time.ToString("MMM d HH:mm", culture),
            "Source" => item.Source switch
            {
                RunSource.Voice => item.Confidence is { } c ? $"  by voice, {c:P0} sure" : "  by voice",
                RunSource.Schedule => "  by schedule",
                RunSource.Test => "  by hand",
                _ => "",
            },
            "Detail" => detail,
            "DetailVisibility" => detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            "Badge" => item.Result switch
            {
                RunResult.Ran => Green,
                RunResult.Failed => Red,
                RunResult.Skipped => Amber,
                _ => Grey,
            },
            _ => "",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
