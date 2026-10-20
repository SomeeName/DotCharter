using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Timeline2D;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.UI.Controls.Layers;
using static DotCharter.Tests.Rendering.PartialChordSlideFixture;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     How the renderers DRAW a chord where only one string slides (#476): the
///     owner's 5+8 where the 5 slides to 6 and the 8 is held (tied) or re-picked
///     (<see cref="PartialChordSlideFixture" />, the real GP import).
///     <para>
///         What was wrong: the snapshot gave every chord note the CHORD's
///         <c>LinkNext</c> (<c>Chord.HasLinkNext()</c>, true as soon as one string
///         links), so with a re-picked 8 the 8 was treated as linking on, its
///         re-pick was flagged a link landing, and the 3D highway — which drew
///         NOTHING for a landing chord — dropped the 8's head. Rocksmith gets that
///         landing as loose single notes (<c>LinkLandingBreakup</c>) and shows
///         the re-picked 8. The 3D driver's own copy of the link walk had the
///         same fault.
///     </para>
/// </summary>
public sealed class PartialChordSlideRenderTests
{
    // ── the snapshot: per-string slide and link data ────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheSlidingStringCarriesTheSlide(bool tied)
    {
        var s = Snapshot(Fixture(tied));

        var sliding = ChordNote(s, OriginMs, AString);
        var held = ChordNote(s, OriginMs, DString);
        Assert.True(sliding.Flags.HasFlag(NoteFlags.HasSlide));
        Assert.Equal(6, sliding.SlideTo);
        Assert.False(held.Flags.HasFlag(NoteFlags.HasSlide), "the held 8 must not slide with the chord");
        Assert.Equal(0, held.SlideTo);
        Assert.All(Notes(s, LandingMs), cn => Assert.False(cn.Flags.HasFlag(NoteFlags.HasSlide)));
    }

    [Fact]
    public void ATiedHold_LinksBothStringsIntoTheLanding()
    {
        var s = Snapshot(Fixture(tied: true));

        Assert.True(ChordNote(s, OriginMs, AString).Flags.HasFlag(NoteFlags.LinkNext));
        Assert.True(ChordNote(s, OriginMs, DString).Flags.HasFlag(NoteFlags.LinkNext));
        Assert.True(ChordNote(s, LandingMs, AString).Flags.HasFlag(NoteFlags.LinkPrevious));
        Assert.True(ChordNote(s, LandingMs, DString).Flags.HasFlag(NoteFlags.LinkPrevious));
    }

    [Fact]
    public void ARepickedHold_LinksOnlyTheSlidingString()
    {
        var s = Snapshot(Fixture(tied: false));

        Assert.True(ChordNote(s, OriginMs, AString).Flags.HasFlag(NoteFlags.LinkNext));
        Assert.False(ChordNote(s, OriginMs, DString).Flags.HasFlag(NoteFlags.LinkNext),
            "the chord's link is per string: the re-picked 8 does not link on");
        Assert.True(ChordNote(s, LandingMs, AString).Flags.HasFlag(NoteFlags.LinkPrevious));
        Assert.False(ChordNote(s, LandingMs, DString).Flags.HasFlag(NoteFlags.LinkPrevious),
            "the re-picked 8 is a fresh note, not a link landing");
    }

    /// <summary>The 3D driver's link walk is the same one (it kept its own copy,
    /// chord-wide, until #476).</summary>
    [Fact]
    public void TheSharedLinkWalk_IsPerString()
    {
        var level = Fixture(tied: false).Levels[0];
        var targets = new HashSet<(long, int)>();

        FrameSnapshot.CollectLinkTargets(level.Notes, level.Chords, Beats(), 0, int.MaxValue, 0, int.MaxValue,
            targets);

        Assert.Equal(((long)LandingMs, AString), Assert.Single(targets));
    }

    // ── the 2D timeline ─────────────────────────────────────────────────────

    /// <summary>The slide arrow (white, solid) sits on the A lane only; the D
    /// lane draws its sustain and nothing white.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Timeline_DrawsTheSlideOnTheSlidingLaneOnly(bool tied)
    {
        var rc = TimelineContext(Snapshot(Fixture(tied)));
        var batcher = new TimelineBatcher();
        new NoteLayer().Render(batcher, rc);

        var originX = (float)((OriginMs - rc.ViewStartMs) * rc.CurrentZoom);
        var landingX = (float)((LandingMs - rc.ViewStartMs) * rc.CurrentZoom);
        Assert.True(SlideTriangles(batcher, rc, AString, originX, landingX) > 0, "the A string's slide is drawn");
        Assert.Equal(0, SlideTriangles(batcher, rc, DString, originX, landingX));
        Assert.True(SustainTriangles(batcher, rc, DString, originX, landingX) > 0, "the held 8 keeps its sustain");
    }

    // ── the 3D highway ──────────────────────────────────────────────────────

    /// <summary>Halfway through the slide: the A tail moves across the frets, the
    /// D tail stays on its fret, and the D tail runs on unbroken through the
    /// landing into the landing's own tail.</summary>
    [Fact]
    public void Highway_SlidesOnlyTheSlidingTail_AndTheHeldTailRunsThroughTheLanding()
    {
        var context = HighwayContext(Snapshot(Fixture(tied: true)), timeMs: OriginMs + 200);
        var quads = Stage(context);
        var landingZ = context.GetZDepthForTime(LandingMs, context.TimeMs);

        var aTail = TailQuads(quads, AString);
        var dTail = TailQuads(quads, DString);
        Assert.NotEmpty(aTail);
        Assert.NotEmpty(dTail);
        Assert.True(aTail.Max(q => q.CentreX) - aTail.Min(q => q.CentreX) > 0.3f, "the sliding tail moves");
        Assert.True(dTail.Max(q => q.CentreX) - dTail.Min(q => q.CentreX) < 0.01f, "the held tail stays put");
        Assert.Contains(dTail, q => Math.Abs(q.MaxZ - landingZ) < 0.01f);
        Assert.Contains(dTail, q => Math.Abs(q.MinZ - landingZ) < 0.01f);
    }

    /// <summary>The landing: nothing for a string that arrives linked (Rocksmith
    /// draws a linked child as no head), a single-note head and its tail for the
    /// re-picked 8.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Highway_DrawsAHeadAtTheLandingOnlyForARepickedString(bool tied, int expectedDHeads)
    {
        var context = HighwayContext(Snapshot(Fixture(tied)), timeMs: LandingMs - 600);
        var quads = Stage(context);

        // The 5+8 has passed the strike line (no heads), and the low-E 3 is a
        // single note whose head the chord pass does not draw: every head here is the landing's.
        Assert.Equal(0, Heads(quads, AString));
        Assert.Equal(expectedDHeads, Heads(quads, DString));

        // Held or re-picked, the 8 rings on past the landing. A landing ships as
        // single notes, and a single note always shows its tail — the hand shape
        // only stands in for a plain CHORD's sustain.
        var landingZ = context.GetZDepthForTime(LandingMs, context.TimeMs);
        Assert.Contains(TailQuads(quads, DString), q => q.MinZ >= landingZ - 0.01f);
    }

    // ── fixture plumbing ────────────────────────────────────────────────────

    private static Arrangement Fixture(bool tied) =>
        tied ? Import() : GpPartialChordSlideTests.ImportRepickedHold();

    private static RenderChordNote ChordNote(FrameSnapshot s, double timeMs, int stringIndex) =>
        Assert.Single(Notes(s, timeMs), cn => cn.StringIndex == stringIndex);

    private static List<RenderChordNote> Notes(FrameSnapshot s, double timeMs)
    {
        var chord = Assert.Single(s.Chords.Take(s.ChordCount), c => Math.Abs(c.Time - timeMs) < 0.5);
        return s.ChordNotes.Skip(chord.NoteOffset).Take(chord.NoteCount).ToList();
    }

    private static ChartRenderContext TimelineContext(FrameSnapshot snapshot) => new()
    {
        Width = 1200,
        Height = 420,
        CurrentZoom = 0.4,
        ViewStartMs = 500,
        ViewEndMs = 500 + 1200 / 0.4,
        TopMargin = 30f,
        BottomMargin = 30f,
        LaneAreaHeight = 360f,
        LaneHeight = 60f,
        StringCount = 6,
        DrawLabels = true,
        Snapshot = snapshot
    };

    /// <summary>Opaque-ish WHITE solid triangles inside the lane, between the two
    /// chords: the slide arrow. (The tube's lit highlight is white too, at ~6 %
    /// alpha.)</summary>
    private static int SlideTriangles(TimelineBatcher b, ChartRenderContext rc, int stringIndex, float x0, float x1)
        => CountSolid(b, rc, stringIndex, x0, x1, (r, g, bl, a) => r == 1f && g == 1f && bl == 1f && a > 0.5f);

    private static int SustainTriangles(TimelineBatcher b, ChartRenderContext rc, int stringIndex, float x0,
        float x1)
    {
        var c = rc.StringColors[stringIndex];
        return CountSolid(b, rc, stringIndex, x0, x1,
            (r, g, bl, a) => a > 0.3f && !(r == 1f && g == 1f && bl == 1f) &&
                             Math.Abs(r - c.R / 255f) + Math.Abs(g - c.G / 255f) + Math.Abs(bl - c.B / 255f) <
                             0.6f);
    }

    private static int CountSolid(TimelineBatcher b, ChartRenderContext rc, int stringIndex, float x0, float x1,
        Func<float, float, float, float, bool> colour)
    {
        var top = rc.LaneTopFor(stringIndex);
        var bottom = top + rc.LaneHeight;
        var buf = b.SolidBuffer;
        var n = 0;
        for (var v = 0; v + 2 < b.SolidVertexCount; v += 3)
        {
            var i = v * 6;
            var cx = (buf[i] + buf[i + 6] + buf[i + 12]) / 3f;
            var cy = (buf[i + 1] + buf[i + 7] + buf[i + 13]) / 3f;
            if (cx <= x0 + 6f || cx >= x1 - 6f || cy < top || cy >= bottom) continue;
            if (colour(buf[i + 2], buf[i + 3], buf[i + 4], buf[i + 5])) n++;
        }

        return n;
    }

    private static RenderContext HighwayContext(FrameSnapshot snapshot, double timeMs) => new()
    {
        Snapshot = snapshot,
        TimeMs = timeMs,
        BoardWidth = 10f,
        QuadBatcher = new QuadBatcher(null!),
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.DefaultScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.DefaultDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.DefaultDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(
            HighwaySpeedPolicy.DefaultScrollSpeed, HighwaySpeedPolicy.DefaultDrawDistanceZ)
    };

    /// <summary>The chord pass, after the tail pass that stages every tail (chord
    /// tails included, #605) as <c>NotesRenderComponent</c> runs them.</summary>
    private static List<Quad> Stage(RenderContext context)
    {
        SustainRenderer.Render(context);
        ChordRenderer.Render(context, new RenderResources());
        const int quadFloats = QuadBatcher.FloatsPerVertex * 4;
        var data = context.QuadBatcher.StagedVertices.ToArray();
        var quads = new List<Quad>();
        for (var q = 0; q < data.Length; q += quadFloats) quads.Add(new Quad(data.AsSpan(q, quadFloats)));
        return quads;
    }

    private static bool IsString(Quad q, int stringIndex)
    {
        var c = FretboardConstants.StringColors[stringIndex];
        return q.R == c.R / 255f && q.G == c.G / 255f && q.B == c.B / 255f;
    }

    /// <summary>A sustain tail is three untextured (mode 0) strips per segment in the
    /// string's colour; the CORE strip (the translucent middle one) tracks its centre line.</summary>
    private static List<Quad> TailQuads(List<Quad> quads, int stringIndex) =>
        quads.Where(q => q.Mode == 0f && IsString(q, stringIndex) && q.MaxZ > q.MinZ &&
                         q.A == RenderingConstants.TailInnerAlpha / 255f).ToList();

    /// <summary>Note heads are the textured gem (mode 3), fully opaque.</summary>
    private static int Heads(List<Quad> quads, int stringIndex) =>
        quads.Count(q => q.Mode == 3f && q.A == 1f && IsString(q, stringIndex));

    private sealed class Quad
    {
        public Quad(ReadOnlySpan<float> f)
        {
            Mode = MathF.Round(f[12]);
            (R, G, B, A) = (f[3], f[4], f[5], f[6]);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var v = 0; v < 4; v++)
            {
                var o = v * QuadBatcher.FloatsPerVertex;
                minX = Math.Min(minX, f[o]);
                maxX = Math.Max(maxX, f[o]);
                minZ = Math.Min(minZ, f[o + 2]);
                maxZ = Math.Max(maxZ, f[o + 2]);
            }

            CentreX = (minX + maxX) / 2f;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        public float Mode { get; }
        public float R { get; }
        public float G { get; }
        public float B { get; }
        public float A { get; }
        public float CentreX { get; }
        public float MinZ { get; }
        public float MaxZ { get; }
    }
}
