using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>
/// Dark / light mode and the accent color. Swaps the app's color resources (used everywhere as
/// DynamicResource, so they update live), switches WPF's built-in theme, and points the built-in
/// controls' accent (checkboxes, sliders, toggles) at the chosen color.
/// </summary>
public static class ThemeManager
{
    /// <summary>Accent presets shown in Settings.</summary>
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Purple", "#7C5CFF"), ("Teal", "#0E8C99"), ("Blue", "#3B82F6"), ("Green", "#22A06B"),
        ("Orange", "#F08C2E"), ("Pink", "#E5487A"), ("Red", "#E5484D"), ("Gold", "#C99A1E"),
    ];

    private static bool _listening;
    private static AppSettings? _settings;

    public static bool IsDark(AppSettings s) => s.Theme switch
    {
        "Light" => false,
        "System" => !WindowsUsesLightTheme(),
        _ => true,
    };

    public static void Apply(AppSettings settings)
    {
        _settings = settings;
        if (!_listening)
        {
            // "Match Windows": follow changes to the Windows setting while running.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _settings?.Theme == "System")
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(_settings));
            };
            _listening = true;
        }

        var dark = IsDark(settings);
        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var r = app.Resources;

        var accent = ParseColor(settings.AccentColor) ?? ParseColor(AppSettings.DefaultAccent)!.Value;

        // App palette: greys with a little of the accent mixed in, so the whole app takes on the
        // accent's hue (purple accent = purple-ish greys, teal = teal-ish, …). (grey level, how much accent)
        Color Tint(byte grey, double amount) => Mix(Color.FromRgb(grey, grey, grey), accent, amount);
        var card = dark ? Tint(0x1D, 0.06) : Tint(0xFF, 0.012);
        Set(r, "WindowBgBrush", dark ? Tint(0x15, 0.045) : Tint(0xF2, 0.035));
        Set(r, "CardBrush", card);
        Set(r, "CardBorderBrush", dark ? Tint(0x2C, 0.09) : Tint(0xDD, 0.07));
        Set(r, "MutedTextBrush", dark ? Tint(0x9A, 0.10) : Tint(0x64, 0.12));
        Set(r, "BodyTextBrush", dark ? Tint(0xC8, 0.06) : Tint(0x32, 0.08));
        Set(r, "TextBrush", dark ? Tint(0xFF, 0.0) : Tint(0x1A, 0.05));
        Set(r, "IdleBrush", dark ? Tint(0x6B, 0.08) : Tint(0xA0, 0.08));
        Set(r, "RowBrush", dark ? Tint(0x24, 0.07) : Tint(0xF3, 0.04));
        Set(r, "RowHoverBrush", dark ? Tint(0x24, 0.07) : Tint(0xEC, 0.06));
        Set(r, "RowSelectedBrush", dark ? Tint(0x2D, 0.10) : Tint(0xE2, 0.10));
        Set(r, "InsetBrush", dark ? Tint(0x19, 0.05) : Tint(0xF7, 0.03));
        Set(r, "MeterTrackBrush", dark ? Tint(0x37, 0.10) : Tint(0xDA, 0.08));
        Set(r, "DangerSoftBrush", dark ? "#3A1D24" : "#FBE3E6");

        r["AppAccentColor"] = accent;
        Set(r, "AppAccentBrush", accent);
        Set(r, "AccentSoftBrush", Mix(card, accent, dark ? 0.14 : 0.10));
        Set(r, "AccentSoftBorderBrush", Mix(card, accent, dark ? 0.30 : 0.35));

        // WPF's built-in (Fluent) controls read these. The accent fill is the chosen color itself;
        // text on it is white or black, whichever reads better.
        var onAccent = Luminance(accent) > 0.6 ? Colors.Black : Colors.White;
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.2);
        r["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.4);
        r["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.6);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.2);
        r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.4);
        r["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.6);
        Set(r, "AccentFillColorDefaultBrush", accent);
        Set(r, "AccentFillColorSecondaryBrush", Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
        Set(r, "AccentFillColorTertiaryBrush", Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        Set(r, "AccentTextFillColorPrimaryBrush", dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.25));
        Set(r, "AccentTextFillColorSecondaryBrush", dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.25));
        Set(r, "AccentTextFillColorTertiaryBrush", dark ? Mix(accent, Colors.White, 0.2) : Mix(accent, Colors.Black, 0.1));
        Set(r, "OnAccentBrush", onAccent);
        Set(r, "TextOnAccentFillColorPrimaryBrush", onAccent);
        Set(r, "TextOnAccentFillColorSecondaryBrush", onAccent);

        // The built-in theme takes its accent from Windows' accent color through these system keys,
        // so override them too or checkboxes and sliders keep the Windows accent.
        r[SystemColors.AccentColorKey] = accent;
        r[SystemColors.AccentColorLight1Key] = Mix(accent, Colors.White, 0.2);
        r[SystemColors.AccentColorLight2Key] = Mix(accent, Colors.White, 0.4);
        r[SystemColors.AccentColorLight3Key] = Mix(accent, Colors.White, 0.6);
        r[SystemColors.AccentColorDark1Key] = Mix(accent, Colors.Black, 0.2);
        r[SystemColors.AccentColorDark2Key] = Mix(accent, Colors.Black, 0.4);
        r[SystemColors.AccentColorDark3Key] = Mix(accent, Colors.Black, 0.6);
        Set(r, SystemColors.AccentColorBrushKey, accent);
        Set(r, SystemColors.AccentColorLight1BrushKey, Mix(accent, Colors.White, 0.2));
        Set(r, SystemColors.AccentColorLight2BrushKey, Mix(accent, Colors.White, 0.4));
        Set(r, SystemColors.AccentColorLight3BrushKey, Mix(accent, Colors.White, 0.6));
        Set(r, SystemColors.AccentColorDark1BrushKey, Mix(accent, Colors.Black, 0.2));
        Set(r, SystemColors.AccentColorDark2BrushKey, Mix(accent, Colors.Black, 0.4));
        Set(r, SystemColors.AccentColorDark3BrushKey, Mix(accent, Colors.Black, 0.6));

        Applied?.Invoke();
    }

    /// <summary>Raised after colors change (e.g. so the main window can re-apply layout tweaks the theme switch resets).</summary>
    public static event Action? Applied;

    /// <summary>"#7C5CFF" or "7c5cff" → color; null if it isn't a 6-digit hex color.</summary>
    public static Color? ParseColor(string? text)
    {
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Rgb(string hex) => ParseColor(hex)!.Value;

    private static void Set(ResourceDictionary r, string key, string hex) => Set(r, key, Rgb(hex));

    private static void Set(ResourceDictionary r, object key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (key?.GetValue("AppsUseLightTheme") as int? ?? 0) == 1;
    }
}
