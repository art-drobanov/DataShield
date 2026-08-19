using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DataShield.Gui;

/// <summary>
/// Видимость поля избыточности: видно только в режиме Encode.
/// </summary>
public sealed class EccVisibilityConverter : IValueConverter
{
    /// <summary>WorkMode.Encode → true, иначе false.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is WorkMode mode && mode == WorkMode.Encode;
    }

    /// <summary>Обратное преобразование не поддерживается.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
}
