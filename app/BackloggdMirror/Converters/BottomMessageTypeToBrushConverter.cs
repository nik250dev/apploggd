using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Icon/text brush for the bottom message bar. White for every type: unlike toasts, the bar keeps a
/// neutral background, and the type is conveyed by the icon alone.
/// </summary>
public class BottomMessageTypeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is BottomMessageType type)
        {
            return type switch
            {
                BottomMessageType.Success => SolidColorBrush.Parse("#ffffffff"),
                BottomMessageType.Warning => SolidColorBrush.Parse("#ffffffff"),
                BottomMessageType.Error => SolidColorBrush.Parse("#ffffffff"),
                _ => SolidColorBrush.Parse("#ffffffff")
            };
        }
        return SolidColorBrush.Parse("#ffffff");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
