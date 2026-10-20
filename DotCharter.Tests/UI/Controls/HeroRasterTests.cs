using DotCharter.UI.Controls.Hero;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The Home hero's image, ported from the owner's "DotCharter — Start screen" mockup: its
///     palette, its LED wall, its aurora and its beams, each against a claim the mockup makes.
/// </summary>
public sealed class HeroRasterTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = 255;
        }

        return px;
    }

    [Fact]
    public void AGreyCover_FallsBackToTheAccent()
    {
        var palette = HeroColor.Extract(Solid(64, 64, 120, 120, 120), 64, 64);
        Assert.True(palette.IsMono);
        Assert.Equal(HeroColor.Accent, palette.C1);
    }

    [Fact]
    public void AColourfulCover_LeadsWithItsHeaviestHue_FittedIntoTheAppsLook()
    {
        // Three quarters red, one quarter blue.
        var px = Solid(40, 40, 220, 30, 40);
        for (var y = 30; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 4;
                px[i] = 30;
                px[i + 1] = 60;
                px[i + 2] = 220;
            }
        }

        var palette = HeroColor.Extract(px, 40, 40);
        Assert.False(palette.IsMono);
        Assert.True(palette.C1.R > palette.C1.B, $"c1 {palette.C1} is not the red");
        Assert.True(palette.C2.B > palette.C2.R, $"c2 {palette.C2} is not the blue");
        var (_, s, l) = HeroColor.ToHsl(palette.C1.R, palette.C1.G, palette.C1.B);
        Assert.InRange(l, 0.5, 0.75);
        Assert.InRange(s, 0.3, 0.85);
    }

    [Fact]
    public void TheLedWall_SizesADotByItsBrightness_AndFadesOutAwayFromTheUpperRight()
    {
        // Left half black, right half white.
        var cover = Solid(32, 32, 0, 0, 0);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 16; x < 32; x++)
            {
                var i = (y * 32 + x) * 4;
                cover[i] = cover[i + 1] = cover[i + 2] = 255;
            }
        }

        const int w = 240, h = 120;
        var (wall, lit) = HeroRaster.BuildLedWall(cover, 32, 32, w, h, 12, HeroPalette.Default);

        static int Covered(byte[] bgra, int x0, int y0) =>
            Enumerable.Range(0, 12 * 12).Count(k => bgra[((y0 + k / 12) * w + x0 + k % 12) * 4 + 3] > 0);

        // Same distance from the mask centre (0.75 w, 0.3 h) so the mask does not decide it.
        var bright = Covered(lit, 168, 36);
        var dark = Covered(lit, 108, 36);
        Assert.True(bright > dark, $"a white cell lights {bright} px against a black cell's {dark}");
        Assert.True(Covered(wall, 180, 36) > 0);
        Assert.Equal(1, HeroRaster.LedMask(0.75 * w, 0.3 * h, w, h), 6);
        Assert.Equal(0, HeroRaster.LedMask(-w, h * 2, w, h), 6);
    }

    [Fact]
    public void TheAurora_IsOpaque_AndDrifts()
    {
        var a = new byte[HeroRaster.AuroraWidth * HeroRaster.AuroraHeight * 4];
        var b = new byte[a.Length];
        HeroRaster.RenderAurora(a, 0, 0, HeroPalette.Default, 2.4);
        HeroRaster.RenderAurora(b, 4000, 0, HeroPalette.Default, 2.4);
        for (var i = 3; i < a.Length; i += 4) Assert.Equal(255, a[i]);
        Assert.False(a.AsSpan().SequenceEqual(b), "the aurora did not move in four seconds");
    }

    [Fact]
    public void ABeam_IsNarrowAtTheTop_AndGoneBelowItsFade()
    {
        var beam = HeroRaster.RenderBeam(100, 200, HeroColor.Accent, 2);
        static int Row(byte[] px, int y) => Enumerable.Range(0, 100).Count(x => px[(y * 100 + x) * 4 + 3] > 0);
        Assert.True(Row(beam, 2) < Row(beam, 120), "the beam does not widen downward");
        Assert.Equal(0, Row(beam, 170));
    }

    [Fact]
    public void ABeam_SwaysBetweenPlusAndMinusSixteenDegrees()
    {
        Assert.Equal(-16, HeroRaster.BeamAngle(0, 10, 0), 6);
        Assert.Equal(16, HeroRaster.BeamAngle(10, 10, 0), 6);
        Assert.Equal(-16, HeroRaster.BeamAngle(20, 10, 0), 6);
        Assert.Equal(0, HeroRaster.BeamAngle(5, 10, 0), 3);
        Assert.Equal(0.5, HeroRaster.EaseInOut(0.5), 3);
        Assert.True(HeroRaster.EaseInOut(0.1) < 0.1, "ease-in-out starts slow");
    }

    [Fact]
    public void ACoverFile_DecodesToStraightRgba()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-hero-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new SkiaSharp.SKBitmap(8, 6))
            {
                bitmap.Erase(new SkiaSharp.SKColor(200, 40, 30));
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
            }

            var (rgba, w, h) = HeroStage.DecodeRgba(path);
            Assert.NotNull(rgba);
            Assert.Equal((8, 6), (w, h));
            Assert.Equal(200, rgba![0]);
            Assert.Equal(30, rgba[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The band loops without a jump: at both ends of its sweep no corner of the wall
    /// is inside it (owner, 2026-09-24: "doesn't loop seamlessly").</summary>
    [Theory]
    [InlineData(1490, 930)]
    [InlineData(800, 420)]
    [InlineData(2400, 1300)]
    public void TheScanBand_IsOffScreenWhereItsLoopWraps(double w, double h)
    {
        var (from, to) = HeroRaster.BandSweepRange(w, h);
        foreach (var x in new[] { from, to })
        {
            foreach (var (cx, cy) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), (w, h) })
            {
                var t = HeroRaster.BandPosition(cx, cy, x, h);
                Assert.True(HeroRaster.BandAlpha(t) == 0, $"corner ({cx}, {cy}) is {t:0.00} across the band at x = {x:0}");
            }
        }

        Assert.Equal(from, HeroRaster.BandSweepX(0, w, h), 6);
        var mid = HeroRaster.BandSweepX(1000, w, h);
        Assert.InRange(mid, from, to);
    }
}
