using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace BackloggdMirror.Converters;

/// <summary>
/// Flag shown next to each entry of the language picker. Drawn as vectors because Windows has no
/// glyphs for flag emoji.
/// </summary>
public class LanguageCodeToFlagConverter : IValueConverter
{
    public IImage? System { get; set; }
    public IImage? Spanish { get; set; }
    public IImage? English { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "es" => Spanish,
            "en" => English,
            _ => System
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
