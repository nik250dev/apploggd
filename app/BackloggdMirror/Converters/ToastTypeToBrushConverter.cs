using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Accent brush for the toast. Every type currently resolves to white, since the coloured
/// background already carries the meaning — the switch is kept so a type can be differentiated
/// later without touching the bindings.
/// </summary>
public class ToastTypeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastType type)
        {
            return type switch
            {
                ToastType.Success => SolidColorBrush.Parse("#ffffffff"),
                ToastType.Warning => SolidColorBrush.Parse("#ffffffff"),
                ToastType.Error => SolidColorBrush.Parse("#ffffffff"),
                _ => SolidColorBrush.Parse("#333333")
            };
        }
        return SolidColorBrush.Parse("#333333");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
