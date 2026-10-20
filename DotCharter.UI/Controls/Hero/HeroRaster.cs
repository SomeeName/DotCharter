namespace DotCharter.UI.Controls.Hero;

/// <summary>
///     The Home hero's pixel work, ported from the owner's "DotCharter — Start screen" mockup:
///     cover resampling, the LED wall, the aurora and the spotlight beam. Pure byte buffers, no
///     Avalonia types, so every layer is testable in the default gate. Outputs are premultiplied
///     BGRA, the layout an Avalonia <c>WriteableBitmap</c> takes.
/// </summary>
public static class HeroRaster
{
    /// <summary>Aurora buffer size: the mockup draws it at 96 × 54 and lets the upscale soften it.</summary>
    public const int AuroraWidth = 96;

    public const int AuroraHeight = 54;

    /// <summary>
    ///     Area-averages the source rectangle (<paramref name="sx" />, <paramref name="sy" />,
    ///     <paramref name="sw" />, <paramref name="sh" />, in source pixels, fractional allowed) into an
    ///     <paramref name="outW" /> × <paramref name="outH" /> straight RGBA buffer.
    /// </summary>
    public static byte[] Resample(ReadOnlySpan<byte> rgba, int width, int height,
        double sx, double sy, double sw, double sh, int outW, int outH)
    {
        var result = new byte[outW * outH * 4];
        for (var oy = 0; oy < outH; oy++)
        {
            var y0 = sy + sh * oy / outH;
            var y1 = sy + sh * (oy + 1) / outH;
            for (var ox = 0; ox < outW; ox++)
            {
                var x0 = sx + sw * ox / outW;
                var x1 = sx + sw * (ox + 1) / outW;
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                var ya = Math.Clamp((int)Math.Floor(y0), 0, height - 1);
                var yb = Math.Clamp((int)Math.Ceiling(y1) - 1, ya, height - 1);
                var xa = Math.Clamp((int)Math.Floor(x0), 0, width - 1);
                var xb = Math.Clamp((int)Math.Ceiling(x1) - 1, xa, width - 1);
                for (var y = ya; y <= yb; y++)
                {
                    for (var x = xa; x <= xb; x++)
                    {
                        var i = (y * width + x) * 4;
                        r += rgba[i];
                        g += rgba[i + 1];
                        b += rgba[i + 2];
                        a += rgba[i + 3];
                        n++;
                    }
                }

                var o = (oy * outW + ox) * 4;
                result[o] = (byte)(r / n + 0.5);
                result[o + 1] = (byte)(g / n + 0.5);
                result[o + 2] = (byte)(b / n + 0.5);
                result[o + 3] = (byte)(a / n + 0.5);
            }
        }

        return result;
    }

    /// <summary>
    ///     The mockup's CSS mask on the LED wall: an ellipse 85 % × 95 % of the hero at (75 %, 30 %),
    ///     opaque out to 35 % of its radius and fading to nothing at its edge.
    /// </summary>
    public static double LedMask(double x, double y, double width, double height)
    {
        var dx = (x - width * 0.75) / (width * 0.85);
        var dy = (y - height * 0.30) / (height * 0.95);
        var d = Math.Sqrt(dx * dx + dy * dy);
        return d <= 0.35 ? 1 : d >= 1 ? 0 : 1 - (d - 0.35) / 0.65;
    }

