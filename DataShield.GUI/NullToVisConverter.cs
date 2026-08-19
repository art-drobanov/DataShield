using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DataShield.Gui;

/// <summary>Видимость элемента при непустой строке: пусто → false.</summary>
public sealed class NullToVisConverter : IValueConverter
{
    /// <summary>Непустая строка → true, иначе false.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value as string);
    }

    /// <summary>Обратное преобразование не поддерживается.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
}
