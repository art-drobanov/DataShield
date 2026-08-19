using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DataShield.Gui;

/// <summary>Конвертация enum ↔ bool для RadioButton-привязок.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    /// <summary>Значение enum равно параметру → true (флажок установлен).</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value.Equals(Enum.Parse(value.GetType(), parameter.ToString()!));
    }

    /// <summary>Установленный флажок → значение enum из параметра.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null)
            return Enum.Parse(targetType, parameter.ToString()!);
        return AvaloniaProperty.UnsetValue;
    }
}
