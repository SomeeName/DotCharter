using DotCharter.Services.Infrastructure;
using Avalonia;
using Avalonia.Media.Imaging;

namespace DotCharter.UI.ViewModels.Application;

/// <summary>
///     Sizing policy for player-profile avatars, plus the two image operations
///     that follow from it: the display-resolution DECODE and the import-time
///     downscale.
///     <para>
///         Both exist because the picture is a file the user picked. A 12 MP phone
///         photo <c>File.Copy</c>'d into the profile folder verbatim and
///         then decoded with <c>new Bitmap(stream)</c> would cost 80-250 ms and ~48 MB of
///         RGBA <b>retained per profile</b>, for a circle that is at most 20 px
///         across. The status bar, the Play Mode front door and the switcher
///         flyout all hold one of these for as long as the profile list lives.
///     </para>
///     <para>
///         Lives in the UI layer on purpose, exactly like
///         <c>LanAvatarImage</c>: <c>DotCharter.Services</c> stays free of an
///         image dependency, so <c>PlayerProfileService</c> only ever copies
///         bytes.
///     </para>
/// </summary>
public static class ProfileAvatarImage
{
    /// <summary>Width every avatar is DECODED at. The largest circle that shows
    /// one is 20 px (Play Mode front door / switcher flyout; the status bar's is
    /// 14 px), so 96 still covers a 2× display scale on top of a 2× global UI
    /// scale.</summary>
    public const int DecodeWidth = 96;

    /// <summary>Width an imported picture is STORED at. Deliberately larger than
    /// <see cref="DecodeWidth" /> — the stored file outlives today's layout, and
    /// a profile export carries it — but small enough that the profile folder
    /// never holds a multi-megapixel photo.</summary>
    public const int StoredWidth = 256;

    /// <summary>Pure policy: the width to re-encode a picked picture at, or
    /// <c>null</c> when it is already small enough to store verbatim. It NEVER
    /// upscales — re-encoding a small picture larger costs quality and bytes and
    /// buys nothing — and a verbatim store also preserves an already-small
    /// source's own format.</summary>
    public static int? StoredWidthFor(int sourcePixelWidth)
        => sourcePixelWidth > StoredWidth ? StoredWidth : null;

    /// <summary>Height that keeps <paramref name="sourceSize" />'s aspect ratio
    /// at <paramref name="targetWidth" />, never below 1 px.</summary>
    public static int ScaledHeightFor(PixelSize sourceSize, int targetWidth)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return 1;
        return Math.Max(1, (int)Math.Round(sourceSize.Height * (targetWidth / (double)sourceSize.Width)));
    }

    /// <summary>
    ///     Decodes a stored avatar at <see cref="DecodeWidth" />. Through a STREAM
    ///     (not <c>new Bitmap(path)</c>) so no file handle stays open — SetAvatar
    ///     overwrites the file in place when the user picks a new picture. Null for
    ///     a missing/unreadable/corrupt image; the caller falls back to the accent
    ///     dot.
    /// </summary>
    public static Bitmap? Load(IArtworkSource artwork, ArtworkKey key)
    {
        try
        {
            using var stream = artwork.Open(key);
            if (stream is null) return null;
            if (!DecodeGuard(stream)) return null;
            var bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            // A header the probe could not read went to the decoder unchecked;
            // what came back is bounded after the fact, the only way it can be.
            if (bitmap.PixelSize.Height > MaxDecodeHeight)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The tallest bitmap <see cref="Load" /> will hand out. Every
    /// surface shows a circle, so anything past a 4:1 portrait is cropped to
    /// nothing useful anyway — and the width bound alone is not a bound: a
    /// 1×100,000 PNG is a few hundred bytes and decodes at width 96 to
    /// 96×9,600,000, ~3.7 GB of RGBA, before the width cap has done anything,
    /// and an avatar file is whatever the user picked.</summary>
    public const int MaxDecodeHeight = DecodeWidth * 4;

    /// <summary>Pure policy: would a source of this pixel size decode within
    /// <see cref="MaxDecodeHeight" /> at <see cref="DecodeWidth" />?</summary>
    public static bool IsDecodableSize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;
        // DecodeToWidth never upscales, so a narrow source decodes at its own
        // width and its height is what it is.
        var decodedHeight = sourceWidth <= DecodeWidth
            ? sourceHeight
            : (long)Math.Ceiling(sourceHeight * (DecodeWidth / (double)sourceWidth));
        return decodedHeight <= MaxDecodeHeight;
    }

    /// <summary>
    ///     The pre-decode half of <see cref="Load" />: reads the header through
    ///     <see cref="ImageHeaderProbe" /> and refuses a recognised raster that
    ///     would decode taller than <see cref="MaxDecodeHeight" /> — BEFORE the
    ///     decoder allocates for it. A header the probe does not recognise passes
    ///     (a user's small avatar in an exotic format still loads as it always
    ///     did) and is bounded by the post-decode check instead. The stream is
    ///     rewound, so the decoder reads from the start.
    /// </summary>
    internal static bool DecodeGuard(Stream stream)
        => !ImageHeaderProbe.TryReadPixelSize(stream, out var w, out var h) || IsDecodableSize(w, h);

    /// <summary>
    ///     Prepares a picked picture for <c>IPlayerProfileService.SetAvatar</c>:
    ///     when it is larger than <see cref="StoredWidth" /> it is downscaled into
    ///     a temp PNG staged by <paramref name="artwork" /> and THAT path is
    ///     returned (the caller deletes it with <see cref="IArtworkSource.DeleteStaged" />
    ///     after the copy); otherwise — and on any read/encode failure — the
    ///     ORIGINAL path comes back, so a format we cannot open still lands
    ///     verbatim, unchanged.
    /// </summary>
    public static string PrepareForImport(string sourcePath, IArtworkSource artwork)
    {
        try
        {
            Bitmap source;
            using (var input = artwork.Open(ArtworkKey.File(sourcePath)))
            {
                if (input is null) return sourcePath;
                source = new Bitmap(input);
            }

            using (source)
            {
                if (StoredWidthFor(source.PixelSize.Width) is not { } width) return sourcePath;

                using var scaled = source.CreateScaledBitmap(
                    new PixelSize(width, ScaledHeightFor(source.PixelSize, width)),
                    BitmapInterpolationMode.HighQuality);
                string temp;
                using (var output = artwork.CreateStaged(".png", out temp))
                    scaled.Save(output, PngBitmapEncoderOptions.Default);
                return temp;
            }
        }
        catch
        {
            return sourcePath;
        }
    }
}
