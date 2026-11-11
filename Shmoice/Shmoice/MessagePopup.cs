using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shmoice;

/// <summary>
/// Small pop-up for the "Show a message" action: appears above the taskbar in the bottom-right
/// corner without taking focus, and disappears after a few seconds or when clicked.
/// </summary>
public static class MessagePopup
{
    private static readonly List<Window> Open = new();

    public static void Show(string text)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        body.Children.Add(new TextBlock
        {
            Text = "SHMOICE",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = res["MutedTextBrush"] as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 4),
        });
        body.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            Content = new Border
            {
                Background = res["WindowBgBrush"] as Brush ?? Brushes.Black,
                BorderBrush = res["AppAccentBrush"] as Brush ?? Brushes.Teal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = body,
            },
        };

        window.Loaded += (_, _) => Restack();
        window.Closed += (_, _) => { Open.Remove(window); Restack(); };
        window.MouseLeftButtonUp += (_, _) => window.Close();
        Open.Add(window);
        window.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); window.Close(); };
        timer.Start();
    }

    /// <summary>Stacks open pop-ups upward from the bottom-right corner, newest at the bottom.</summary>
    private static void Restack()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 12;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            w.Left = area.Right - w.ActualWidth - 12;
            w.Top = bottom - w.ActualHeight;
            bottom = w.Top - 8;
        }
    }
}
