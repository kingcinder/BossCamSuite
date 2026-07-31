using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using BossCam.Contracts;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Converts a <see cref="ConnectivityStatus"/> to a status brush: Healthy →
/// green, Degraded → amber, Offline/Unknown → grey/red. Exposed as a singleton
/// for <c>{x:Static c:ConnectivityBrushConverter.Instance}</c>.
/// </summary>
public sealed class ConnectivityBrushConverter : IValueConverter
{
    public static readonly ConnectivityBrushConverter Instance = new();

    private static readonly SolidColorBrush HealthyBrush = new(Color.Parse("#4caf6a"));
    private static readonly SolidColorBrush DegradedBrush = new(Color.Parse("#d9a13b"));
    private static readonly SolidColorBrush OfflineBrush = new(Color.Parse("#d9554f"));
    private static readonly SolidColorBrush UnknownBrush = new(Color.Parse("#8a8aa8"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ConnectivityStatus.Healthy => HealthyBrush,
            ConnectivityStatus.Degraded => DegradedBrush,
            ConnectivityStatus.Offline => OfflineBrush,
            _ => UnknownBrush
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
