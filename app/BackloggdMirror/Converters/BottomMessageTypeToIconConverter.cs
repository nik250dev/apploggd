using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Icon for the bottom message bar. Null for None and Loading — Loading shows a spinner instead,
/// which the view swaps in via a separate visibility binding.
/// </summary>
public class BottomMessageTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is BottomMessageType type)
        {
            return type switch
            {
                BottomMessageType.Success => StreamGeometry.Parse("M12 2C6.5 2 2 6.5 2 12S6.5 22 12 22 22 17.5 22 12 17.5 2 12 2M10 17L5 12L6.41 10.59L10 14.17L17.59 6.58L19 8L10 17Z"),
                BottomMessageType.Warning => StreamGeometry.Parse("M18.295,3.895L1.203,34.555C-0.219,37.146,0.385,39.5,4.228,39.5H36.77c3.854,0,4.447-2.354,3.025-4.945L22.35,3.914 C21.996,3.223,21.482,2.49,20.393,2.5C19.233,2.521,18.658,3.203,18.295,3.895z M18.5,13.5h4v14h-4V13.5z M18.5,30.5h4v4h-4V30.5z"),
                BottomMessageType.Error => StreamGeometry.Parse("M13 13H11V7H13M13 15V17H11V15M12 2A10 10 0 002 12 10 10 0 0012 22 10 10 0 0022 12 10 10 0 0012 2Z"),
                _ => null
            };
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
