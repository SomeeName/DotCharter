namespace DotCharter.UI.Controls.Hero;

/// <summary>An RGB colour with 0–255 channels kept as doubles, the way the mockup's maths keeps them.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb Mix(Rgb a, Rgb b, double t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
}

/// <summary>The two colours the hero takes from its cover. <see cref="IsMono" />: the cover had
/// too little colour, so the app's accent stands in.</summary>
public sealed record HeroPalette(Rgb C1, Rgb C2, bool IsMono)
{
    public static HeroPalette Default { get; } = new(HeroColor.Accent, HeroColor.AccentDeep, true);
}

/// <summary>
///     The Home hero's colour maths, ported from the owner's "DotCharter — Start screen" mockup:
///     the theme constants, HSL, the fit that keeps any cover colour inside the app's
///     look, and the palette extraction.
/// </summary>
public static class HeroColor
{
    public static readonly Rgb Bg = new(17, 19, 23);
    public static readonly Rgb Surface = new(23, 25, 28);
    public static readonly Rgb Accent = new(122, 182, 255);
    public static readonly Rgb AccentDeep = new(58, 96, 150);
    public static readonly Rgb White = new(255, 255, 255);

    public static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        r /= 255;
        g /= 255;
        b /= 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new Rgb(Channel(p, q, h + 1.0 / 3) * 255, Channel(p, q, h) * 255, Channel(p, q, h - 1.0 / 3) * 255);
    }

    /// <summary>Pushes any cover colour into the app's look: never muddy, never neon.</summary>
    public static Rgb FitToTheme(Rgb rgb)
    {
        var (h, s, l) = ToHsl(rgb.R, rgb.G, rgb.B);
        var fitted = FromHsl(h, Math.Clamp(s, 0.45, 0.8), Math.Clamp(l, 0.55, 0.7));
        return Rgb.Mix(fitted, Accent, 0.2);
    }

    /// <summary>
    ///     Shrinks the cover to 40 × 40, sorts the vivid pixels into 12 hue bins and takes the two
    ///     heaviest. A black-and-white cover falls back to the accent. The pixels are straight
    ///     (not premultiplied) RGBA.
    /// </summary>
    public static HeroPalette Extract(ReadOnlySpan<byte> rgba, int width, int height)
    {
        const int size = 40;
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return HeroPalette.Default;

        var sample = HeroRaster.Resample(rgba, width, height, 0, 0, width, height, size, size);
        var weight = new double[12];
        var sumR = new double[12];
        var sumG = new double[12];
        var sumB = new double[12];
        double total = 0;
        for (var i = 0; i < size * size; i++)
        {
            double r = sample[i * 4], g = sample[i * 4 + 1], b = sample[i * 4 + 2];
            var (h, s, l) = ToHsl(r, g, b);
            var w = s * (1 - Math.Abs(l - 0.5) * 2);
            if (w < 0.08) continue;
            var bin = (int)Math.Floor(h * 12) % 12;
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
            total += w;
        }

        int first = -1, second = -1;
        for (var bin = 0; bin < 12; bin++)
        {
            if (weight[bin] <= 0) continue;
            if (first < 0 || weight[bin] > weight[first])
            {
                second = first;
                first = bin;
            }
            else if (second < 0 || weight[bin] > weight[second])
            {
                second = bin;
            }
        }

        if (first < 0 || total / (size * size) < 0.04) return HeroPalette.Default;

        Rgb Average(int bin) => new(sumR[bin] / weight[bin], sumG[bin] / weight[bin], sumB[bin] / weight[bin]);
        var c1 = FitToTheme(Average(first));
        var c2 = second >= 0 && weight[second] > weight[first] * 0.25
            ? FitToTheme(Average(second))
            : Rgb.Mix(c1, AccentDeep, 0.55);
        return new HeroPalette(c1, c2, false);
    }
}
