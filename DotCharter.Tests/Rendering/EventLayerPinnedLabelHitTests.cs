using Avalonia;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A PINNED (sticky) ribbon badge is the one chip on the 2D timeline whose
///     click target is not derived from its own drawn box — the layer paints it
///     and separately registers a rect for <c>EventInteractionTool</c> in the
///     shared <see cref="PinnedLabelHitRegistry" />. The two disagreed on BOTH
///     axes: the rect started at the ROW top while the chip is drawn at
///     <see cref="RibbonLayout.BarTopIn" /> (+2.5 px), and it padded the text by
///     12 px against the chip's 9-per-side 18. So the bottom 2.5 px and the
///     right 6 px of every visible sticky badge were not clickable, and 2.5 px of
///     dead row above it were.
///     <para>
///         Harness is the view-model-free one from
///         <see cref="EventLayerLyricPhraseMarkerTests" />: a real
///         <see cref="FrameSnapshot" /> through the real layer into a plain
///         <see cref="TimelineBatcher" /> (6 floats per solid vertex —
///         x, y, r, g, b, a), so the DRAWN rect is recoverable by colour and can
///         be compared with the REGISTERED one. No GPU, no app, no font atlas
///         (text measures 0, which changes both widths equally and so cannot
///         mask the padding mismatch).
///     </para>
/// </summary>
public sealed class EventLayerPinnedLabelHitTests
{
    private const double Zoom = 0.2;
    private const double ViewWidth = 1920;

    /// <summary>Window deliberately starts late so a tone at 8 s is BEFORE it
    /// and therefore pinned. Still inside the snapshot's 5 s buffer.</summary>
    private const double ViewStartMs = 10_000;

    private const double ToneTimeMs = 8_000;

    /// <summary>EventLayer's tone chip fill — unconditional (unlike the section
    /// chip, which brightens when it is the active one).</summary>
    private static readonly Color ToneFill = EventLayer.OnLane(Color.FromArgb(215, 22, 130, 96));

    private static List<BeatMarker> Beats(int count = 64) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static (TimelineBatcher Batcher, PinnedLabelHitRegistry Registry, object Owner) RenderPinnedTone(
        object? owner = null, PinnedLabelHitRegistry? registry = null) =>
        RenderTones([ToneTimeMs], owner, registry);

    private static (TimelineBatcher Batcher, PinnedLabelHitRegistry Registry, object Owner) RenderTones(
        double[] toneTimesMs, object? owner = null, PinnedLabelHitRegistry? registry = null, bool selectFirst = false)
    {
        var beats = Beats();
        var tones = toneTimesMs.Select((t, i) => new ToneChange
        {
            Position = TimelineMath.PositionFromMs(t, beats),
            ToneName = i == 0 ? "Lead" : "Clean"
        }).ToList();
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = [],
            ActiveChords = [],
            ActiveAnchors = [],
            ActiveHandShapes = [],
            EventPoints = [],
            Tones = tones,
            SelectedTones = selectFirst ? [tones[0]] : [],
            LyricPhrases = [],
            Showlights = []
        };

        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(ViewStartMs, ViewStartMs + ViewWidth / Zoom, ViewStartMs, snapshot, ctx, []);

        var rc = new ChartRenderContext
        {
            Width = ViewWidth,
            Height = 500,
            CurrentZoom = Zoom,
            ViewStartMs = ViewStartMs,
            ViewEndMs = ViewStartMs + ViewWidth / Zoom,
            CurrentTimeMs = ViewStartMs,
            StringCount = 6,
            DrawLabels = true,
            RendererPixelsPerMs = Zoom,
            Ribbon = new RibbonLayout(RibbonLanes.All),
            Snapshot = snapshot
        };

