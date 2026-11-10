using System.Globalization;
using System.Windows.Data;
using Shmoice.Models;

namespace Shmoice;

/// <summary>Shows "Play / Pause" instead of "MediaPlayPause" in the action dropdown.</summary>
public class ActionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActionType t && ActionStep.Labels.TryGetValue(t, out var label) ? label : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
