using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Issue #470, the highway audit: the hand-shape band gradient axis, template
///     lookups by ID (not position), a template-less hand shape swallowing the
///     fingering overlay, and the chord head draw order under inverted strings.
///     No GPU: <see cref="QuadBatcher" /> only stages floats until flushed.
/// </summary>
public sealed class HighwayAuditFindingsTests
{
    private const int Floats = QuadBatcher.FloatsPerVertex;
    private const int QuadFloats = Floats * 4;

    private static readonly (int, int)[] VertexPairs = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)];

    // ---- 1: the soft band fades across the highway (x), not along it (z) ----

    [Fact]
    public void The_hand_shape_bands_fade_across_x_and_stay_constant_along_z()
    {
        var bare = Highway();
        var baseline = Stage(batcher => new HighwayRenderComponent().Render(Context(batcher, bare, 0)));
        var withShape = Highway();
        withShape.HandShapes = [new RenderHandShape { Time = 500, EndTime = 3000, TemplateId = -1 }];
        withShape.HandShapeCount = 1;
        var staged = Stage(batcher => new HighwayRenderComponent().Render(Context(batcher, withShape, 0)));

        var added = staged.Skip(baseline.Length).ToArray();
        Assert.True(added.Length >= 6 * QuadFloats, "two brackets of three quads each must be staged");
        var fadingAcrossX = 0;
        for (var q = 0; q + QuadFloats <= added.Length; q += QuadFloats)
        {
            var quad = added.AsSpan(q, QuadFloats);
            foreach (var (a, b) in VertexPairs)
            {
                var sameX = Math.Abs(quad[a * Floats] - quad[b * Floats]) < 1e-6f;
                var alphaDiffers = Math.Abs(quad[a * Floats + 6] - quad[b * Floats + 6]) > 1e-4f;
                // Two corners at the same x (one near, one far in z) must carry the same alpha.
                Assert.False(sameX && alphaDiffers,
                    "the band alpha changes along z: the soft edge runs along the highway instead of across it");
                if (!sameX && alphaDiffers) fadingAcrossX++;
            }
        }

        Assert.True(fadingAcrossX > 0, "no band fades across x at all");
    }

    // ---- 2: template lookups resolve by Id ---------------------------------

    [Fact]
    public void The_template_helper_finds_by_id_after_a_template_was_deleted()
    {
        var s = Highway();
        SetTemplates(s, Template(2, "Am", false), Template(5, "G", true));

        Assert.Equal(1, s.FindTemplateIndex(5));
        Assert.Equal(0, s.FindTemplateIndex(2));
        Assert.Equal(-1, s.FindTemplateIndex(1));
        Assert.Equal(-1, s.FindTemplateIndex(-1));
    }

    [Fact]
    public void An_arpeggio_bracket_comes_from_the_template_with_that_id()
    {
        // Ids 3 and 7 (the ones between were deleted): position 1 is NOT id 1.
        var arpeggioLast = Highway();
        SetTemplates(arpeggioLast, Template(3, "C", false), Template(7, "G", true));
        var plain = Highway();
        SetTemplates(plain, Template(3, "C", false), Template(7, "G", false));

        // Before the fix id 7 was out of range, so the bracket vanished.
        Assert.True(BracketFloats(arpeggioLast, 7) > BracketFloats(plain, 7));
        // No template carries id 0: nothing is drawn, whatever sits at position 0.
        Assert.Equal(0, BracketFloats(arpeggioLast, 0));
    }

    [Fact]
    public void A_chord_name_comes_from_the_template_with_that_id()
    {
        var s = Highway();
        SetTemplates(s, Template(3, "C", false), Template(7, "G", false));
        var context = Context(new QuadBatcher(null!), s, 0);

        Assert.True(ChordNameRenderer.TryLabel(context, 7, 1000, out var name, out _));
        Assert.Equal("G", name);
        Assert.False(ChordNameRenderer.TryLabel(context, 1, 1000, out _, out _));
    }

    // ---- 3: a template-less shape does not suppress fingering --------------

    [Fact]
    public void A_template_less_hand_shape_does_not_suppress_a_later_chord_grip()
    {
        var s = Highway();
        SetTemplates(s, Template(0, "C", false));
        s.HandShapes = [new RenderHandShape { Time = 0, EndTime = 5000, TemplateId = -1 }];
        s.HandShapeCount = 1;
        s.Chords = [new RenderChord { Time = 1000, TemplateId = 0 }];
        s.ChordCount = 1;

        Assert.True(FingeringRenderer.TrySelectGrip(s, 500, out var templateId, out var startMs));
        Assert.Equal(0, templateId);
        Assert.Equal(1000, startMs);
    }

    // ---- 4: chord heads are staged by lane ---------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chord_heads_are_staged_lowest_lane_first_so_the_highest_lane_wins_an_overlap(bool invert)
    {
        var s = Highway();
        s.InvertStrings = invert;
        s.Chords = [new RenderChord { Time = 1500, TemplateId = -1, NoteOffset = 0, NoteCount = 3 }];
        s.ChordCount = 1;
        s.ChordNotes =
        [
            new RenderChordNote { StringIndex = 0, Fret = 5 },
            new RenderChordNote { StringIndex = 1, Fret = 5 },
            new RenderChordNote { StringIndex = 2, Fret = 5 }
        ];
        s.ChordNoteCount = 3;
        var staged = Stage(b => ChordRenderer.RenderChord(Context(b, s, 0), new RenderResources(), 0));

        var ys = new List<float>();
        for (var q = 0; q + QuadFloats <= staged.Length; q += QuadFloats)
        {
            if (staged[q + 12] != 3.0f) continue; // the note-head quads
            ys.Add((staged[q + 1] + staged[q + Floats + 1] + staged[q + 2 * Floats + 1] + staged[q + 3 * Floats + 1]) / 4f);
        }

        Assert.Equal(3, ys.Count);
        Assert.True(ys[0] < ys[1] && ys[1] < ys[2], $"heads staged in the order {string.Join(", ", ys)}");
    }

    // ---- fixtures ----------------------------------------------------------

    private static float[] Stage(Action<QuadBatcher> draw)
    {
        var batcher = new QuadBatcher(null!);
        draw(batcher);
        return batcher.StagedVertices.ToArray();
    }

    private static int BracketFloats(FrameSnapshot s, int templateId)
    {
        s.HandShapes = [new RenderHandShape { Time = 1500, EndTime = 2500, TemplateId = templateId }];
        s.HandShapeCount = 1;
        var batcher = new QuadBatcher(null!);
        HandShapeRenderer.RenderHandShape(Context(batcher, s, 0), 0);
        return batcher.StagedFloatCount;
    }

    private static void SetTemplates(FrameSnapshot s, params RenderChordTemplate[] templates)
    {
        s.Templates = templates;
        s.TemplateCount = templates.Length;
    }

    private static RenderChordTemplate Template(int id, string name, bool arpeggio)
    {
        var t = new RenderChordTemplate { Id = id, Name = name, Arpeggio = arpeggio };
        for (var i = 0; i < 8; i++)
        {
            t.SetFret(i, i is 1 or 2 ? 3 : -1);
            t.SetFinger(i, i is 1 or 2 ? 1 : -1);
        }

        return t;
    }

    private static FrameSnapshot Highway() => new()
    {
        StringCount = 6,
        Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 1, Width = 4 }],
        AnchorCount = 1,
        Templates = [],
        Chords = [],
        ChordNotes = [],
        HandShapes = [],
        Notes = []
    };

    private static RenderContext Context(QuadBatcher batcher, FrameSnapshot snapshot, double timeMs) => new()
    {
        Snapshot = snapshot,
        TimeMs = timeMs,
        BoardWidth = 10f,
        QuadBatcher = batcher,
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
    };
}
