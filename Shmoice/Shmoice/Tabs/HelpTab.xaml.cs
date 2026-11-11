using System.Windows;
using System.Windows.Controls;
using Shmoice.Models;

namespace Shmoice.Tabs;

/// <summary>A short guide, plus every action (from the action catalog) with a search box.</summary>
public partial class HelpTab : UserControl, ITab
{
    public record Group(string Name, List<ActionInfo> Items);

    public HelpTab()
    {
        InitializeComponent();
        Show("");
    }

    public void Attach(MainWindow host) { }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Show(SearchBox.Text);
    }

    private void Show(string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Actions.ItemsSource = ActionCatalog.All
            .Where(a => words.All(w => $"{a.Label} {a.Category} {a.Description}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(a => a.Category)
            .Select(g => new Group(g.Key, g.ToList()))
            .ToList();
    }
}
