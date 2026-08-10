using Avalonia.Data.Converters;
using Avalonia.Media;
using BackloggdMirror.Models;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Toast icon, as inline Material Design path data. Kept as geometry literals rather than image
/// assets so the icons scale and recolour with the rest of the UI.
/// </summary>
public class ToastTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastType type)
        {
            return type switch
            {
                // check-circle
                ToastType.Success => StreamGeometry.Parse("M12 2C6.5 2 2 6.5 2 12S6.5 22 12 22 22 17.5 22 12 17.5 2 12 2M10 17L5 12L6.41 10.59L10 14.17L17.59 6.58L19 8L10 17Z"),
                // information-circle, matching the blue background: these warnings inform rather than alarm
                ToastType.Warning => StreamGeometry.Parse("M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z"),
                // alert-circle
                ToastType.Error => StreamGeometry.Parse("M13 13H11V7H13M13 15V17H11V15M12 2A10 10 0 002 12 10 10 0 0012 22 10 10 0 0022 12 10 10 0 0012 2Z"),
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
