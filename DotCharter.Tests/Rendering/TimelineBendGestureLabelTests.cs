using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     On the 2D timeline: <see cref="HighwayNoteHeadRenderer" /> labels each bend GESTURE once, as Guitar Pro 8
///     does. The owner's report: a plain bend 0 → Full read "↗1/2 ↗Full", and a release Full → 0 read "↗Full".
/// </summary>
public sealed class TimelineBendGestureLabelTests
{
    private const double NoteTimeMs = 100;
    private const double SustainMs = 600;

    [Fact]
    public void APlainBendThroughItsMiddlePoint_IsOneFull() =>
        Assert.Equal(["↗Full"], Labels(NoteFlags.HasBends, 0f, (0, 0f), (100, 1f), (200, 2f)));

    [Fact]
    public void AReleaseTiedInFromABend_IsOneDownLabel() =>
        Assert.Equal(["↘Full"], Labels(NoteFlags.HasBends | NoteFlags.LinkPrevious, 2f, (0, 2f), (100, 1f), (200, 0f)));

    [Fact]
    public void APrebend_AndAPrebendRelease_SayPrebend() =>
        Assert.Equal(["PB Full", "↘Full"], Labels(NoteFlags.HasBends, 0f, (0, 2f), (200, 2f), (400, 0f)));

    [Fact]
    public void AMultiStepCurve_LabelsEachRise() =>
        Assert.Equal(["↗1/2", "↗Full"], Labels(NoteFlags.HasBends, 0f, (0, 0f), (100, 1f), (300, 1f), (400, 2f)));

    private static string[] Labels(NoteFlags flags, float carry, params (double OffsetMs, float Step)[] points)
    {
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            BendValues = [.. points.Select(p => new RenderBendValue { Time = NoteTimeMs + p.OffsetMs, Step = p.Step })],
            BendValueCount = points.Length
        };
        var rc = new ChartRenderContext
        {
            Width = 900, Height = 400, CurrentZoom = 1.0, ViewStartMs = 0, ViewEndMs = 900, TopMargin = 20f,
            BottomMargin = 20f, LaneAreaHeight = 360f, LaneHeight = 60f, StringCount = 6, DrawLabels = true,
            Snapshot = snapshot
        };
        var batcher = new TimelineBatcher { FontAtlas = Atlas.Value };
        var labels = new List<DeferredLabel>();
        var data = new NoteDrawData(NoteTimeMs, SustainMs, 0, 7, 0, 0, 0, 0, flags, false, 0, points.Length,
            bendCarry: carry);
        HighwayNoteHeadRenderer.Draw(batcher, rc, in data, labels);
        return [.. labels.Select(l => l.Text)];
    }

    private static readonly Lazy<ProbeAtlas> Atlas = new(() =>
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        return atlas;
    });

    private sealed class ProbeAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SKBitmap bitmap)
        {
        }

        public override void Dispose()
        {
        }
    }
}
