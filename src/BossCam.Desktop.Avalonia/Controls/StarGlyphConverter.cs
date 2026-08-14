using System.Globalization;
using Avalonia.Data.Converters;
using BossCam.Contracts;

namespace BossCam.Desktop.Avalonia.Controls;

/// <summary>
/// Multi-value converter mapping <c>(DeviceIdentity, starred ids)</c> to the star
/// glyph shown on a camera row: gold filled ★ when the camera is starred (pinned to
/// the landing board), hollow ☆ otherwise. Exposed as a singleton for
/// <c>{x:Static c:StarGlyphConverter.Instance}</c> inside a MultiBinding.
/// </summary>
public sealed class StarGlyphConverter : IMultiValueConverter
{
    public static readonly StarGlyphConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is { Count: >= 2 }
            && values[0] is DeviceIdentity device
            && values[1] is IReadOnlyCollection<Guid> starred)
        {
            return starred.Contains(device.Id) ? "★" : "☆";
        }

        return "☆";
    }

    public object[]? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
