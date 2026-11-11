using System.Globalization;
using System.Windows.Data;
using Shmoice.Models;

namespace Shmoice;

/// <summary>Shows "Play / Pause" instead of "MediaPlayPause".</summary>
public class ActionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActionType t ? ActionCatalog.Get(t).Label : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A height → CornerRadius of half that height, for pill-shaped bars.</summary>
public class HalfRoundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new System.Windows.CornerRadius(value is double h ? h / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>" · Gaming" after a command's step count when it belongs to a profile.</summary>
public class ProfileNameConverter : IValueConverter
{
    /// <summary>Set once at startup by the main window.</summary>
    public static AppData? Data { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Guid id && Data?.Profiles.FirstOrDefault(p => p.Id == id) is { } profile ? $"  ·  {profile.Name}" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// ComboBoxes can't select a null value, so "Any app" uses Guid.Empty in the list and null on the command.
/// </summary>
public class NullGuidConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value as Guid? ?? Guid.Empty;

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Guid id && id != Guid.Empty ? id : null;
}
