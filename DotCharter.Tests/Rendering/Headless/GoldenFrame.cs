using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Structural golden-frame helpers for the headless renderers. Deliberately
///     NOT byte-exact pixel equality: GPU rasterization (MSAA, blend rounding,
///     driver filtering) varies frame-identical scenes by a few LSBs across
///     vendors and driver versions, so a byte compare is flaky by construction.
///     Instead we assert STRUCTURAL properties that are stable under that
///     variance — non-black coverage, presence of saturated colour clusters,
///     region-to-region difference — with explicit tolerances. Byte-exact
///     comparison is reserved for values that are genuinely stable (e.g. the
///     readback dimensions), never rendered pixels.
///     <para>
///         All buffers are tightly-packed BGRA8, top row first, as returned by
///         <see cref="HeadlessHighwayRenderer" /> / the timeline renderer.
///     </para>
/// </summary>
public static class GoldenFrame
{
    /// <summary>GPU-dependent tests gate on this (the repo's env-gating
    /// convention). CI-less machines skip; set to "1" to run on the GPU, or to
    /// "sw" to run on the software rasterizer (<see cref="SoftwareVulkanLane" />,
    /// which this points the process's Vulkan loader at before the first
    /// renderer is built).</summary>
    public static bool GpuTestsEnabled
    {
        get
        {
            var lane = SoftwareVulkanLane.Current;
            SoftwareVulkanLane.Apply(lane);
            return lane.Lane != GpuTestLane.Off;
        }
    }

    /// <summary>When set (<c>DOTCHARTER_GPU_DUMP=1</c>) tests always write their
    /// rendered frame as a PNG under <c>TestResults/headless/</c> for eyeballing,
    /// not only on failure.</summary>
    public static bool DumpFrames =>
        Environment.GetEnvironmentVariable("DOTCHARTER_GPU_DUMP") == "1";

    /// <summary>Writes the frame only when <see cref="DumpFrames" /> is on.</summary>
    public static void Dump(byte[] bgra, int width, int height, string name)
    {
        if (DumpFrames) Attach(bgra, width, height, name);
    }

    /// <summary>Fraction (0..1) of pixels whose brightest channel exceeds
    /// <paramref name="threshold" /> — i.e. that are not (near-)black. A frame
    /// that rendered anything at all clears this comfortably; an all-black
    /// (failed) frame reads ~0.</summary>
    public static double NonBlackRatio(byte[] bgra, int threshold = 12)
    {
        long hits = 0;
        var count = bgra.Length / 4;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var max = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            if (max > threshold) hits++;
        }

