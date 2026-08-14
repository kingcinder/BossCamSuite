using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Converts a <see cref="ServiceConnectionStatus"/> to a status brush: Online →
/// green, Starting → amber, Offline → red. Exposed as a singleton for
/// <c>{x:Static c:ServiceConnectionBrushConverter.Instance}</c>.
/// </summary>
public sealed class ServiceConnectionBrushConverter : IValueConverter
{
    public static readonly ServiceConnectionBrushConverter Instance = new();

    private static readonly SolidColorBrush OnlineBrush = new(Color.Parse("#4caf6a"));
    private static readonly SolidColorBrush StartingBrush = new(Color.Parse("#d9a13b"));
    private static readonly SolidColorBrush OfflineBrush = new(Color.Parse("#d9554f"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ServiceConnectionStatus.Online => OnlineBrush,
            ServiceConnectionStatus.Starting => StartingBrush,
            _ => OfflineBrush
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
