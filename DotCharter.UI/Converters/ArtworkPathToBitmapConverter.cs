using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Converters;

/// <summary>
///     A cover's file path, as a screen contract carries it, to the bitmap an
///     <c>Image</c> draws. Decoded through <see cref="IArtworkSource" /> and
///     <see cref="SongCoverImage" /> like the song browser's covers (capped, by
///     stream, no handle left open), and kept for the few covers Home shows so
///     a refresh does not decode them again. Null when there is no path or the
///     file will not decode, which leaves the caller's no-art look in place.
/// </summary>
public sealed class ArtworkPathToBitmapConverter : IValueConverter
{
    public static readonly ArtworkPathToBitmapConverter Instance = new();

    // A cover is a FILE key, which needs no profile service: the song browser builds its own the same way.
    private static readonly IArtworkSource Artwork = new ArtworkSource();

    private const int Capacity = 12;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        var bitmap = SongCoverImage.Load(Artwork, ArtworkKey.File(path));
        _cache[path] = bitmap;
        _order.Enqueue(path);
        // Dropped, not disposed: an Image may still be drawing it.
        while (_order.Count > Capacity) _cache.Remove(_order.Dequeue());
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