        return count == 0 ? 0 : (double)hits / count;
    }

    /// <summary>Fraction (0..1) of pixels that are visibly SATURATED — the
    /// spread between their brightest and darkest channel exceeds
    /// <paramref name="spread" />. String lanes and note gems are saturated
    /// colours; a grey board/background is not, so this measures "coloured
    /// content present" without pinning exact hues.</summary>
    public static double ColorfulRatio(byte[] bgra, int spread = 40)
    {
        long hits = 0;
        var count = bgra.Length / 4;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var max = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            var min = Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2]));
            if (max - min > spread) hits++;
        }

        return count == 0 ? 0 : (double)hits / count;
    }

    /// <summary>Fraction (0..1) of pixels that differ between two same-size
    /// frames by more than <paramref name="perChannelTol" /> on any channel.
    /// A small tolerance absorbs AA jitter so only real content/framing changes
    /// register — used to prove two renders are distinct (or identical).</summary>
    public static double DifferenceRatio(byte[] a, byte[] b, int perChannelTol = 8)
    {
        if (a.Length != b.Length) return 1.0;
        long diff = 0;
        var count = a.Length / 4;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > perChannelTol ||
                Math.Abs(a[i + 1] - b[i + 1]) > perChannelTol ||
                Math.Abs(a[i + 2] - b[i + 2]) > perChannelTol)
            {
                diff++;
            }
        }

        return count == 0 ? 0 : (double)diff / count;
    }

    /// <summary>The spread (max − min) of luminance down a single pixel column.
    /// A vertical gradient (sky/fog fading toward the horizon) has a large
    /// spread; a flat opaque vertical "edge band" (the regression this guards)
    /// has ~0. Luminance is a cheap integer Rec.601-ish mix.</summary>
    public static int ColumnLuminanceSpread(byte[] bgra, int width, int height, int x)
    {
        var min = 255;
        var max = 0;
        for (var y = 0; y < height; y++)
        {
            var i = (y * width + x) * 4;
            var lum = (bgra[i + 2] * 30 + bgra[i + 1] * 59 + bgra[i] * 11) / 100; // R,G,B weights on BGRA
            if (lum < min) min = lum;
            if (lum > max) max = lum;
        }

        return max - min;
    }

    /// <summary>Non-black coverage restricted to a sub-rectangle (fractions of
    /// the frame). Lets a test assert "the board/near strings region has
    /// content" without caring about the empty upper sky.</summary>
    public static double NonBlackRatioInRegion(byte[] bgra, int width, int height,
        double x0, double y0, double x1, double y1, int threshold = 12)
    {
        var px0 = (int)(x0 * width);
        var px1 = (int)(x1 * width);
        var py0 = (int)(y0 * height);
        var py1 = (int)(y1 * height);
        long hits = 0;
        long count = 0;
        for (var y = py0; y < py1; y++)
        {
            for (var x = px0; x < px1; x++)
            {
                var i = (y * width + x) * 4;
                var max = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
                if (max > threshold) hits++;
                count++;
            }
        }

        return count == 0 ? 0 : (double)hits / count;
    }

    /// <summary>
    ///     Bounding box (in pixels) of everything that differs between two
    ///     same-size frames, plus how many pixels that was. Null when nothing
    ///     differs.
    ///     <para>
    ///         The point is ISOLATION: render a scene twice, changing exactly one
    ///         thing, and the differing pixels ARE that thing — so its position on
    ///         screen becomes assertable without knowing its colour, and without
    ///         re-deriving the projection in the test. Used to prove a 3D text
    ///         label rendered AND landed inside the viewport, which is what three
    ///         releases of invisible chord names needed and did not have.
    ///     </para>
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1, int Pixels)? DifferenceBounds(
        byte[] a, byte[] b, int width, int height, int perChannelTol = 8)
    {
        if (a.Length != b.Length) return null;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        var pixels = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Abs(a[i] - b[i]) <= perChannelTol &&
                    Math.Abs(a[i + 1] - b[i + 1]) <= perChannelTol &&
                    Math.Abs(a[i + 2] - b[i + 2]) <= perChannelTol)
                {
                    continue;
                }

                pixels++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }

        return pixels == 0 ? null : (x0, y0, x1, y1, pixels);
    }

    /// <summary>
    ///     Bounding box (in pixels) of every pixel whose ALPHA is below
    ///     <paramref name="minAlpha" />, plus how many there were. Null when the
    ///     frame is fully opaque.
    ///     <para>
    ///         <b>The alpha byte was this file's blind spot.</b> Every other
    ///         helper here reads B, G and R and skips index +3, so no golden frame
    ///         could ever see that the 3D scene pipelines were overwriting the
    ///         frame's alpha with each draw's own (<c>dstA = srcA</c>). The
    ///         colour output is identical either way, so the whole suite passed
    ///         while the presented frame was transparent everywhere the vignette
    ///         and the note glows had drawn. See <c>FrameAlphaPolicy</c>.
    ///     </para>
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1, int Pixels)? NonOpaqueBounds(
        byte[] bgra, int width, int height, byte minAlpha = 255)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        var pixels = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bgra[(y * width + x) * 4 + 3] >= minAlpha) continue;

                pixels++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }

        return pixels == 0 ? null : (x0, y0, x1, y1, pixels);
    }

    /// <summary>
    ///     Composites <paramref name="bgra" /> over a flat backdrop using the
    ///     frame's OWN alpha (premultiplied "over"), i.e. exactly what a
    ///     compositor does with the presented image. On an opaque frame this is
    ///     the identity; on a frame with a broken alpha channel it renders the
    ///     artifact the user actually sees, which makes a failure dump readable
    ///     instead of a uniformly dark picture.
    /// </summary>
    public static byte[] CompositeOverBackdrop(byte[] bgra, byte b, byte g, byte r)
    {
        var outp = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var t = 1.0 - bgra[i + 3] / 255.0;
            outp[i] = (byte)Math.Min(255, bgra[i] + b * t);
            outp[i + 1] = (byte)Math.Min(255, bgra[i + 1] + g * t);
            outp[i + 2] = (byte)Math.Min(255, bgra[i + 2] + r * t);
            outp[i + 3] = 255;
        }

        return outp;
    }

    /// <summary>Writes the frame as a PNG under the test output's
    /// <c>TestResults/headless/</c> so a failure leaves an inspectable artifact.
    /// Returns the path written.</summary>
    public static string Attach(byte[] bgra, int width, int height, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "headless");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}.png");
        HeadlessHighwayRenderer.SavePng(bgra, width, height, path);
        return path;
    }
}
