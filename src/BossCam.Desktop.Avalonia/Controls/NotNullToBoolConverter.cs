using System.Globalization;
using Avalonia.Data.Converters;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Converts a reference to a bool: non-null → true, null → false. Used to toggle the
/// fullscreen menu sheet's visibility off a nullable ActiveMenu property.
/// </summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public static readonly NotNullToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
