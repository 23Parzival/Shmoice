using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shmoice;

public enum InputKind
{
    /// <summary>Anything goes.</summary>
    Any,
    /// <summary>Digits only, e.g. Repeat.</summary>
    Integer,
    /// <summary>Digits with one decimal point or comma, e.g. Wait seconds.</summary>
    Decimal,
    /// <summary>Letters, spaces, apostrophes and hyphens: things you can say, e.g. the wake word.</summary>
    Words,
}

/// <summary>
/// Attached property that stops a TextBox from accepting characters that don't fit:
/// <c>local:InputFilter.Kind="Integer"</c>. Covers typing and pasting. If the box is left
/// empty or invalid, it snaps back to the last good value when it loses focus.
/// </summary>
public static class InputFilter
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(InputKind), typeof(InputFilter), new PropertyMetadata(InputKind.Any, OnKindChanged));

    public static InputKind GetKind(DependencyObject d) => (InputKind)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, InputKind value) => d.SetValue(KindProperty, value);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        // Detach first so switching kinds (e.g. a step changing to Wait) never double-subscribes.
        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.LostFocus -= OnLostFocus;
        DataObject.RemovePastingHandler(box, OnPasting);
        if ((InputKind)e.NewValue == InputKind.Any) return;

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.LostFocus += OnLostFocus;
        DataObject.AddPastingHandler(box, OnPasting);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !IsValid(GetKind(box), ResultingText(box, e.Text));
    }

    /// <summary>Space doesn't come through PreviewTextInput, so block it here where it isn't allowed.</summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && GetKind((TextBox)sender) is InputKind.Integer or InputKind.Decimal)
            e.Handled = true;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        var pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
        if (!IsValid(GetKind(box), ResultingText(box, pasted.Trim()))) e.CancelCommand();
    }

    /// <summary>An empty or half-typed value ("", ".") would leave the binding in an error state; restore the saved value.</summary>
    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var kind = GetKind(box);
        var incomplete = kind switch
        {
            InputKind.Integer => !int.TryParse(box.Text, out _),
            InputKind.Decimal => box.Text.Length > 0 && !double.TryParse(box.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
            _ => false,
        };
        if (incomplete) box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private static string ResultingText(TextBox box, string input)
    {
        var text = box.Text;
        var start = Math.Min(box.SelectionStart, text.Length);
        var length = Math.Min(box.SelectionLength, text.Length - start);
        return text.Remove(start, length).Insert(start, input);
    }

    private static bool IsValid(InputKind kind, string text) => kind switch
    {
        InputKind.Integer => Regex.IsMatch(text, @"^\d{0,3}$"),
        InputKind.Decimal => Regex.IsMatch(text, @"^\d{0,4}([.,]\d{0,3})?$"),
        InputKind.Words => Regex.IsMatch(text, @"^[\p{L}' -]*$"),
        _ => true,
    };
}
