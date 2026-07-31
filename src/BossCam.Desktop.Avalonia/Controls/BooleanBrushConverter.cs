using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Converts a boolean to a status brush (true → green, false → grey) so list
/// rows can color-code running/stopped jobs inline without per-row converters.
/// Exposed as a singleton for <c>{x:Static c:BooleanBrushConverter.Instance}</c>.
/// </summary>
public sealed class BooleanBrushConverter : IValueConverter
{
    public static readonly BooleanBrushConverter Instance = new();

    private static readonly SolidColorBrush RunningBrush = new(Color.Parse("#4caf6a"));
    private static readonly SolidColorBrush StoppedBrush = new(Color.Parse("#8a8aa8"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? RunningBrush : StoppedBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
