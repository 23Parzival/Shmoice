using System.Windows;
using System.Windows.Input;

namespace Shmoice;

/// <summary>Dark, rounded confirmation popup with an optional "Don't ask me again" checkbox.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
        // No title bar, so let the user drag the dialog by its body.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Shows the dialog. Returns true if confirmed; <paramref name="dontAskAgain"/> reports the checkbox.</summary>
    public static bool Ask(Window owner, string title, string message, string confirmText, out bool dontAskAgain)
    {
        var dialog = new ConfirmDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;

        // Dim the main window while the dialog is open.
        var content = owner.Content as UIElement;
        var oldOpacity = content?.Opacity ?? 1;
        if (content != null) content.Opacity = 0.45;
        try
        {
            var result = dialog.ShowDialog() == true;
            dontAskAgain = result && dialog.DontAskCheck.IsChecked == true;
            return result;
        }
        finally
        {
            if (content != null) content.Opacity = oldOpacity;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