        registry ??= new PinnedLabelHitRegistry();
        owner ??= new object();
        var batcher = new TimelineBatcher();
        new EventLayer(registry, owner).Render(batcher, rc);
        batcher.FinalizeFrame();
        return (batcher, registry, owner);
    }

    /// <summary>Bounding box of every solid vertex painted in
    /// <paramref name="fill" /> — for a chip that is exactly the rounded rect
    /// <c>DrawChip</c> emitted (its shadow, border and pinned stripe are all
    /// other colours).</summary>
    private static bool Paints(TimelineBatcher batcher, Color fill)
    {
        for (var v = 0; v < batcher.SolidVertexCount; v++)
        {
            var o = v * 6;
            if (Math.Abs(batcher.SolidBuffer[o + 2] - fill.R / 255f) < 0.004f &&
                Math.Abs(batcher.SolidBuffer[o + 3] - fill.G / 255f) < 0.004f &&
                Math.Abs(batcher.SolidBuffer[o + 4] - fill.B / 255f) < 0.004f &&
                Math.Abs(batcher.SolidBuffer[o + 5] - fill.A / 255f) < 0.004f)
            {
                return true;
            }
        }

        return false;
    }

    private static Rect DrawnRectOf(TimelineBatcher batcher, Color fill)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var found = false;
        for (var v = 0; v < batcher.SolidVertexCount; v++)
        {
            var o = v * 6;
            if (Math.Abs(batcher.SolidBuffer[o + 2] - fill.R / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 3] - fill.G / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 4] - fill.B / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 5] - fill.A / 255f) >= 0.004f)
            {
                continue;
            }

            found = true;
            minX = Math.Min(minX, batcher.SolidBuffer[o]);
            maxX = Math.Max(maxX, batcher.SolidBuffer[o]);
            minY = Math.Min(minY, batcher.SolidBuffer[o + 1]);
            maxY = Math.Max(maxY, batcher.SolidBuffer[o + 1]);
        }

        Assert.True(found, "the pinned tone chip was not drawn at all — the harness, not the assertion, is wrong");
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    [Fact]
    public void ThePinnedBadgesClickRect_IsTheChipItDrew()
    {
        var (batcher, registry, owner) = RenderPinnedTone();
        var drawn = DrawnRectOf(batcher, ToneFill);

        Assert.Equal(1, registry.CountFor(owner));
        Assert.True(registry.TryHit(owner, drawn.Center, out var hit), "the chip's own centre did not hit");

        Assert.Equal(drawn.X, hit.Rect.X, 3);
        Assert.Equal(drawn.Y, hit.Rect.Y, 3);
        Assert.Equal(drawn.Width, hit.Rect.Width, 3);
        Assert.Equal(drawn.Height, hit.Rect.Height, 3);
        Assert.Equal(ToneTimeMs, hit.TimeMs, 3);
        Assert.Equal(PinnedLabelHitRegistry.PinnedLabelKind.Tone, hit.Kind);
    }

    [Fact]
    public void ClickingInsideTheDrawnChip_Hits_AndJustAboveIt_DoesNot()
    {
        var (batcher, registry, owner) = RenderPinnedTone();
        var drawn = DrawnRectOf(batcher, ToneFill);

        // Bottom-right INSIDE corner: the half of the chip the old rect
        // (rowY-anchored, 6 px narrow) did not cover.
        Assert.True(registry.TryHit(owner, new Point(drawn.Right - 0.5, drawn.Bottom - 0.5), out _),
            "the chip's bottom-right pixel is drawn but does not hit");

        // …and the row gap ABOVE the chip is not the chip.
        Assert.False(registry.TryHit(owner, new Point(drawn.Center.X, drawn.Y - 1.5), out _),
            "a point above the drawn chip hit it — the rect is still anchored to the row top");
    }

    [Fact]
    public void TheChipSitsInTheLyricRowsSiblingBand_NotOnTheRowLine()
    {
        // Guards the Y half specifically against the geometry the ribbon
        // defines, so the test does not merely restate the layer's code.
        var (batcher, _, _) = RenderPinnedTone();
        var drawn = DrawnRectOf(batcher, ToneFill);
        var ribbon = new RibbonLayout(RibbonLanes.All);

        Assert.Equal(RibbonLayout.BarTopIn(ribbon.ToneY), drawn.Y, 3);
        Assert.Equal(RibbonLayout.BarHeight, drawn.Height, 3);
        Assert.Equal(ChartEditorConstants.RenderMetrics.ActiveLabelFixedX, drawn.X, 3);
    }

    [Fact]
    public void OneViewportsBadges_SurviveAnotherViewportsFrame()
    {
        // The registry is a DI SINGLETON and the app runs one ChartRenderer per
        // stacked track row beside the main timeline, each with its own
        // EventLayer drawing every frame. Flat, the last renderer to draw wiped
        // the rest — and its rects, in ITS control's pixel space, were then
        // hit-tested against a pointer from another control.
        var registry = new PinnedLabelHitRegistry();
        var main = new object();
        var stackedRow = new object();

        var (batcher, _, _) = RenderPinnedTone(main, registry);
        var drawn = DrawnRectOf(batcher, ToneFill);
        RenderPinnedTone(stackedRow, registry);

        Assert.Equal(1, registry.CountFor(main));
        Assert.Equal(1, registry.CountFor(stackedRow));
        Assert.True(registry.TryHit(main, drawn.Center, out _),
            "the main timeline's pinned badge was wiped by the stacked row's frame");

        registry.Forget(stackedRow);
        Assert.Equal(0, registry.CountFor(stackedRow));
        Assert.Equal(1, registry.CountFor(main));
    }

    /// <summary>
    ///     Sticky-header push-off (2026-09-23): the next tone's chip arriving at the left edge
    ///     shoves the pinned one out instead of sliding underneath it.
    /// </summary>
    [Fact]
    public void TheNextChipArriving_PushesThePinnedChipOut_InsteadOfSlidingUnderIt()
    {
        const double nextX = 10.0;
        var (_, registry, owner) = RenderTones([ToneTimeMs, ViewStartMs + nextX / Zoom]);

        Assert.Equal(1, registry.CountFor(owner));
        Assert.True(registry.TryHit(owner, new Point(0.5, RibbonLayout.BarTopIn(new RibbonLayout(RibbonLanes.All).ToneY) + 1),
            out var pinned), "the pushed chip left the screen entirely although part of it still fits");
        Assert.True(pinned.Rect.Right <= nextX - StickyLabel.PushGapPx + 0.001,
            $"the pinned chip ends at {pinned.Rect.Right}, over the next chip that starts at {nextX}");
        Assert.True(pinned.Rect.X < ChartEditorConstants.RenderMetrics.ActiveLabelFixedX,
            "the pinned chip did not move at all");
    }

    /// <summary>
    ///     The in-flow chip is suppressed while its event is pinned, so the PINNED copy is the only
    ///     place a selected tone can show it is selected. It was drawn with the base fill and
    ///     <c>selected: false</c>, so a selection vanished the moment its chip pinned (static review,
    ///     2026-09-25). It wears the same emphasis fill as its in-flow chip now.
    /// </summary>
    [Fact]
    public void ASelectedPinnedTone_KeepsItsSelectedLook()
    {
        var toneFillEmph = EventLayer.OnLane(Color.FromArgb(240, 34, 156, 117));

        var (plain, _, _) = RenderTones([ToneTimeMs]);
        Assert.True(Paints(plain, ToneFill), "an unselected pinned tone lost its base fill");
        Assert.False(Paints(plain, toneFillEmph), "an unselected pinned tone is drawn emphasised");

        var (selected, _, _) = RenderTones([ToneTimeMs], selectFirst: true);
        var drawn = DrawnRectOf(selected, toneFillEmph);
        Assert.Equal(ChartEditorConstants.RenderMetrics.ActiveLabelFixedX, drawn.X, 3);
        Assert.False(Paints(selected, ToneFill), "the selected pinned tone is still drawn in the base fill");
    }

    /// <summary>A tone that began a moment before the viewport is still partly on screen in flow;
    /// it is drawn ONCE, as the pinned chip, not twice a few pixels apart.</summary>
    [Fact]
    public void AToneJustPastTheEdge_IsDrawnOnce_AsThePinnedChip()
    {
        var (batcher, _, _) = RenderTones([ViewStartMs - 5 / Zoom]);
        var drawn = DrawnRectOf(batcher, ToneFill);

        Assert.Equal(ChartEditorConstants.RenderMetrics.ActiveLabelFixedX, drawn.X, 3);
    }
}
