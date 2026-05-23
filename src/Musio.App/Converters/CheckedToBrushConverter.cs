using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Musio_App.Converters;

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
        var res = Application.Current.Resources;
        return key switch
        {
            "Bg" => isChecked && res["AccentFillColorDefaultBrush"] is Brush bg
                ? bg
                : (object)new SolidColorBrush(Colors.Transparent),
            "Border" => isChecked
                ? (Brush)res["AccentFillColorDefaultBrush"]
                : (Brush)res["TextFillColorPrimaryBrush"],
            "Glyph" => isChecked
                ? (Brush)res["TextOnAccentFillColorPrimaryBrush"]
                : (Brush)res["TextFillColorPrimaryBrush"],
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

