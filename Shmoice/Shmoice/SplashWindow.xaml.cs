using System.Windows;
using System.Windows.Media.Animation;

namespace Shmoice;

/// <summary>The logo shown for a moment at launch, while the main window gets ready behind it.</summary>
public partial class SplashWindow : Window
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AnimateIn();
    }

    /// <summary>How long the progress bar takes to fill; matches how long the splash stays up.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);

    private void AnimateIn()
    {
        var appear = TimeSpan.FromMilliseconds(350);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, appear) { EasingFunction = EaseOut });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, appear) { EasingFunction = EaseOut });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, appear) { EasingFunction = EaseOut });

        var track = ((FrameworkElement)Progress.Parent).ActualWidth;
        Progress.BeginAnimation(WidthProperty, new DoubleAnimation(0, track, Duration - TimeSpan.FromMilliseconds(150))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Fades out (and shrinks slightly), then closes.</summary>
    public Task FadeOutAndCloseAsync()
    {
        var done = new TaskCompletionSource();
        var vanish = TimeSpan.FromMilliseconds(280);
        var fade = new DoubleAnimation(0, vanish) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { Close(); done.TrySetResult(); };
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, vanish));
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, vanish));
        Card.BeginAnimation(OpacityProperty, fade);
        return done.Task;
    }
}