    /// <summary>
    ///     The LED wall: the cover sampled "object-fit: cover" into one cell per
    ///     <paramref name="pitch" />, each cell a dot whose SIZE is its brightness (halftone) and whose
    ///     colour is a duotone from the dark surface to the palette. <c>Base</c> is the resting wall,
    ///     <c>Lit</c> the brighter, larger dots the scan band and the spotlight reveal. The mask is
    ///     baked in, since it never moves. The output size and the pitch are in device pixels.
    /// </summary>
    public static (byte[] Base, byte[] Lit) BuildLedWall(ReadOnlySpan<byte> coverRgba, int coverW, int coverH,
        int width, int height, double pitch, HeroPalette palette)
    {
        var baseLayer = new byte[width * height * 4];
        var litLayer = new byte[width * height * 4];
        var cols = (int)Math.Ceiling(width / pitch);
        var rows = (int)Math.Ceiling(height / pitch);
        var scale = Math.Max(cols / (double)coverW, rows / (double)coverH);
        var cropW = cols / scale;
        var cropH = rows / scale;
        var cells = Resample(coverRgba, coverW, coverH, (coverW - cropW) / 2, (coverH - cropH) / 2, cropW, cropH,
            cols, rows);

        var dark = Rgb.Mix(HeroColor.Surface, palette.C2, 0.15);
        var bright = Rgb.Mix(palette.C1, HeroColor.White, 0.6);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var i = (row * cols + col) * 4;
                var lum = (0.2126 * cells[i] + 0.7152 * cells[i + 1] + 0.0722 * cells[i + 2]) / 255;
                var tone = lum < 0.55
                    ? Rgb.Mix(dark, palette.C1, lum / 0.55)
                    : Rgb.Mix(palette.C1, bright, (lum - 0.55) / 0.45);
                var cx = col * pitch + pitch / 2;
                var cy = row * pitch + pitch / 2;
                Dot(baseLayer, width, height, cx, cy, pitch / 2 * (0.22 + 0.6 * lum), tone, 0.85);
                Dot(litLayer, width, height, cx, cy, pitch / 2 * (0.32 + 0.6 * lum),
                    Rgb.Mix(tone, HeroColor.White, 0.35), 1);
            }
        }

        return (baseLayer, litLayer);
    }

    /// <summary>An anti-aliased disc, alpha scaled by <see cref="LedMask" />, written premultiplied.</summary>
    private static void Dot(byte[] bgra, int width, int height, double cx, double cy, double radius, Rgb color,
        double alpha)
    {
        var x0 = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + radius + 1));
        var y0 = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + radius + 1));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                var coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                if (coverage <= 0) continue;
                var a = alpha * coverage * LedMask(x + 0.5, y + 0.5, width, height);
                if (a <= 0) continue;
                var o = (y * width + x) * 4;
                bgra[o] = (byte)Math.Round(color.B * a);
                bgra[o + 1] = (byte)Math.Round(color.G * a);
                bgra[o + 2] = (byte)Math.Round(color.R * a);
                bgra[o + 3] = (byte)Math.Round(255 * a);
            }
        }
    }

    /// <summary>
    ///     One aurora frame into an opaque <see cref="AuroraWidth" /> × <see cref="AuroraHeight" /> BGRA
    ///     buffer: four soft blobs of the palette added onto the background, drifting on their own
    ///     sine clocks, then the CSS <c>blur()</c> and <c>saturate(1.2)</c>. The blur sigma is the
    ///     mockup's 38 px in aurora pixels.
    /// </summary>
    public static void RenderAurora(Span<byte> bgra, double timeMs, double pulse, HeroPalette palette,
        double blurSigma)
    {
        const int w = AuroraWidth, h = AuroraHeight;
        (Rgb Color, double X, double Y, double R, double Speed)[] blobs =
        [
            (palette.C1, 0.75, 0.25, 0.55, 0.00021),
            (palette.C2, 0.35, 0.35, 0.5, 0.00017),
            (HeroColor.Accent, 0.55, 0.8, 0.45, 0.00025),
            (palette.C1, 0.15, 0.7, 0.35, 0.00013)
        ];

        var rgb = _auroraRgb ??= new double[w * h * 3];
        for (var p = 0; p < w * h; p++)
        {
            rgb[p * 3] = HeroColor.Bg.R;
            rgb[p * 3 + 1] = HeroColor.Bg.G;
            rgb[p * 3 + 2] = HeroColor.Bg.B;
        }

        for (var index = 0; index < blobs.Length; index++)
        {
            var blob = blobs[index];
            var bx = w * (blob.X + Math.Sin(timeMs * blob.Speed + index * 1.7) * 0.15);
            var by = h * (blob.Y + Math.Cos(timeMs * blob.Speed * 1.3 + index) * 0.12);
            var radius = w * blob.R * (1 + pulse * 0.08);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var d = Math.Sqrt((x + 0.5 - bx) * (x + 0.5 - bx) + (y + 0.5 - by) * (y + 0.5 - by));
                    if (d >= radius) continue;
                    var a = 0.45 * (1 - d / radius);
                    var p = (y * w + x) * 3;
                    rgb[p] += blob.Color.R * a;
                    rgb[p + 1] += blob.Color.G * a;
                    rgb[p + 2] += blob.Color.B * a;
                }
            }
        }

        for (var i = 0; i < rgb.Length; i++) rgb[i] = Math.Min(255, rgb[i]);
        GaussianBlur(rgb, w, h, blurSigma);

        const double s = 1.2;
        for (var p = 0; p < w * h; p++)
        {
            double r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            var r2 = (0.213 + 0.787 * s) * r + (0.715 - 0.715 * s) * g + (0.072 - 0.072 * s) * b;
            var g2 = (0.213 - 0.213 * s) * r + (0.715 + 0.285 * s) * g + (0.072 - 0.072 * s) * b;
            var b2 = (0.213 - 0.213 * s) * r + (0.715 - 0.715 * s) * g + (0.072 + 0.928 * s) * b;
            bgra[p * 4] = (byte)Math.Clamp(b2 + 0.5, 0, 255);
            bgra[p * 4 + 1] = (byte)Math.Clamp(g2 + 0.5, 0, 255);
            bgra[p * 4 + 2] = (byte)Math.Clamp(r2 + 0.5, 0, 255);
            bgra[p * 4 + 3] = 255;
        }
    }

    // Scratch for the aurora, which runs every few frames: reused, never reallocated. Per thread,
    // so a test on another thread cannot share a half-written buffer.
    [ThreadStatic] private static double[]? _auroraRgb;
    [ThreadStatic] private static double[]? _blurTemp;

    /// <summary>A separable Gaussian over an RGB double buffer, edges clamped.</summary>
    private static void GaussianBlur(double[] rgb, int w, int h, double sigma)
    {
        if (sigma < 0.3) return;
        var kernel = Kernel(sigma);
        var radius = kernel.Length / 2;
        if (_blurTemp is null || _blurTemp.Length != rgb.Length) _blurTemp = new double[rgb.Length];
        var temp = _blurTemp;
        for (var pass = 0; pass < 2; pass++)
        {
            var src = pass == 0 ? rgb : temp;
            var dst = pass == 0 ? temp : rgb;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    double r = 0, g = 0, b = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sx = pass == 0 ? Math.Clamp(x + k, 0, w - 1) : x;
                        var sy = pass == 0 ? y : Math.Clamp(y + k, 0, h - 1);
                        var i = (sy * w + sx) * 3;
                        var kw = kernel[k + radius];
                        r += src[i] * kw;
                        g += src[i + 1] * kw;
                        b += src[i + 2] * kw;
                    }

                    var o = (y * w + x) * 3;
                    dst[o] = r;
                    dst[o + 1] = g;
                    dst[o + 2] = b;
                }
            }
        }
    }

    /// <summary>
    ///     One spotlight beam: a trapezoid from 8 % of the width at the top to all of it at the
    ///     bottom, its colour at 0.55 fading to nothing 78 % of the way down, edges softened like the
    ///     mockup's <c>blur(8px)</c>.
    /// </summary>
    public static byte[] RenderBeam(int width, int height, Rgb color, double softPx)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) / height;
            var fade = v >= 0.78 ? 0 : 0.55 * (1 - v / 0.78);
            if (fade <= 0) continue;
            var half = (0.04 + 0.46 * v) * width;
            for (var x = 0; x < width; x++)
            {
                var dx = Math.Abs(x + 0.5 - width / 2.0);
                var edge = Math.Clamp((half - dx) / softPx + 0.5, 0, 1);
                var a = fade * edge;
                if (a <= 0) continue;
                var o = (y * width + x) * 4;
                bgra[o] = (byte)Math.Round(color.B * a);
                bgra[o + 1] = (byte)Math.Round(color.G * a);
                bgra[o + 2] = (byte)Math.Round(color.R * a);
                bgra[o + 3] = (byte)Math.Round(255 * a);
            }
        }

        return bgra;
    }

    /// <summary>
    ///     A beam's sway: CSS <c>sweep</c> (-16° to 16°), <c>ease-in-out</c>, <c>alternate</c>, with a
    ///     negative delay, at <paramref name="seconds" />.
    /// </summary>
    public static double BeamAngle(double seconds, double duration, double delay)
    {
        var t = (seconds - delay) / duration;
        var cycle = Math.Floor(t);
        var f = t - cycle;
        if (((long)cycle & 1) == 1) f = 1 - f;
        return -16 + 32 * EaseInOut(f);
    }

    /// <summary>CSS <c>ease-in-out</c>, i.e. <c>cubic-bezier(0.42, 0, 0.58, 1)</c>.</summary>
    public static double EaseInOut(double x)
    {
        // Solve bx(t) = x for t by bisection, then return by(t).
        static double Bez(double t, double p1, double p2) =>
            3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;

        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (Bez(mid, 0.42, 0.58) < x) lo = mid;
            else hi = mid;
        }

        return Bez((lo + hi) / 2, 0, 1);
    }

    private static double[] Kernel(double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1];
        double sum = 0;
        for (var k = -radius; k <= radius; k++) sum += kernel[k + radius] = Math.Exp(-k * k / (2 * sigma * sigma));
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        return kernel;
    }

    /// <summary>
    ///     The scan band's opacity at <paramref name="t" /> across it: the mockup's gradient,
    ///     transparent → opaque at the middle → transparent, times its 0.8.
    /// </summary>
    public static double BandAlpha(double t) => t is <= 0 or >= 1 ? 0 : 0.8 * (1 - Math.Abs(t - 0.5) * 2);

    /// <summary>
    ///     A CSS <c>box-shadow</c> baked once into a premultiplied BGRA bitmap: the shape (a rectangle,
    ///     or the ellipse inscribed in it) at (<paramref name="x" />, <paramref name="y" />) filled with
    ///     <paramref name="color" /> at <paramref name="alpha" />, blurred by <paramref name="sigma" />
    ///     (CSS blur radius / 2). All in the bitmap's own pixels. Drawn as a bitmap it costs one blit,
    ///     where a live shadow re-blurs every frame the hero animates.
    /// </summary>
    public static byte[] RenderShadow(int width, int height, double x, double y, double w, double h, bool ellipse,
        double sigma, Rgb color, double alpha)
    {
        var a = new double[width * height];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                double cover;
                if (ellipse)
                {
                    var dx = (px + 0.5 - (x + w / 2)) / (w / 2);
                    var dy = (py + 0.5 - (y + h / 2)) / (h / 2);
                    cover = Math.Clamp((1 - Math.Sqrt(dx * dx + dy * dy)) * w / 2 + 0.5, 0, 1);
                }
                else
                {
                    var cx = Math.Clamp(Math.Min(px + 1 - x, x + w - px), 0, 1);
                    var cy = Math.Clamp(Math.Min(py + 1 - y, y + h - py), 0, 1);
                    cover = cx * cy;
                }

                a[py * width + px] = cover;
            }
        }

        var kernel = Kernel(Math.Max(0.3, sigma));
        var r = kernel.Length / 2;
        var temp = new double[a.Length];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                double v = 0;
                for (var k = -r; k <= r; k++) v += a[py * width + Math.Clamp(px + k, 0, width - 1)] * kernel[k + r];
                temp[py * width + px] = v;
            }
        }

        var bgra = new byte[width * height * 4];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                double v = 0;
                for (var k = -r; k <= r; k++) v += temp[Math.Clamp(py + k, 0, height - 1) * width + px] * kernel[k + r];
                var o = (py * width + px) * 4;
                var pa = v * alpha;
                bgra[o] = (byte)Math.Round(color.B * pa);
                bgra[o + 1] = (byte)Math.Round(color.G * pa);
                bgra[o + 2] = (byte)Math.Round(color.R * pa);
                bgra[o + 3] = (byte)Math.Round(255 * pa);
            }
        }

        return bgra;
    }

    /// <summary>
    ///     Where the scan band's gradient starts (its x at the top edge) at <paramref name="ms" />. The
    ///     mockup swept it from -30 % to 130 % of the width every 7 s, which wrapped while the tilted
    ///     band still crossed the lower part of the wall, so it visibly jumped. This sweeps it at the
    ///     mockup's speed (1.6 widths per 7 s) from fully off the left to fully off the right, and the
    ///     loop is seamless: at the wrap nothing of it is on screen.
    /// </summary>
    public static double BandSweepX(double ms, double width, double height)
    {
        var (from, to) = BandSweepRange(width, height);
        var speed = width * 1.6 / 7000;
        var period = (to - from) / speed;
        return from + ms % period / period * (to - from);
    }

    /// <summary>
    ///     The band's gradient runs from (x - 200, 0) to (x + 200, 0.4 h); it is visible between the two
    ///     lines through those points square to it. Those lines slope back by 0.4 h / 400 per pixel
    ///     down, so the band leans left as it goes down the wall.
    /// </summary>
    public static (double From, double To) BandSweepRange(double width, double height)
    {
        var lean = height * 0.4 / 400;
        return (-200 - height * 0.4 * lean, width + 200 + height * lean);
    }

    /// <summary>Where <paramref name="x" />, <paramref name="y" /> falls across the band whose gradient
    /// starts at <paramref name="sweepX" />: 0 and 1 are its transparent edges.</summary>
    public static double BandPosition(double x, double y, double sweepX, double height)
    {
        double dx = 400, dy = height * 0.4;
        return ((x - (sweepX - 200)) * dx + y * dy) / (dx * dx + dy * dy);
    }
}
