using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The highway's open-note rule is <c>fret &lt;= Capo</c>: a fret the capo
///     already stops draws as a BAR spanning the active anchor, not as a gem at
///     that fret's middle. `NoteHeadRenderer`, `ChordRenderer` and
///     `SustainRenderer` have always read it that way.
///     <para>
///         The three hit-feedback components tested <c>fret == 0</c> instead, so
///         with a capo on fret 3 a note at fret 3 was drawn as a bar across the
///         whole anchor while its hit flash, spark burst and particle explosion
///         all fired as a point at <c>GetLinearFretMiddle(3)</c> — the effect
///         landed beside the note that caused it, and on a capo-1 chart every
///         open string had the same split.
///     </para>
///     <para>
///         Asserted on the STAGED GEOMETRY: the components emit symmetric quads
///         around their origin, so the x-centre of everything a pass staged IS
///         the effect's origin. No GPU — <see cref="QuadBatcher" /> only fills a
///         float array until it is flushed.
///     </para>
/// </summary>
public sealed class CapoHitFeedbackTests
{
    private const int Capo = 3;
    private const byte NoteFret = 3; // == the capo: an OPEN note
    private const double NoteTimeMs = 1_000;

    /// <summary>Anchor 1, width 4 ⇒ span [0, 4], centre 2.0.</summary>
    private const float AnchorSpanCentre = 2.0f;

    /// <summary>Where the buggy fretted path put it: (3 - 0.5) * 1.0.</summary>
    private const float FrettedCentre = 2.5f;

    /// <summary>The spark burst scatters its particles pseudo-randomly around
    /// the origin, so its staged centre wanders a little. Half the 0.5-unit gap
    /// between the two candidate origins still separates them cleanly.</summary>
    private const float Tolerance = 0.25f;

    [Fact]
    public void The_hit_flash_of_a_capoed_note_sits_on_the_open_note_bar()
    {
        var centre = StagedXCentre(new EnhancedHitFeedbackRenderComponent(), Capo);

        AssertOrigin(AnchorSpanCentre, centre);
    }

    [Fact]
    public void The_spark_burst_of_a_capoed_note_sits_on_the_open_note_bar()
    {
        var centre = StagedXCentre(new HitSparkRenderComponent(), Capo);

        AssertOrigin(AnchorSpanCentre, centre);
    }

    [Fact]
    public void The_particle_explosion_of_a_capoed_note_spans_the_open_note_bar()
    {
        // The explosion spawns particles uniformly ACROSS the bar rather than
        // symmetrically around a point, so it is pinned by its width: a fretted
        // spawn has Length 0 and every particle shares one origin x, while the
        // open spawn spreads them over the four-fret anchor span.
        var open = StagedXSpread(Capo);
        var fretted = StagedXSpread(capo: 0);

        Assert.True(open > fretted + 1f,
            $"a capoed note must spawn across the anchor bar (open spread {open:F2} vs fretted {fretted:F2})");
    }

    [Fact]
    public void Without_a_capo_the_same_note_still_reads_as_fretted()
    {
        // Guards the fix from over-reaching: fret 3 with no capo is a plain
        // fretted note and must keep its point-origin effect.
        AssertOrigin(FrettedCentre, StagedXCentre(new EnhancedHitFeedbackRenderComponent(), capo: 0));
        AssertOrigin(FrettedCentre, StagedXCentre(new HitSparkRenderComponent(), capo: 0));
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>The effect's origin is <paramref name="expected" /> and NOT the
    /// other candidate — the two sit 0.5 units apart, so the tolerance both
    /// absorbs the spark scatter and keeps the assertion sharp.</summary>
    private static void AssertOrigin(float expected, float actual)
    {
        var other = Math.Abs(expected - AnchorSpanCentre) < 0.01f ? FrettedCentre : AnchorSpanCentre;
        Assert.True(Math.Abs(actual - expected) < Tolerance,
            $"expected the effect at x={expected} (the other candidate is {other}) but it staged around {actual:F3}");
        Assert.True(Math.Abs(actual - other) > Tolerance, $"the effect landed on the wrong origin {other}");
    }

    private static float StagedXCentre(IRenderable component, int capo)
    {
        var batcher = new QuadBatcher(null!);
        component.Render(Context(batcher, capo, NoteTimeMs + 40));

        var (min, max) = XRange(batcher);
        Assert.True(max > min, "the pass staged no geometry — the fixture is not exercising the effect");
        return (min + max) / 2f;
    }

    /// <summary>Width of the particle field the explosion component spawns.</summary>
    private static float StagedXSpread(int capo)
    {
        var component = new NoteHitExplosionRenderComponent();
        var batcher = new QuadBatcher(null!);

        // The explosion detects hits by the playhead CROSSING them, so it needs
        // a prior frame to establish "last time" before the note is passed.
        component.Render(Context(batcher, capo, NoteTimeMs - 20));
        component.Render(Context(batcher, capo, NoteTimeMs + 20));

        var (min, max) = XRange(batcher);
        Assert.True(max > min, "the explosion staged no geometry — the fixture is not exercising the effect");
        return max - min;
    }

    private static (float min, float max) XRange(QuadBatcher batcher)
    {
        var data = batcher.StagedVertices;
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < data.Length; i += QuadBatcher.FloatsPerVertex)
        {
            if (data[i] < min) min = data[i];
            if (data[i] > max) max = data[i];
        }

        return (min, max);
    }

    private static RenderContext Context(QuadBatcher batcher, int capo, double timeMs)
    {
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Capo = capo,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 1, Width = 4 }],
            AnchorCount = 1,
            Notes = [new RenderNote { Time = NoteTimeMs, Sustain = 0, StringIndex = 0, Fret = NoteFret }],
            NoteCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = timeMs,
            BoardWidth = 10f,
            QuadBatcher = batcher,
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(
                HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };
    }
}
