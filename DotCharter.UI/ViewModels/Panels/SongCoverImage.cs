using Avalonia;
using Avalonia.Media.Imaging;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     Sizing policy for song cover art, plus the decode that follows from it.
///     <para>
///         A cover is a file a CHARTER shipped: typical Rocksmith art is 256²,
///         but hand-built packages carry arbitrary PNGs, and the old
///         <c>new Bitmap(path)</c> decode retained whatever arrived — a 1024²
///         cover is ~4 MB of RGBA for a list cell that is 32/52 px across. The
///         largest surface that ever shows one is the browser detail pane at
///         278 logical px, so <see cref="MaxDecodeWidth" /> covers that at a 2×
///         display scale and everything bigger is downscaled once, off the UI
///         thread, at decode time.
///     </para>
///     <para>
///         Same split as <c>ProfileAvatarImage</c>: the POLICY here is pure and
///         tested in the default gate; the pixel work needs a Skia platform the
///         gate deliberately does not boot, so the wiring is a source lint
///         (<c>SongCoverImageTests</c>).
///     </para>
/// </summary>
public static class SongCoverImage
{
    /// <summary>Widest any cover is RETAINED at. The browser detail pane is the
    /// largest cover surface (278 logical px, square-ish); 640 covers it at a 2×
    /// display scale with room for the global UI scale. Typical 256² art is far
    /// below this and stays verbatim.</summary>
    public const int MaxDecodeWidth = 640;

    /// <summary>Pure policy: the width to downscale a decoded cover to, or
    /// <c>null</c> when it is already small enough to keep verbatim. NEVER
    /// upscales — which is why the decode below cannot be a bare
    /// <c>Bitmap.DecodeToWidth</c>: that call resizes to the requested width in
    /// BOTH directions, so a cap-sized decode of a normal 256² cover would have
    /// tripled its retained bytes.</summary>
    public static int? DecodeWidthFor(int sourcePixelWidth)
        => sourcePixelWidth > MaxDecodeWidth ? MaxDecodeWidth : null;

    /// <summary>Height that keeps <paramref name="sourceSize" />'s aspect ratio
    /// at <paramref name="targetWidth" />, never below 1 px.</summary>
    public static int ScaledHeightFor(PixelSize sourceSize, int targetWidth)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return 1;
        return Math.Max(1, (int)Math.Round(sourceSize.Height * (targetWidth / (double)sourceSize.Width)));
    }

    /// <summary>
    ///     Decodes a cover, downscaled to at most <see cref="MaxDecodeWidth" />.
    ///     The full-size decode is transient (Avalonia exposes no header probe, so
    ///     the source size is only known after decoding) — the caller runs this on
    ///     a pool thread and only the capped bitmap is retained. Through a STREAM
    ///     so no file handle stays open on the extracted-cover cache. Null for a
    ///     missing/corrupt image; the row keeps its placeholder icon.
    /// </summary>
    public static Bitmap? Load(IArtworkSource artwork, ArtworkKey key)
    {
        try
        {
            Bitmap full;
            using (var stream = artwork.Open(key))
            {
                if (stream is null) return null;
                full = new Bitmap(stream);
            }

            if (DecodeWidthFor(full.PixelSize.Width) is not { } width) return full;

            using (full)
            {
                return full.CreateScaledBitmap(
                    new PixelSize(width, ScaledHeightFor(full.PixelSize, width)),
                    BitmapInterpolationMode.HighQuality);
            }
        }
        catch
        {
            return null;
        }
    }
}
