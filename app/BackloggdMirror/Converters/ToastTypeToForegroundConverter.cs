using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Toast text colour. Always white: all three backgrounds are dark enough to carry white text, so
/// the switch exists only to keep the per-type structure of the other toast converters.
/// </summary>
public class ToastTypeToForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastType type)
        {
            return type switch
            {
                ToastType.Success => Brushes.White,
                ToastType.Warning => Brushes.White,
                ToastType.Error => Brushes.White,
                _ => Brushes.White
            };
        }
        return Brushes.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
