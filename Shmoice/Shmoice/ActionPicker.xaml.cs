using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shmoice.Models;

namespace Shmoice;

/// <summary>A tree node: either a category or an action.</summary>
public abstract class PickerNode : ObservableObject
{
    private bool _isExpanded, _isSelected;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public class ActionCategoryNode(string name) : PickerNode
{
    public string Name { get; } = name;
    public ObservableCollection<ActionNode> Items { get; } = new();
}

public class ActionNode(ActionInfo info) : PickerNode
{
    public ActionInfo Info { get; } = info;
}

/// <summary>
/// Action chooser for a step: a dropdown-style button that opens a tree of categories
/// (Media, Mouse, Browser…) with the actions inside, plus a search box that filters the tree.
/// </summary>
public partial class ActionPicker : UserControl
{
    public static readonly DependencyProperty SelectedTypeProperty = DependencyProperty.Register(
        nameof(SelectedType), typeof(ActionType), typeof(ActionPicker),
        new FrameworkPropertyMetadata(default(ActionType), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ActionPicker)d).UpdateLabel()));

    public ActionType SelectedType
    {
        get => (ActionType)GetValue(SelectedTypeProperty);
        set => SetValue(SelectedTypeProperty, value);
    }

    private readonly ObservableCollection<ActionCategoryNode> _categories = new();

    public ActionPicker()
    {
        InitializeComponent();
        Tree.ItemsSource = _categories;
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        var info = ActionCatalog.Get(SelectedType);
        LabelText.Text = info.Label;
        OpenButton.ToolTip = $"{info.Category} › {info.Label}\n{info.Description}";
    }

    // ---- Opening / closing -----------------------------------------------------------

    private void Popup_Opened(object? sender, EventArgs e)
    {
        // While open, clicks on the button go to the popup's "click outside" handling instead,
        // so clicking the button again closes it rather than closing and instantly reopening.
        OpenButton.IsHitTestVisible = false;
        PopupBody.Width = Math.Max(320, OpenButton.ActualWidth);
        SearchBox.Text = "";
        Build("");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());
    }

    private void Popup_Closed(object? sender, EventArgs e) => OpenButton.IsHitTestVisible = true;

    private void Close()
    {
        OpenButton.IsChecked = false;
        OpenButton.Focus();
    }

    private void Pick(ActionNode node)
    {
        SelectedType = node.Info.Type;
        Close();
    }

    // ---- Tree contents ----------------------------------------------------------------

    /// <summary>Fills the tree. With no filter, only the current action's category is open.</summary>
    private void Build(string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _categories.Clear();
        ActionNode? toSelect = null;

        foreach (var group in ActionCatalog.All.GroupBy(a => a.Category))
        {
            var category = new ActionCategoryNode(group.Key);
            foreach (var info in group)
            {
                var text = $"{info.Label} {info.Category} {info.Description} {info.Type}";
                if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                var node = new ActionNode(info);
                category.Items.Add(node);
                if (words.Length == 0 ? info.Type == SelectedType : toSelect == null) toSelect = node;
            }
            if (category.Items.Count == 0) continue;
            category.IsExpanded = words.Length > 0 || category.Items.Contains(toSelect!);
            _categories.Add(category);
        }

        NoMatches.Visibility = _categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Tree.Visibility = _categories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (toSelect != null) toSelect.IsSelected = true;
    }

    private ActionNode? SelectedNode => _categories.SelectMany(c => c.Items).FirstOrDefault(n => n.IsSelected);

    /// <summary>Keeps the highlighted item scrolled into view.</summary>
    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == sender) ((TreeViewItem)sender).BringIntoView();
    }

    // ---- Input ------------------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Build(SearchBox.Text.Trim());

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (SelectedNode is { } node) Pick(node);
                e.Handled = true;
                break;
            case Key.Down:
                // Move into the tree, onto the highlighted action.
                if (ContainerFor(SelectedNode) is { } item) item.Focus();
                else if (Tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first) first.Focus();
                e.Handled = true;
                break;
        }
    }

    private void PopupBody_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !SearchBox.IsKeyboardFocused && Tree.SelectedItem is PickerNode node)
        {
            if (node is ActionNode action) Pick(action);
            else node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    /// <summary>One click picks an action, or opens/closes a category.</summary>
    private void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != Tree; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ToggleButton) return; // the expand arrow handles itself
            if (d is not TreeViewItem item) continue;
            if (item.DataContext is ActionNode action) Pick(action);
            else if (item.DataContext is ActionCategoryNode category) category.IsExpanded = !category.IsExpanded;
            e.Handled = true;
            return;
        }
    }

    private TreeViewItem? ContainerFor(ActionNode? node)
    {
        if (node == null) return null;
        var category = _categories.FirstOrDefault(c => c.Items.Contains(node));
        if (category == null || Tree.ItemContainerGenerator.ContainerFromItem(category) is not TreeViewItem parent) return null;
        return parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
    }
}
