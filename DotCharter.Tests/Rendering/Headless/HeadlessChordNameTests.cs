using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The chord name has to actually be ON SCREEN. Three releases shipped it
///     invisible — x = −0.9 world units left of the anchor span (v0.41.1), then
///     the span's left edge + 0.15 (v0.42.0) — and nothing caught it, twice over:
///     <list type="number">
///         <item>
///             the unit test asserted only that the label was "somewhere inside
///             the anchor span", which both wrong placements satisfied; and
///         </item>
///         <item>
///             the headless renderer SKIPPED the font atlas, and
///             <c>TextBatcher</c> answers a null atlas by making every text draw
///             a silent no-op — so no golden frame could see 3D text at all, and
///             they all passed regardless.
///         </item>
///     </list>
///     The atlas is now built headless (it has been pure SkiaSharp since the
///     rebuild-deadlock fix; only a stale comment said otherwise), which makes
///     the real claim testable: render the chord twice, changing ONLY the
///     template name, and the pixels that differ ARE the label.
///     <para>
///         Deliberately NOT an assertion about exact pixels or colour. What is
///         pinned is what actually broke: the label rendered, and it landed
///         inside the viewport rather than past a clip plane.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessChordNameTests
{
    // 1600x900, not 800x450. The 0.42.2 chord-name scale was calibrated on the
    // smaller frame, where everything looks small, and shipped roughly 2.5x too
    // big — the owner's "goddamn gigantic". Judge type size at something close to
    // how the window is actually sized.
    private const int Width = 1600;
    private const int Height = 900;

    /// <summary>
    ///     One named chord under an anchor, at a fixed depth. Everything else is
    ///     held still so the A/B differs by the label alone.
    /// </summary>
    private static FrameSnapshot BuildChordAt(int anchorFret, int anchorWidth, string? name)
    {
        var s = new FrameSnapshot
        {
            StringCount = 6, HighwayScrollSpeed = 1.3f, Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = (byte)anchorFret, Width = (byte)anchorWidth }],
            AnchorCount = 1
        };

        var beats = new List<RenderBeat>();
        for (var i = 0; i < 48; i++)
        {
            beats.Add(new RenderBeat
            {
                Time = i * 500, IsMeasure = i % 4 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = i / 4
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;

        // The template's FRETS are deliberately left unset: this project does not
        // allow unsafe blocks (the fret array is a `fixed sbyte[8]`), and nothing
        // under test reads them — ChordNameRenderer reads only Name, and the chord
        // itself draws from the per-string RenderChordNotes below. Both frames of
        // the A/B share the template anyway, so anything it drove would cancel.
        var template = new RenderChordTemplate { Id = 0, Name = name ?? string.Empty };

        s.Templates = [template];
        s.TemplateCount = 1;

        // Near the strike line on purpose. z = time × TimeToZScale, so at the
        // default 1.3 scroll speed a chord one second out already sits ~14 world
        // units away and perspective shrinks its label to a few pixels — which is
        // a statement about distance, not about the label. What has to be legible
        // is the chord the player is about to PLAY.
        s.Chords = [new RenderChord { Time = 250, TemplateId = 0, NoteOffset = 0, NoteCount = 3 }];
        s.ChordCount = 1;
        s.ChordNotes =
        [
            new RenderChordNote { StringIndex = 3, Fret = (byte)anchorFret },
            new RenderChordNote { StringIndex = 4, Fret = (byte)(anchorFret + 2) },
            new RenderChordNote { StringIndex = 5, Fret = (byte)(anchorFret + 2) }
        ];
        s.ChordNoteCount = 3;

        return s;
    }

    /// <summary>
    ///     First position, twelfth position, and a width-1 anchor (which the
    ///     camera silently widens to its own 4-fret minimum — the case where a
    ///     label reasoning about the anchor's DECLARED span computes a centre the
    ///     camera never framed).
    /// </summary>
    [Theory]
    [InlineData(1, 4, "Am")]
    [InlineData(12, 4, "D")]
    [InlineData(5, 1, "G")]
    public void TheChordNameRendersAndLandsInsideTheViewport(int anchorFret, int anchorWidth, string name)
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var unnamed = r.Render(BuildChordAt(anchorFret, anchorWidth, null), Width, Height, timeMs: 0);
        var named = r.Render(BuildChordAt(anchorFret, anchorWidth, name), Width, Height, timeMs: 0);
        GoldenFrame.Dump(unnamed, Width, Height, $"vk_chordname_{anchorFret}_off");
        GoldenFrame.Dump(named, Width, Height, $"vk_chordname_{anchorFret}_on");

        var box = GoldenFrame.DifferenceBounds(unnamed, named, Width, Height);

        // 1. It rendered at all. This is the whole v0.41.1/v0.42.0 failure: the
        //    draw call ran every frame and produced nothing visible.
        Assert.True(box is not null,
            $"Naming the chord '{name}' changed no pixel — the label did not render.");
        var (x0, y0, x1, y1, pixels) = box!.Value;

        // A couple of stray AA pixels would satisfy "not null"; a two-letter
        // label at scale 0.014 covers far more than that.
        Assert.True(pixels > 60,
            $"Only {pixels} pixels changed — too few to be a rendered chord name.");

        // 2. It is INSIDE the frame, not clipped at an edge. Both shipped
        //    placements put it past the left clip plane, which is exactly what a
        //    bounding box touching x=0 (or nothing at all) looks like.
        Assert.True(x0 > 0 && x1 < Width - 1,
            $"The label touches a horizontal frame edge (x {x0}..{x1} of {Width}) — it is being clipped.");
        Assert.True(y0 > 0 && y1 < Height - 1,
            $"The label touches a vertical frame edge (y {y0}..{y1} of {Height}) — it is being clipped.");

        // 3. It floats ABOVE the neck, the way Rocksmith draws it — not down on
        //    the strings where it would sit over the incoming note heads. The
        //    board occupies the lower part of the frame; the label's whole box
        //    must clear the vertical middle.
        Assert.True(y1 < Height * 0.5,
            $"The label is not above the neck (its box runs to y={y1} of {Height}).");

        // 4. It is a LABEL, not a billboard. 0.42.2 shipped scale 0.026 and the
        //    owner reported the names as "goddamn gigantic"; in Rocksmith and
        //    rscharter the name is a modest mark near the shape. A cap height
        //    over ~8% of the frame is the failure this pins.
        var height = y1 - y0;
        Assert.True(height < Height * 0.08,
            $"The label is {height}px tall in a {Height}px frame — that is a billboard, not a chord name.");
    }

    /// <summary>
    ///     A hand shape holding SEVERAL strums of the same chord is labelled
    ///     ONCE. The owner caught "A7" drawn twice inside one shape, at two
    ///     depths, because the renderer labelled every chord rather than every
    ///     shape — and re-strumming a held shape is ordinary chart content.
    ///     <para>
    ///         Measured as pixel COUNT, not as a bounding box: two labels at
    ///         different depths are different sizes and positions, so the box
    ///         alone would not separate them — but twice the glyphs is roughly
    ///         twice the ink.
    ///     </para>
    /// </summary>
    [Fact]
    public void AReStrummedShapeIsLabelledOnce()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var one = BuildChordAt(5, 4, "A7");
        var many = BuildChordAt(5, 4, "A7");
        // Three strums of ONE held shape. The hand shape is what makes this the
        // case under test — without it these are three bare chords, and three
        // labels would be correct. Strums 2 and 3 carry the repeat flag the app's
        // export rule supplies for them (#487), so they draw as repeat boxes.
        many.Chords =
        [
            new RenderChord { Time = 250, TemplateId = 0, NoteOffset = 0, NoteCount = 3 },
            new RenderChord { Time = 700, TemplateId = 0, NoteOffset = 0, NoteCount = 3, Flags = ChordFlags.Repeat },
            new RenderChord { Time = 1150, TemplateId = 0, NoteOffset = 0, NoteCount = 3, Flags = ChordFlags.Repeat }
        ];
        many.ChordCount = 3;
        many.HandShapes = [new RenderHandShape { Time = 250, EndTime = 1500, TemplateId = 0 }];
        many.HandShapeCount = 1;

        var unnamed = r.Render(BuildChordAt(5, 4, null), Width, Height, timeMs: 0);
        var single = r.Render(one, Width, Height, timeMs: 0);
        var restrummed = r.Render(many, Width, Height, timeMs: 0);
        GoldenFrame.Dump(restrummed, Width, Height, "vk_chordname_restrummed");

        var oneInk = GoldenFrame.DifferenceBounds(unnamed, single, Width, Height)?.Pixels ?? 0;
        Assert.True(oneInk > 60, "the single-strum label did not render");

        // The re-strummed frame has MORE note heads, so diff it against its own
        // unnamed twin to isolate the text.
        var manyUnnamed = BuildChordAt(5, 4, null);
        manyUnnamed.Chords = many.Chords;
        manyUnnamed.ChordCount = many.ChordCount;
        manyUnnamed.HandShapes = many.HandShapes;
        manyUnnamed.HandShapeCount = many.HandShapeCount;
        var manyInk = GoldenFrame.DifferenceBounds(
            r.Render(manyUnnamed, Width, Height, timeMs: 0), restrummed, Width, Height)?.Pixels ?? 0;

        Assert.True(manyInk < oneInk * 1.6,
            $"three strums of one shape drew {manyInk}px of text vs {oneInk}px for one — " +
            "the shape is being labelled once per strum.");
    }

    /// <summary>
    ///     The vacuity guard for the guard: with the font atlas skipped, EVERY
    ///     3D text draw is a silent no-op and the assertions above would have
    ///     nothing to see. If this ever fails, the headless renderer has gone
    ///     back to rendering no text and the chord-name test above has quietly
    ///     stopped proving anything.
    /// </summary>
    [Fact]
    public void TheHeadlessRendererReallyRasterizesText()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var plain = BuildChordAt(1, 4, null);
        var withDebugText = BuildChordAt(1, 4, null);
        withDebugText.DebugText = "TEXT RASTERIZES";

        var a = r.Render(plain, Width, Height, timeMs: 0);
        var b = r.Render(withDebugText, Width, Height, timeMs: 0);

        Assert.True(GoldenFrame.DifferenceBounds(a, b, Width, Height) is not null,
            "Headless text rendered nothing — the font atlas is missing, so every " +
            "3D-text golden frame in this file is vacuous.");
    }
}
