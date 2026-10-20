using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace DotCharter.UI.Converters;

/// <summary>
///     A contract's PNG bytes (cover art) as an image source. A contract may not hold an
///     Avalonia <c>Bitmap</c>, so the decode happens here; bytes that do not decode show
///     nothing rather than failing the view. One way.
/// </summary>
public sealed class PngBytesToBitmapConverter : IValueConverter
{
    public static readonly PngBytesToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } png) return null;
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
