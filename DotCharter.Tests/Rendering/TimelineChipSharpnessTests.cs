using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 2D timeline's lane chips (sections, phrases, lyrics, tones, measures) and the song map's
///     labels, after the owner found both still blurry and the chips worth improving (2026-09-24).
///     The chips' corners were triangle fans with no anti-aliasing and a border of four straight
///     strips, which notched every corner; under each sat an offset shadow. The map's labels were
///     9 px at fractional positions, so each glyph stem straddled two pixel columns.
/// </summary>
public sealed class TimelineChipSharpnessTests
{
    [Fact]
    public void AnAaRoundRect_IsABodyOfRectsAndFourAntiAliasedCornerDiscs()
    {
        var batcher = new TimelineBatcher();
        batcher.AddRoundRectAa(10, 20, 60, 20, 4, Color.FromRgb(165, 54, 178));
        batcher.FinalizeFrame();

        // Three body rects, six vertices each, in the solid stream…
        Assert.Equal(18, batcher.SolidVertexCount);
        // …and four disc sprites (type 1, the SDF circle), six vertices each, in the general stream.
        Assert.Equal(24, batcher.GeneralVertexCount);
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
            Assert.Equal(1f, batcher.GeneralBuffer[v * 9 + 8]);
    }

    [Theory]
    [InlineData(203.4, 1.0, 203.0)]
    [InlineData(203.6, 1.0, 204.0)]
    [InlineData(10.3, 1.25, 10.4)]
    [InlineData(5.0, 0.0, 5.0)]
    public void ALabelPosition_LandsOnAWholeDevicePixel(double logical, double scaling, double expected) => Assert.Equal(expected, MinimapViewportMath.SnapToPixel(logical, scaling), 6);

    /// <summary>Every surface that renders its own pixels sizes them with the interface scale folded in
    /// (<c>VisualPixelScale</c>): the 2D timeline and the 3D view did; the song map and the sheet music
    /// used the window's scaling alone and were stretched ~10 % at auto interface scale.</summary>
    [Fact]
    public void EveryOwnPixelSurface_CountsTheInterfaceScale()
    {
        var controls = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls"));
        var map = UiSourceTree.ReadAllText(Path.Combine(controls, "MinimapOverviewControl.cs"));
        var sheet = SheetMusicViewSource.ReadAllText();

        Assert.Contains("VisualPixelScale.For(this)", map);
        Assert.Contains("var dpi = VisualPixelScale.For(this);", sheet);
        Assert.DoesNotContain("TopLevel.GetTopLevel(this)?.RenderScaling", sheet);
    }

    [Fact]
    public void TheChipsAndTheMapLabels_UseTheSharpPaths()
    {
        var controls = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls"));
        var events = UiSourceTree.ReadAllText(Path.Combine(controls, "Layers", "EventLayer.cs"));
        var background = UiSourceTree.ReadAllText(Path.Combine(controls, "Layers", "BackgroundLayer.cs"));
        var map = UiSourceTree.ReadAllText(Path.Combine(controls, "MinimapOverviewControl.cs"));

        Assert.Contains("batcher.AddRoundRectAa(x, y, w, h, radius, solid)", events);
        Assert.DoesNotContain("_chipShadow", events);
        Assert.DoesNotContain("DrawRoundRectBorder", events);
        Assert.Contains("batcher.AddRoundRectAa(x, y, totalW, h, radius, MeasureBorder)", background);
        Assert.Contains("MinimapViewportMath.SnapToPixel(x + 3, _layerScaling)", map);
        Assert.DoesNotContain("SectionTypeface, 9,", map);
        Assert.Contains("var renderScaling = DeviceScale();", map);
    }

    /// <summary>
    ///     The FHP row draws a span as a slim track with a tick and a fret chip at its start. It used
    ///     to fill the whole row height across the whole span in translucent amber between two amber
    ///     hairlines, which read as a yellow bar with a line through the song (owner, 2026-09-24).
    ///     So: across a long span, nothing taller than the track reaches past the chip.
    /// </summary>
    [Fact]
    public void AnFhpSpan_IsASlimTrack_NotAFilledBar()
    {
        const float fhpY = 470f, zoom = 0.2f;
        var snapshot = new DotCharter.Rendering.Data.FrameSnapshot
        {
            StringCount = 6, Anchors = [new DotCharter.Rendering.Data.Render.RenderAnchor { Time = 1000, EndTime = 6000, Fret = 12 }],
            AnchorCount = 1
        };
        var rc = new DotCharter.UI.Controls.Layers.ChartRenderContext
        {
            Width = 1600, Height = 520, CurrentZoom = zoom, ViewStartMs = 0, ViewEndMs = 1600 / zoom,
            FhpY = fhpY, StringCount = 6, DrawLabels = true, RendererPixelsPerMs = zoom, Snapshot = snapshot
        };

        var batcher = new TimelineBatcher();
        new DotCharter.UI.Controls.Layers.FretAnchorLayer().Render(batcher, rc);
        batcher.FinalizeFrame();

        var spanStart = 1000 * zoom;
        var widest = 0f;
        foreach (var (buffer, stride, count) in new[]
                 {
                     (batcher.SolidBuffer, 6, batcher.SolidVertexCount),
                     (batcher.GeneralBuffer, 9, batcher.GeneralVertexCount)
                 })
        {
            for (var t = 0; t + 2 < count; t += 3)
            {
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                for (var v = t; v < t + 3; v++)
                {
                    minX = Math.Min(minX, buffer[v * stride]);
                    maxX = Math.Max(maxX, buffer[v * stride]);
                    minY = Math.Min(minY, buffer[v * stride + 1]);
                    maxY = Math.Max(maxY, buffer[v * stride + 1]);
                }

                if (maxY - minY > DotCharter.UI.Controls.Layers.FretAnchorLayer.TrackHeight + 0.01f)
                    widest = Math.Max(widest, maxX - spanStart);
            }
        }

        Assert.True(widest > 0, "the layer drew nothing taller than its track: no tick, no chip");
        Assert.True(widest <= 40f,
            $"something taller than the track runs {widest:0} px into a 1000 px span — the filled bar is back");
    }
}
