using System.Globalization;
using Avalonia.Data.Converters;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Converts an int/count to a bool: 0 → true, anything else → false. Used to show the
/// empty-board hint on the Live View landing board when no cameras are available yet.
/// </summary>
public sealed class ZeroToBoolConverter : IValueConverter
{
    public static readonly ZeroToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int i => i == 0,
            long l => l == 0,
            _ => false
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
