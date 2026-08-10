using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Toast background colour, which is the only thing that visually distinguishes the three types.
/// Colours are literals rather than theme resources because a toast keeps the same appearance
/// regardless of the surface it appears over.
/// </summary>
public class ToastTypeToBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastType type)
        {
            return type switch
            {
                ToastType.Success => SolidColorBrush.Parse("#43b94f"),   // green

                // Blue, not amber: warnings here are informational ("session discarded"), and
                // amber next to the red error toast read as the same alarm.
                ToastType.Warning => SolidColorBrush.Parse("#4b7bd4"),

                ToastType.Error => SolidColorBrush.Parse("#ea4747"),     // red

                _ => SolidColorBrush.Parse("#242832")                    // panel background
            };
        }
        return SolidColorBrush.Parse("#242832");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
