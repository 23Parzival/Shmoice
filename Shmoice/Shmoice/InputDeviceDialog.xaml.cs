using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using Shmoice.Services;

namespace Shmoice;

/// <summary>Picks which microphone to listen on, with a live level meter for the selected one.</summary>
public partial class InputDeviceDialog : Window
{
    private WaveInEvent? _meter;
    private float _peak;          // loudest sample since the last UI tick (0..1), written on the audio thread
    private double _heardLoudest;  // loudest level seen for the current device
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private InputDeviceDialog()
    {
        InitializeComponent();
        // No title bar, so let the user drag the dialog by its body.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        _tick.Tick += Tick;
        Closed += (_, _) => { _tick.Stop(); StopMeter(); };
    }

    /// <summary>
    /// Shows the picker with <paramref name="preferredName"/> preselected.
    /// Returns the chosen input, or null if cancelled.
    /// </summary>
    public static AudioInput? Ask(Window owner, IReadOnlyList<AudioInput> inputs, string preferredName)
    {
        var dialog = new InputDeviceDialog { Owner = owner };
        dialog.DeviceCombo.ItemsSource = inputs;
        dialog.DeviceCombo.SelectedItem = inputs.FirstOrDefault(i => i.Name == preferredName) ?? inputs[0];

        // Dim the main window while the dialog is open.
        var content = owner.Content as UIElement;
        var oldOpacity = content?.Opacity ?? 1;
        if (content != null) content.Opacity = 0.45;
        try
        {
            var ok = dialog.ShowDialog() == true;
            return ok ? dialog.DeviceCombo.SelectedItem as AudioInput : null;
        }
        finally
        {
            if (content != null) content.Opacity = oldOpacity;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopMeter();
        LevelBar.Value = 0;
        _heardLoudest = 0;
        LevelHint.Text = "Say something. The bar should move while you talk.";
        if (DeviceCombo.SelectedItem is not AudioInput input) return;

        try
        {
            // DeviceNumber -1 is Windows' default recording device.
            var meter = new WaveInEvent
            {
                DeviceNumber = input.DeviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
            };
            meter.DataAvailable += (_, a) =>
            {
                var peak = 0;
                for (var i = 0; i + 1 < a.BytesRecorded; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(a.Buffer, i)));
                _peak = Math.Max(_peak, peak / 32768f);
            };
            meter.StartRecording();
            _meter = meter;
            _tick.Start();
        }
        catch (Exception ex)
        {
            LevelHint.Text = "Couldn't open this microphone: " + ex.Message;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        var peak = _peak;
        _peak = 0;
        // Rise instantly, fall smoothly, like a normal VU meter. Square root makes quiet speech visible.
        var level = Math.Sqrt(peak);
        LevelBar.Value = Math.Max(level, LevelBar.Value * 0.85);
        _heardLoudest = Math.Max(_heardLoudest, level);
        if (_heardLoudest > 0.15) LevelHint.Text = "This microphone is picking you up.";
    }

    private void StopMeter()
    {
        if (_meter == null) return;
        try { _meter.StopRecording(); } catch { /* device may be gone */ }
        _meter.Dispose();
        _meter = null;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
