using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Shmoice;

public partial class App : Application
{
    /// <summary>
    /// Exists for as long as Shmoice is running. The installer waits for it to go away before replacing
    /// the exe during an update (see PrepareToInstall in installer.iss; keep the name the same there).
    /// </summary>
    private static Mutex? _runningMarker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _runningMarker = new Mutex(false, "Shmoice.Running");

        // App-wide: Enter (in single-line boxes) or clicking anywhere else takes focus out of a text box.
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.KeyDownEvent, new KeyEventHandler(TextInput_KeyDown));
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.KeyDownEvent, new KeyEventHandler(TextInput_KeyDown));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(Window_PreviewMouseDown));

        ShowSplashThenMainWindow();
    }

    /// <summary>Shows the logo for 2 seconds while the main window loads behind it, then fades into the app.</summary>
    private async void ShowSplashThenMainWindow()
    {
        var main = new MainWindow(); // loads commands and settings (quick)
        MainWindow = main;
        var settings = main.Data.Settings;
        if (settings.StartMinimized) main.WindowState = WindowState.Minimized;

        // No splash when it's turned off, or when starting minimized (it would pop up over whatever you're doing).
        if (!settings.ShowSplash || settings.StartMinimized)
        {
            main.Show();
            return;
        }

        var splash = new SplashWindow();
        splash.Show();
        await Task.Delay(splash.Duration);
        main.Show();
        main.Activate();
        await splash.FadeOutAndCloseAsync(); // fades over the main window, which is already there
    }

    private static void TextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox { AcceptsReturn: true }) return; // multi-line boxes keep Enter for new lines
        ClearFocus((DependencyObject)sender);
        e.Handled = true;
    }

    private static void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not (TextBoxBase or PasswordBox) || Keyboard.FocusedElement is not DependencyObject focused)
            return;
        // Clicks inside the focused box (or its scrollbar, caret, etc.) keep focus.
        if (e.OriginalSource is DependencyObject clicked && IsInside(clicked, focused)) return;
        ClearFocus(focused);
    }

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var node = child; node != null;
             node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node == ancestor) return true;
        return false;
    }

    /// <summary>Drops keyboard and logical focus so the box no longer looks or acts focused.</summary>
    private static void ClearFocus(DependencyObject element)
    {
        var scope = FocusManager.GetFocusScope(element);
        if (scope != null) FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }
}
