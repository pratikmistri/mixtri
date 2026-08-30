using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Mixtri_App.Converters;

/// <summary>
/// Converts a bool (or bool?) to one of two brushes based on a ConverterParameter key.
/// Looks brushes up at runtime from Application.Current.Resources so we avoid
/// putting {ThemeResource ...} on a non-DependencyProperty (a XAML pattern that
/// raises XAML_E_LAYOUT_CYCLE in WinUI 3 when bound into the visual tree).
/// </summary>
public sealed class CheckedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isChecked = value is bool b && b;
        string key = parameter as string ?? "Bg";

        return key switch
        {
            "Bg" => isChecked && TryGetBrush("AccentFillColorDefaultBrush") is Brush bg
                ? (object)bg
                : new SolidColorBrush(Colors.Transparent),
            "Border" => isChecked
                ? (object)(TryGetBrush("AccentFillColorDefaultBrush")
                    ?? (Brush)new SolidColorBrush(Colors.Transparent))
                : TryGetBrush("TextFillColorPrimaryBrush")
                    ?? (Brush)new SolidColorBrush(Colors.Gray),
            "Glyph" => isChecked
                ? (object)(TryGetBrush("TextOnAccentFillColorPrimaryBrush")
                    ?? (Brush)new SolidColorBrush(Colors.White))
                : TryGetBrush("TextFillColorPrimaryBrush")
                    ?? (Brush)new SolidColorBrush(Colors.Gray),
            _ => DependencyProperty.UnsetValue,
        };
    }

    // Application.Current can be null in design-time / early init, and the
    // Resources indexer throws on a missing key. Look up safely and return
    // null so the caller can substitute a fallback brush.
    private static Brush? TryGetBrush(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        var resources = app.Resources;
        if (resources is null) return null;
        if (resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

